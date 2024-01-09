/* ****************************************************************************
 *
 * Copyright (c) Microsoft Corporation. 
 *
 * This source code is subject to terms and conditions of the Apache License, Version 2.0. A 
 * copy of the license can be found in the License.html file at the root of this distribution. If 
 * you cannot locate the  Apache License, Version 2.0, please send an email to 
 * ironruby@microsoft.com. By using this source code in any fashion, you are agreeing to be bound 
 * by the terms of the Apache License, Version 2.0.
 *
 * You must not remove this notice, or any other, from this software.
 *
 *
 * ***************************************************************************/

using System;
using System.Buffers;
using System.Diagnostics;
using System.IO;
using IronRuby.Runtime;
using Microsoft.Scripting.Utils;
using System.Collections.Generic;

namespace IronRuby.Builtins {
    #if NET472_OR_GREATER
    internal static class StreamExtensions
    {
        public static int Read(this Stream thisStream, Span<byte> buffer)
        {
            byte[] sharedBuffer = ArrayPool<byte>.Shared.Rent(buffer.Length);
            try
            {
                int numRead = thisStream.Read(sharedBuffer, 0, buffer.Length);
                if ((uint)numRead > (uint)buffer.Length)
                {
                    throw new IOException("Stream is too long!");
                }
                new Span<byte>(sharedBuffer, 0, numRead).CopyTo(buffer);
                return numRead;
            }
            finally { ArrayPool<byte>.Shared.Return(sharedBuffer); }
        }
        
        public static void Write(this Stream thisStream, ReadOnlySpan<byte> buffer)
        {
            byte[] sharedBuffer = ArrayPool<byte>.Shared.Rent(buffer.Length);
            try
            {
                buffer.CopyTo(sharedBuffer);
                thisStream.Write(sharedBuffer, 0, buffer.Length);
            }
            finally { ArrayPool<byte>.Shared.Return(sharedBuffer); }
        }
    }
#endif
    
    /// <summary>
    /// Not thread-safe.
    /// </summary>
    ///
    public class RubyBufferedStream : Stream {
        private readonly Stream/*!*/ _stream;

        // read buffer [...xxxxxxx...]
        //                 ^      ^
        //           read pos     stream pos
        #if NET472_OR_GREATER
        private byte[] _buffer;
        #else 
        private IMemoryOwner<byte> _bufferMemory;
        #endif
        
        private int _defaultBufferSize;

        // the position of the first buffered byte in buffer
        private int _bufferStart;          

        // the number of buffered bytes
        private int _bufferCount;

        // the number of bytes pushed back by ungetc:
        private int _pushedBackCount;

        private bool _pushBackPreservesPosition;

        private const byte CR = (byte)'\r';
        private const byte LF = (byte)'\n';

        public RubyBufferedStream(Stream/*!*/ stream)
            : this(stream, false) {
        }
        
        public RubyBufferedStream(Stream/*!*/ stream, bool pushBackPreservesPosition)
            : this(stream, pushBackPreservesPosition, 0x1000) {
        }

        public RubyBufferedStream(Stream/*!*/ stream, bool pushBackPreservesPosition, int bufferSize) {
            ContractUtils.RequiresNotNull(stream, "stream");
            ContractUtils.Requires(bufferSize > 0, "bufferSize", "Buffer size must be positive.");
            _stream = stream;
            _defaultBufferSize = bufferSize;
            _pushBackPreservesPosition = pushBackPreservesPosition;
        }

        public Stream/*!*/ BaseStream {
            get { return _stream; }
        }

        public bool DataBuffered {
            get { return _bufferCount > 0; }
        }

#if NET6_0_OR_GREATER
        private int LoadBuffer(int count) {
            if (_bufferMemory == null)
            {
                Debug.Assert(_bufferCount == 0 && _bufferStart == 0);
                // create a new buffer
                _bufferMemory = MemoryPool<byte>.Shared.Rent(_defaultBufferSize);
            }
            Debug.Assert(_bufferCount + count <= _bufferMemory.Memory.Length);
            
            var memory = _bufferMemory.Memory;
            if (_bufferStart + _bufferCount + count > memory.Length)
            {
                // Shift left efficiently using Memory<byte>:
                memory[_bufferStart.._bufferCount].CopyTo(memory[.._bufferCount]);
                _bufferStart = 0;
            }

            // Read directly into the Memory<byte> buffer:
            var bytesRead = _stream.Read(memory.Span[_bufferCount..]);
            _bufferCount += bytesRead;
            return bytesRead;
        }
#else
        private int LoadBuffer(int count) {
            Debug.Assert(_bufferCount + count <= (_buffer != null ? _buffer.Length : _defaultBufferSize));

            int bytesRead;
            if (_buffer == null) {
                Debug.Assert(_bufferCount == 0 && _bufferStart == 0);
                _buffer = new byte[_defaultBufferSize];
            } else if (_bufferStart + _bufferCount + count > _buffer.Length) {
                // shift left:
                Buffer.BlockCopy(_buffer, _bufferStart, _buffer, 0, _bufferCount);
                _bufferStart = 0;
            }

            bytesRead = _stream.Read(_buffer, _bufferCount, count);
            _bufferCount += bytesRead;
            return bytesRead;
        }
#endif
        private void ConsumeBuffered(int count) {
            _bufferCount -= count;
            _pushedBackCount -= Math.Min(_pushedBackCount, count);
            if (_bufferCount == 0) {
                _bufferStart = 0;
            } else {
                _bufferStart += count;
            }
        }
        
        private int ReadAheadCount {
            get { return _bufferCount - _pushedBackCount; }
        }

        #if NET6_0_OR_GREATER
        public void PushBack(byte b) {
            //for .NET 6, we can use the Memory<byte> buffer
            _bufferMemory ??= MemoryPool<byte>.Shared.Rent(_defaultBufferSize);
            var span = _bufferMemory.Memory.Span;
            
            if (_bufferStart > 0) {
                span[--_bufferStart] = b;
            } else {
                // we need to check if the buffer is full and if so, resize it
                if (_bufferCount == _bufferMemory.Memory.Length)
                {
                    var newBuffer = MemoryPool<byte>.Shared.Rent(Math.Max(_defaultBufferSize, _bufferCount * 2));
                    // first, put the pushed back byte into the new buffer
                    newBuffer.Memory.Span[0] = b;
                    // then copy the old buffer into the new one
                    _bufferMemory.Memory.Span.CopyTo(newBuffer.Memory.Span[1..]);
                    _bufferMemory.Dispose();
                    _bufferMemory = newBuffer;
                }
                else
                {
                    // we can just shift the buffer to the right by one
                    span.Slice(1, _bufferCount).CopyTo(span);
                    span[0] = b;
                }
            } 
            
            _pushedBackCount++;
            _bufferCount++;
        }
        #else
        public void PushBack(byte b) {
            if (_bufferStart > 0) {
                _buffer[--_bufferStart] = b;
            } else if (_buffer != null) {
                Utils.InsertAt(ref _buffer, _bufferCount, 0, b, 1);
            } else
            {
                _buffer = new byte[_defaultBufferSize];
                _buffer[0] = b;
            }
            _pushedBackCount++;
            _bufferCount++;
        }
        #endif
        
        public override long Position {
            get {
                // TODO: this seems to be bug in MRI: you can read(0); ungetc(x); at the beginning of a stream, yet
                // you can't do ungetc(x) at the beginning of the stream.
                // (see http://redmine.ruby-lang.org/issues/show/1909)
                if (_pushBackPreservesPosition) {
                    return _stream.Position - ReadAheadCount;
                } else {
                    return Math.Max(_stream.Position - _bufferCount, 0);
                }
            }
            set {
                ContractUtils.Requires(value >= 0, "value", "Value must be positive");
                Seek(value, SeekOrigin.Begin);
            }
        }

        #if NET6_0_OR_GREATER
        protected override void Dispose(bool disposing) {
            if (disposing) {
                //release the buffer back to the pool
                if (_bufferMemory != null)
                {
                    _bufferMemory.Dispose();
                    _bufferMemory = null;
                }
                _bufferCount = _bufferStart = _pushedBackCount = 0;
                _stream.Dispose();
            }

            base.Dispose(disposing);
        }
        #else
        protected override void Dispose(bool disposing) {
            if (disposing) {
                _buffer = null;
                _bufferCount = _bufferStart = _pushedBackCount = 0;
                _stream.Dispose();
            }

            base.Dispose(disposing);
        }
        #endif
        public override long Seek(long pos, SeekOrigin origin) {
            if (origin == SeekOrigin.Current) {
                if (_pushBackPreservesPosition) {
                    pos -= ReadAheadCount;
                } else {
                    origin = SeekOrigin.Begin;
                    pos += Position;
                }
            }

            // try seek first, it may fail and we shouldn't change the buffer if so:
            var result = _stream.Seek(pos, origin);

            // TODO: we might keep the buffered data if we seek within the buffered data (but not in pushed back data):
            // clear any buffer content (including ungetc):
            _bufferStart = _bufferCount = _pushedBackCount = 0;

            return result;
        }

        private void FlushRead() {
            // unwind cached data:
            if (ReadAheadCount > 0) {
                Seek(-ReadAheadCount, SeekOrigin.Current);
            }

            _bufferStart = _bufferCount = _pushedBackCount = 0;
        }

        public override void Write(byte[]/*!*/ buffer, int offset, int count) {
            FlushRead();

            _stream.Write(buffer, offset, count);
        }

        public int WriteBytes(MutableString/*!*/ buffer, int offset, int count, bool preserveEndOfLines) {
            // TODO: this is not safe, we are passing an internal pointer to the byte[] content of MutableString to the Stream:
            return WriteBytes(buffer.SwitchToBytes().GetByteArrayChecked(offset, count), offset, count, preserveEndOfLines);
        }

        public int WriteBytes(byte[]/*!*/ buffer, int offset, int count, bool preserveEndOfLines) {
            ContractUtils.RequiresArrayRange(buffer.Length, offset, count, "offset", "count");
            FlushRead();

            if (preserveEndOfLines) {
                _stream.Write(buffer, offset, count);
                return count;
            } else {
                int bytesWritten = 0;
                int i = offset;
                int end = offset + count;
                while (i < end) {
                    int j = i;
                    while (j < end && buffer[j] != LF) {
                        j++;
                    }
                    _stream.Write(buffer, i, j - i);
                    bytesWritten += j - i;

                    if (j < end) {
                        _stream.WriteByte(CR);
                        _stream.WriteByte(LF);
                        bytesWritten += 2;
                    }

                    i = j + 1;
                }

                return bytesWritten;
            }
        }

        public int PeekByte() {
            return PeekByte(0);
        }

        #if NET6_0_OR_GREATER
        private int PeekByte(int i) {
            Debug.Assert(i < (_bufferMemory != null ? _bufferMemory.Memory.Length : _defaultBufferSize));

            if (i >= _bufferCount) {
                LoadBuffer(i + 1 - _bufferCount);
            }

            // end of stream:
            if (i >= _bufferCount) {
                return -1;
            }

            return _bufferMemory.Memory.Span[_bufferStart + i];
        }
        
        private byte ReadBufferByte() {
            Debug.Assert(_bufferCount > 0);
            var result = _bufferMemory.Memory.Span[_bufferStart];
            ConsumeBuffered(1);
            return result;
        }
        #else
        /// <summary>
        /// Peeks i-th byte. Assumes small <c>i</c>.
        /// </summary>
        private int PeekByte(int i) {
            Debug.Assert(i < (_buffer != null ? _buffer.Length : _defaultBufferSize));

            if (i >= _bufferCount) {
                LoadBuffer(i + 1 - _bufferCount);
            }

            // end of stream:
            if (i >= _bufferCount) {
                return -1;
            }

            return _buffer[_bufferStart + i];
        }
        
        private byte ReadBufferByte() {
            Debug.Assert(_bufferCount > 0);
            var result = _buffer[_bufferStart];
            ConsumeBuffered(1);
            return result;
        }
        #endif
        

        // TODO: read in full buffer (underlying FileStream will buffer it anyways)
        public override int ReadByte() {
            return (_bufferCount > 0) ? ReadBufferByte() : _stream.ReadByte();
        }

        #if NET6_0_OR_GREATER
        public override int Read(Span<byte> buffer) {
            if (_bufferCount == 0) {
                return _stream.Read(buffer);
            }

            var bytesRead = Math.Min(buffer.Length, _bufferCount);
            _bufferMemory.Memory.Span[_bufferStart.._bufferCount].CopyTo(buffer);
            ConsumeBuffered(bytesRead);
            return bytesRead;
        }
        
        // also need to give a way for existing code to read from the memory buffer
        // into a byte[] buffer
        public override int Read(byte[] buffer, int offset, int count) {
            if (_bufferCount == 0) {
                return _stream.Read(buffer, offset, count);
            }

            var bytesRead = Math.Min(count, _bufferCount);
            _bufferMemory.Memory.Span[_bufferStart.._bufferCount].CopyTo(buffer.AsSpan(offset, bytesRead));
            ConsumeBuffered(bytesRead);
            return bytesRead;
        }
        #else
        public override int Read(byte[]/*!*/ buffer, int offset, int count) {
            int c = Math.Min(_bufferCount, count);
            if (c > 0) {
                Buffer.BlockCopy(_buffer, _bufferStart, buffer, offset, c);
                ConsumeBuffered(c);
            }
            return c + _stream.Read(buffer, offset + c, count - c);
        }
#endif
        /// <summary>
        /// Reads <paramref name="count"/> bytes from the stream and appends them to the given <paramref name="buffer"/>.
        /// If <paramref name="count"/> is <c>Int32.MaxValue</c> the stream is read to the end.
        /// Unless <paramref name="preserveEndOfLines"/> is set the line endings in the appended data are normalized to "\n".
        /// </summary>
        public int AppendBytes(MutableString/*!*/ buffer, int count, bool preserveEndOfLines) {
            ContractUtils.RequiresNotNull(buffer, "buffer");
            ContractUtils.Requires(count >= 0, "count");

            if (count == 0) {
                return 0;
            }

            bool readAll = count == Int32.MaxValue;

            buffer.SwitchToBytes();
            int initialBufferSize = buffer.GetByteCount();
            if (preserveEndOfLines) {
                AppendRawBytes(buffer, count);
            } else {
                // allocate 3 more bytes at the end for a backstop and possible LF:
                byte[] bytes = Utils.EmptyBytes;

                int done = initialBufferSize;
                bool eof;
                do {
                    AppendRawBytes(buffer, readAll ? 1024 : count);
                    int end = buffer.GetByteCount();
                    int bytesRead = end - done;
                    if (bytesRead == 0) {
                        break;
                    }

                    eof = bytesRead < count;

                    buffer.EnsureCapacity(end + 3);
                    int byteCount;
                    bytes = buffer.GetByteArray(out byteCount);

                    if (bytes[end - 1] == CR && PeekByte(0) == LF) {
                        ReadByte();
                        bytes[end++] = LF;
                    }

                    // insert backstop:
                    bytes[end] = CR;
                    bytes[end + 1] = LF;

                    int last = IndexOfCrLf(bytes, done);
                    count -= last - done;
                    done = last;
                    while (last < end) {
                        int next = IndexOfCrLf(bytes, last + 2);
                        int chunk = next - last - 1;
                        Buffer.BlockCopy(bytes, last + 1, bytes, done, chunk);
                        done += chunk;
                        count -= chunk;
                        last = next;
                    }
                    buffer.Remove(done);
                } while (readAll || count > 0 && !eof);
            }

            if (readAll) {
                buffer.TrimExcess();
            }

            return buffer.GetByteCount() - initialBufferSize;
        }

        #if NET6_0_OR_GREATER
        private void AppendRawBytes(MutableString/*!*/ buffer, int count) {
            Debug.Assert(count > 0);

            int remaining = count;

            if (_bufferCount > 0) {
                int c = Math.Min(_bufferCount, count);
                buffer.Append(_bufferMemory.Memory.Span[_bufferStart.._bufferCount]);
                ConsumeBuffered(c);
                remaining -= c;
            }

            if (count == Int32.MaxValue) {
                const int chunk = 1024;

                int done = buffer.GetByteCount();
                int bytesRead;
                do {
                    buffer.Append(_stream, chunk);
                    bytesRead = buffer.GetByteCount() - done;
                    done += bytesRead;
                } while (bytesRead == chunk);
            } else {
                buffer.Append(_stream, remaining);
            }
        }
        #else
        private void AppendRawBytes(MutableString/*!*/ buffer, int count) {
            Debug.Assert(count > 0);

            int remaining = count;

            if (_bufferCount > 0) {
                int c = Math.Min(_bufferCount, count);
                buffer.Append(_buffer, _bufferStart, c);
                ConsumeBuffered(c);
                remaining -= c;
            }

            if (count == Int32.MaxValue) {
                const int chunk = 1024;

                int done = buffer.GetByteCount();
                int bytesRead;
                do {
                    buffer.Append(_stream, chunk);
                    bytesRead = buffer.GetByteCount() - done;
                    done += bytesRead;
                } while (bytesRead == chunk);
            } else {
                buffer.Append(_stream, remaining);
            }
        }
        #endif
        private static int IndexOfCrLf(byte[]/*!*/ array, int i) {
            while (true) {
                if (array[i++] == CR && array[i] == LF) {
                    return i - 1;
                }
            }
        }

        public int ReadByteNormalizeEoln(bool preserveEndOfLines) {
            int first = ReadByte();
            if (first == '\r' && !preserveEndOfLines) {
                int second = PeekByte(0);
                if (second == '\n') {
                    return ReadByte();
                }
            }

            return first;
        }

        public int PeekByteNormalizeEoln(bool preserveEndOfLines) {
            int first = PeekByte(0);
            if (first == -1) {
                return -1;
            }

            if (first == '\r' && !preserveEndOfLines && PeekByte(1) == '\n') {
                return '\n';
            }

            return first;
        }

        public MutableString ReadLineOrParagraph(MutableString separator, RubyEncoding/*!*/ encoding, bool preserveEndOfLines, int limit) {
            ContractUtils.Requires(limit >= 0);

            if (limit == 0) {
                return MutableString.CreateEmpty();
            } else if (separator == null) {
                var result = MutableString.CreateBinary();
                return AppendBytes(result, limit, preserveEndOfLines) == 0 ? null : result;
            } else if (separator.StartsWith('\n') && separator.GetLength() == 1) {
                return ReadLine(encoding, preserveEndOfLines, limit);
            } else if (separator.IsEmpty) {
                return ReadParagraph(encoding, preserveEndOfLines, limit);
            } else {
                return ReadLine(separator, encoding, preserveEndOfLines, limit);
            }
        }

        #if NET6_0_OR_GREATER
        public MutableString ReadLine(RubyEncoding/*!*/ encoding, bool preserveEndOfLines, int limit)
        {
            if (_bufferCount == 0)
            {
                if (LoadBuffer(_defaultBufferSize) == 0)
                {
                    return null;
                }
            }
            
            bool bufferResized = false;
            var window = _bufferMemory.Memory.Span[_bufferStart.._bufferCount];
            var lf = window.IndexOf(LF);
            while (lf < 0)
            {
                var s = _bufferCount;
                LoadBuffer(_bufferMemory.Memory.Length - _bufferCount);
                Debug.Assert(_bufferStart == 0);

                lf = window.Slice(s, _bufferCount - s).IndexOf(LF);
                if (lf >= 0)
                {
                    break;
                }

                // end of stream:
                if (_bufferCount < _bufferMemory.Memory.Length)
                {
                    return ConsumeLine(encoding, _bufferCount, _bufferCount, bufferResized);
                }

                var newBuffer = MemoryPool<byte>.Shared.Rent(_bufferMemory.Memory.Length * 2);
                _bufferMemory.Memory.CopyTo(newBuffer.Memory);
                _bufferMemory.Dispose();
                _bufferMemory = newBuffer;
                bufferResized = true;
                _bufferStart = 0;
            }
            
            int lineLength;
            int consume = lf + 1 - _bufferStart;
            if (!preserveEndOfLines && lf - 1 >= _bufferStart && window[lf - 1] == CR)
            {
                window[lf - 1] = LF;
                lineLength = consume - 1;
            }
            else
            {
                lineLength = consume;
            }
            
            return ConsumeLine(encoding, lineLength, consume, bufferResized);
        }
        #else
        public MutableString ReadLine(RubyEncoding/*!*/ encoding, bool preserveEndOfLines, int limit) {
            // TODO: limit

            if (_bufferCount == 0) {
                if (LoadBuffer(_defaultBufferSize) == 0) {
                    return null;
                }
            }

            bool bufferResized = false;
            int lf = Array.IndexOf(_buffer, LF, _bufferStart, _bufferCount);
            while (lf < 0) {
                int s = _bufferCount;
                LoadBuffer(_buffer.Length - _bufferCount);
                Debug.Assert(_bufferStart == 0);

                lf = Array.IndexOf(_buffer, LF, s, _bufferCount - s);
                if (lf >= 0) {
                    break;
                }

                // end of stream:
                if (_bufferCount < _buffer.Length) {
                    return ConsumeLine(encoding, _bufferCount, _bufferCount, bufferResized);
                }

                Array.Resize(ref _buffer, _buffer.Length << 1);
                bufferResized = true;
                _bufferStart = 0;
            }

            int lineLength;
            int consume = lf + 1 - _bufferStart;
            if (!preserveEndOfLines && lf - 1 >= _bufferStart && _buffer[lf - 1] == CR) {
                _buffer[lf - 1] = LF;
                lineLength = consume - 1;
            } else {
                lineLength = consume;
            }

            return ConsumeLine(encoding, lineLength, consume, bufferResized);
        }
#endif 
        #if NET6_0_OR_GREATER
        public MutableString/*!*/ ConsumeLine(RubyEncoding/*!*/ encoding, int lineLength, int consume, bool bufferResized) {
            Debug.Assert(consume >= lineLength);
            Debug.Assert(consume <= _bufferCount);

            MutableString line;
            if (bufferResized || _bufferStart == 0 && !Utils.IsSparse(lineLength, _bufferMemory.Memory.Length)) {
                Debug.Assert(_bufferStart == 0);
                line = new MutableString(_bufferMemory.Memory.Slice(0, lineLength).Span, encoding);

                if (_bufferCount > consume)
                {
                    var newBuffer = MemoryPool<byte>.Shared.Rent(Math.Max(_defaultBufferSize, _bufferCount - consume));
                    _bufferMemory.Memory.Slice(consume).CopyTo(newBuffer.Memory);
                    _bufferMemory.Dispose();
                    _bufferMemory = newBuffer;
                } else {
                    _bufferMemory.Dispose();
                    _bufferMemory = null;
                }

                // consume as if we kept the same buffer and then adjust start:
                ConsumeBuffered(consume);
                _bufferStart = 0;
            } else {
                line = MutableString.CreateBinary(encoding).Append(_bufferMemory.Memory.Slice(_bufferStart, lineLength).Span);
                ConsumeBuffered(consume);
            }
            return line;
        }
        #else
        private MutableString/*!*/ ConsumeLine(RubyEncoding/*!*/ encoding, int lineLength, int consume, bool bufferResized) {
            Debug.Assert(consume >= lineLength);
            Debug.Assert(consume <= _bufferCount);

            MutableString line;
            if (bufferResized || _bufferStart == 0 && !Utils.IsSparse(lineLength, _buffer.Length)) {
                Debug.Assert(_bufferStart == 0);
                line = new MutableString(_buffer, lineLength, encoding);

                if (_bufferCount > consume)
                {
                    var newBuffer = ArrayPool<byte>.Shared.Rent(Math.Max(_defaultBufferSize, _bufferCount - consume));
                    Buffer.BlockCopy(_buffer, consume, newBuffer, 0, _bufferCount - consume);
                    _buffer = newBuffer;
                } else {
                    ArrayPool<byte>.Shared.Return(_buffer);
                    _buffer = null;
                }

                // consume as if we kept the same buffer and then adjust start:
                ConsumeBuffered(consume);
                _bufferStart = 0;
            } else {
                line = MutableString.CreateBinary(encoding).Append(_buffer, _bufferStart, lineLength);
                ConsumeBuffered(consume);
            }
            return line;
        }
#endif
        public MutableString ReadParagraph(RubyEncoding/*!*/ encoding, bool preserveEndOfLines, int limit) {
            // TODO: limit

            var result = ReadLine(MutableString.CreateAscii("\n\n"), encoding, preserveEndOfLines, limit);

            int c;
            while ((c = PeekByteNormalizeEoln(preserveEndOfLines)) != -1) {
                if (c != '\n') {
                    break;
                }
                ReadByteNormalizeEoln(preserveEndOfLines);
            }

            return result;
        }

        public MutableString ReadLine(MutableString/*!*/ separator, RubyEncoding/*!*/ encoding, bool preserveEndOfLines, int limit) {
            // TODO: limit

            int b = ReadByteNormalizeEoln(preserveEndOfLines);
            if (b == -1) {
                return null;
            }

            int separatorOffset = 0;
            int separatorLength = separator.GetByteCount();
            MutableString result = MutableString.CreateBinary(encoding);

            do {
                result.Append((byte)b);

                if (b == separator.GetByte(separatorOffset)) {
                    if (separatorOffset == separatorLength - 1) {
                        break;
                    }
                    separatorOffset++;
                } else if (separatorOffset > 0) {
                    separatorOffset = 0;
                }

                b = ReadByteNormalizeEoln(preserveEndOfLines);
            } while (b != -1);

            return result;
        }

        public override bool CanRead {
            get { return _stream.CanRead; }
        }

        public override bool CanSeek {
            get { return _stream.CanSeek; }
        }

        public override bool CanWrite {
            get { return _stream.CanWrite; }
        }

        public override void Flush() {
            FlushRead();
            _stream.Flush();
        }

        public override long Length {
            get { return _stream.Length; }
        }

        public override void SetLength(long value) {
            _stream.SetLength(value);
        }
    }
}

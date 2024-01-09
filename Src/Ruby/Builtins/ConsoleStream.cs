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
using System.IO;
using System.IO.Pipelines;
using Microsoft.Scripting.Utils;

namespace IronRuby.Builtins
{
    
    internal sealed class ConsoleStream : Stream, IDisposable
    {
        private readonly ConsoleStreamType _consoleType;
        private readonly BinaryReader _reader;
        private readonly BinaryWriter _writer;
        
        public ConsoleStream(ConsoleStreamType consoleType)
        {
            _consoleType = consoleType;
            (_reader, _writer) = consoleType switch
            {
                ConsoleStreamType.Input       => (new BinaryReader(Console.OpenStandardInput()), null),
                ConsoleStreamType.Output      => (null, new BinaryWriter(Console.OpenStandardOutput())),
                ConsoleStreamType.ErrorOutput => (null as BinaryReader, 
                                                  new BinaryWriter(Console.OpenStandardError())),
                _ => throw new ArgumentException("Invalid console type", nameof(consoleType))
            };
        }

        public ConsoleStreamType StreamType  => _consoleType; 
            
        public override bool CanRead => _consoleType == ConsoleStreamType.Input;

        public override bool CanSeek => false;

        public override bool CanWrite => _consoleType != ConsoleStreamType.Input;

        public override void Flush()
        {
            if (_writer != null)
            {
                _writer.Flush();
            }
        }
        
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_reader == null)
            {
                throw new InvalidOperationException("Cannot read from output or error streams");
            }
            return _reader.Read(buffer, offset, count);
        }
        
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (_writer == null)
            {
                throw new InvalidOperationException("Cannot write to input stream");
            }

            _writer.Write(buffer, offset, count);
        }

#if NET6_0_OR_GREATER
        public int Read(Span<byte> buffer)
        {
            if (_reader == null)
            {
                throw new InvalidOperationException("Cannot read from output or error streams");
            }

            return _reader.Read(buffer);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (_writer == null)
            {
                throw new InvalidOperationException("Cannot write to input stream");
            }

            _writer.Write(buffer);
        }
#endif 
        public new void Dispose()
        {
            _reader?.Dispose();
            _writer?.Dispose();
            base.Dispose();
        }
        
    }
}

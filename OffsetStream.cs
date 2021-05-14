// Author:
// Evan Thomas Olds
//
// Original creation date:
// Unknown
//
// Alteration date:
// November 23, 2014 (moved to ETOF.IO and fixed up some things)

using System;
using System.IO;

namespace ETOF.IO
{
    internal sealed class OffsetStream : Stream
    {
        private readonly Stream r_base;

        /// <summary>
        /// Offset in bytes, counting from the beginning of the stream. Cannot be negative.
        /// </summary>
        private readonly long m_offset;

        /// <summary>
        /// Initializes the offset stream using the stream's current position as the offset.
        /// </summary>
		public OffsetStream(Stream underlyingStream)
            : this(underlyingStream, underlyingStream.Position) { }

        public OffsetStream(Stream underlyingStream, long offsetInBytes)
        {
            if (offsetInBytes < 0)
            {
                throw new ArgumentException();
            }
            
            r_base = underlyingStream;
            m_offset = offsetInBytes;
            r_base.Position = offsetInBytes;
        }

        public override bool CanRead
        {
            get { return r_base.CanRead; }
        }

        public override bool CanSeek
        {
            get { return r_base.CanSeek; }
        }

        public override bool CanWrite
        {
            get { return r_base.CanWrite; }
        }

        public override void Flush()
        {
            r_base.Flush();
        }

        public override long Length
        {
            get
            {
                return r_base.Length - m_offset;
            }
        }

        public override long Position
        {
            get
            {
                return r_base.Position - m_offset;
            }
            set
            {
                // We ignore anything less than 0
                if (value >= 0)
                {
                    // If the position cannot be set then the underlying stream will throw 
                    // an appropriate exception.
                    r_base.Position = m_offset + value;
                }
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            return r_base.Read(buffer, offset, count);
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            switch (origin)
            {
                case SeekOrigin.Begin:
                    Position = offset;
                    break;

                case SeekOrigin.Current:
                    Position += offset;
                    break;

                case SeekOrigin.End:
                    Position = r_base.Length - offset;
                    break;

                default:
                    throw new InvalidOperationException();
            }
            return Position;
        }

        public override void SetLength(long value)
        {
            r_base.SetLength(value + m_offset);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            r_base.Write(buffer, offset, count);
        }
    }
}

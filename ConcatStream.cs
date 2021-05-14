// Author:
// Evan Thomas Olds
//
// Creation Date:
// November 23, 2014

using System;
using System.IO;

namespace ETOF.IO
{
    /// <summary>
    /// Simulates the concatenation of two streams together. The two streams are NOT actually 
    /// concatenated in memory. The ConcatStream keeps references to both underlying streams 
    /// and dynamically serves their functionality as if there were just one large stream. 
    /// The functionality of the ConcatStream is limited by the lesser-functional of the two 
    /// underlying streams. It can seek only if BOTH underlying streams can seek, it can 
    /// write only if BOTH underlying streams can write, and so on.
    /// </summary>
    public class ConcatStream : Stream
    {
        private Stream m_a, m_b;

        /// <summary>
        /// Only used when the second stream (stream B) does not support seeking. Keeps track 
        /// of the position that we are in within stream B. Works under the assumption that 
        /// on construction we are at position 0 in B.
        /// </summary>
        private long m_bPos = 0;

        private long m_pos = 0;

		/// <summary>
		/// The fixed stream length or -1 if the stream length is not to be fixed at a particular 
		/// value.
		/// </summary>
		private long r_len = -1;

        /// <summary>
        /// Constructs the ConcatStream from two streams. Both streams must be non-null. The 
        /// first stream must support seeking and querying the Length property.
        /// </summary>
        public ConcatStream(Stream first, Stream second)
        {
            if (null == first || null == second)
            {
                throw new ArgumentNullException();
            }

			if (!first.CanSeek)
			{
				throw new ArgumentException(
					"First stream in a ConcatStream must support seeking");
			}
            
            m_a = first;
            m_b = second;
            m_a.Position = 0;
        }

		/// <summary>
		/// Constructs the ConcatStream from two streams and a fixed length. Both streams must be 
		/// non-null. The first stream must support seeking and querying the Length property. The 
		/// stream will be read-only.
		/// </summary>
		private ConcatStream(Stream first, Stream second, long fixedLength)
		{
			if (null == first || null == second)
			{
				throw new ArgumentNullException();
			}

			if (!first.CanSeek)
			{
				throw new ArgumentException(
					"First stream in a ConcatStream must support seeking");	}

			if (!first.CanRead || !second.CanRead)
			{
				throw new ArgumentException(
					"Cannot construct a fixed-length, read-only ConcatStream unless " + 
					"both streams support reading.");
			}

			m_a = first;
			m_b = second;
			m_a.Position = 0;
			r_len = fixedLength;
		}

        public override bool CanRead
        {
            // We can read only if BOTH underlying streams can read
            get { return m_a.CanRead && m_b.CanRead; }
        }

        public override bool CanSeek
        {
            // We can seek only if BOTH underlying streams can seek
            get { return m_a.CanSeek && m_b.CanSeek; }
        }

        public override bool CanWrite
        {
            get
			{
				if (r_len >= 0)
				{
					// This means it's fixed-length and read-only
					return false;
				}

				// We can write only if BOTH underlying streams can write
				return m_a.CanWrite && m_b.CanWrite;
			}
        }

		public static ConcatStream CreateReadOnlyWithFixedLength(Stream first, Stream second, long lengthInBytes)
		{
			return new ConcatStream(first, second, lengthInBytes);
		}

        public override void Flush()
        {
            // We don't try to do anything fancy like keep track of which of the two streams 
            // have been modified, so in the case when we're asked to flush we just flush 
            // both streams.
            m_a.Flush();
            m_b.Flush();
        }

		public bool IsFixedLength
		{
			get { return (r_len >= 0); }
		}

        public override long Length
        {
            get
			{
				if (r_len >= 0)
				{
					// This means it's fixed-length and read-only
					return r_len;
				}

				return m_a.Length + m_b.Length;
			}
        }

        public override long Position
        {
            get { return m_pos; }
            set
            {
                // Take only non-negative values
                if (value >= 0)
                {
                    m_pos = value;
                    if (m_pos < m_a.Length)
                    {
                        m_a.Position = m_pos;
                    }
                    else
                    {
                        // We're somewhere in the second stream
                        m_b.Position = m_pos - m_a.Length;
                    }
                }
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            // For fixed-length streams we need to stop reading when the position == the length
			if (r_len >= 0)
			{
				if (m_pos == r_len)
				{
					// End of stream, so we return 0
					return 0;
				}

				// Lower the count if need be so we don't go over the length
				if (m_pos + count > r_len)
				{
					count = (int)(r_len - m_pos);
				}
			}

			int requestedCount = count;
            while (count > 0)
            {
                int bytesRead;
                
                // If we're in the first stream then we can read from it, passing the 
                // remaining number of bytes as the count.
                if (m_pos < m_a.Length)
                {
                    bytesRead = m_a.Read(buffer, offset, count);
                    count -= bytesRead;
                    m_pos += bytesRead;
                    offset += bytesRead;
                }
                else
                {
                    // The second stream is a bit trickier because of the fact that we want 
                    // to support non-seekable streams. We'll start with the easy case 
                    // though, where the second stream DOES support seeking.
                    if (m_b.CanSeek)
                    {
                        m_b.Position = m_pos - m_a.Length;
                    }
                    else
                    {
                        // In this case we might still be able to do the read, but we need 
                        // to verify that we are in the exactly correct position within 
                        // stream B.
                        if (m_bPos != m_pos - m_a.Length)
                        {
                            throw new NotSupportedException(
                                "Reading at location " + m_pos.ToString() + " would " +
                                "require seeking within the second stream. Since the second" +
                                " stream doesn't support seeking, this action cannot be " +
                                "completed.");
                        }
                    }

                    // We're at the correct position if we come here, so now do the read
                    bytesRead = m_b.Read(buffer, offset, count);
                    count -= bytesRead;
                    m_pos += bytesRead;
                    offset += bytesRead;
                    m_bPos += bytesRead;
                }

                // To avoid infinite loops we need to break after reading 0 bytes
                if (0 == bytesRead)
                {
                    break;
                }
            }

            return requestedCount - count;
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
                    Position = Length - offset;
                    break;

                default:
                    throw new InvalidOperationException();
            }
            return Position;
        }

        /// <summary>
        /// Not supported for the ConcatStream class.
        /// </summary>
        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

		public Stream StreamA
		{
			get { return m_a; }
		}

		public Stream StreamB
		{
			get { return m_b; }
		}

        /// <summary>
        /// The ConcatStream write functionality is such that it never expands the length of StreamA. It 
		/// can potentially overwrite existing contents in StreamA, but should it get to the end and have 
		/// more data to write, then it moves on to the beginning of StreamB.
        /// </summary>
		public override void Write(byte[] buffer, int offset, int count)
        {
			if (r_len >= 0)
			{
				// This means it's fixed-length and read-only so we cannot write
				throw new InvalidOperationException(
					"ConcatStream cannot be written to because it was instantiated as read-only.");
			}

			while (count > 0)
            {
                int bytesWritten;

                // If we're in the first stream then we can write to it, but we need to make 
                // sure that we don't go beyond the end.
                if (m_pos < m_a.Length)
                {
                    int writeCount = count;
                    if ((long)writeCount + m_pos > m_a.Length)
                    {
                        writeCount = (int)(m_a.Length - m_pos);
                    }
                    long posBefore = m_a.Position;
                    m_a.Write(buffer, offset, writeCount);
                    bytesWritten = (int)(m_a.Position - posBefore);
                    count -= bytesWritten;
                    m_pos += bytesWritten;
                    offset += bytesWritten;
                }
                else
                {
                    // The second stream is a bit trickier because of the fact that we want 
                    // to support non-seekable streams. We'll start with the easy case 
                    // though, where the second stream DOES support seeking.
                    long posBefore;
                    if (m_b.CanSeek)
                    {
                        m_b.Position = m_pos - m_a.Length;
                        posBefore = m_b.Position;
                    }
                    else
                    {
                        // In this case we might still be able to do the write, but we need 
                        // to verify that we are in the exactly correct position within 
                        // stream B.
                        if (m_bPos != m_pos - m_a.Length)
                        {
                            throw new NotSupportedException(
                                "Writing at location " + m_pos.ToString() + " would " +
                                "require seeking within the second stream. Since the second" +
                                " stream doesn't support seeking, this action cannot be " +
                                "completed.");
                        }
                        posBefore = m_bPos;
                    }

                    // We're at the correct position if we come here, so now do the write
                    m_b.Write(buffer, offset, count);

                    // Determine how many bytes were written
                    if (m_b.CanSeek)
                    {
                        bytesWritten = (int)(m_b.Position - posBefore);
                    }
                    else
                    {
                        // Assume in this case that we wrote everything
                        bytesWritten = count;
                    }

                    count -= bytesWritten;
                    m_pos += bytesWritten;
                    offset += bytesWritten;
                    m_bPos += bytesWritten;
                }

                // To avoid infinite loops we need to break after writing 0 bytes
                if (0 == bytesWritten)
                {
                    break;
                }
            }
        }
    }
}
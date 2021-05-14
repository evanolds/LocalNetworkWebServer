// Author:
// Evan Thomas Olds
//
// Creation Date:
// March 12, 2015

using System;

namespace ETOF
{
	/// <summary>
	/// Represents a byte array that can be searched for within other byte arrays.
	/// </summary>
	internal class SearchBuf
	{
		private byte[] m_buf;

		private int[] m_rightmost;

		/// <summary>
		/// Constructs the search buffer object from a buffer of byte data. If a shallow 
		/// copy of the buffer is used then it may save memory, but the buffer must remain 
		/// unchanged for the lifetime of the SearchBuf object in order to guarantee proper 
		/// search functionality. If a deep copy is made then the buffer passed to this 
		/// constructor has all its contents copied to a newly allocated internal buffer.
		/// </summary>
		public SearchBuf(byte[] buffer, bool makeDeepCopy)
		{
			if (makeDeepCopy)
			{
				m_buf = new byte[buffer.Length];
				Array.Copy(buffer, m_buf, buffer.Length);
			}
			else
			{
				m_buf = buffer;
			}

			// This is the table with the rightmost occurence of each byte value
			m_rightmost = new int[256];
			// All entries must be -1 by default
			for (int i = 0; i < m_rightmost.Length; i++)
			{
				m_rightmost[i] = -1;
			}

			// Build the table
			for (int i = 0; i < buffer.Length; i++)
			{
				m_rightmost[buffer[i]] = i;
			}
		}

		public int GetIndex(byte[] bufferToSearchWithin)
		{
			return GetIndex(bufferToSearchWithin, 0);
		}

		public int GetIndex(byte[] bufferToSearchWithin, int startIndex)
		{
			return GetIndex(bufferToSearchWithin, startIndex, bufferToSearchWithin.Length);
		}

		public int GetIndex(byte[] bufferToSearchWithin, int startIndex, int length)
		{
			if (length > bufferToSearchWithin.Length)
			{
				throw new ArgumentException();
			}

			while (startIndex + m_buf.Length <= length)
			{
				// Compare bytes right to left
				bool match = true;
				for (int i = m_buf.Length - 1; i >= 0; i--)
				{
					byte searchByte = bufferToSearchWithin[startIndex + i];
					if (m_buf[i] != searchByte)
					{
						match = false;

						// On mismatch we lookup the mismatch byte in the table
						int rightmostIndex = m_rightmost[searchByte];

						// Case 1: It's greater than i which means that we matched it in a 
						// previous comparison before hitting this mismatch. The unfortunate 
						// thing is we don't know about where any other instances of the 
						// byte value exist, so we just have to jump ahead by 1.
						if (rightmostIndex > i)
						{
							startIndex++;
						}

						// Case 2: It's less than i, which means we have to move forward to 
						// align the rightmost occurrence with the mismatch byte in what 
						// we're searching within. Note that the rightmost index could 
						// be -1 and this logic still works.
						else if (rightmostIndex < i)
						{
							startIndex += (i - rightmostIndex);
						}

						// Always break the inner loop on a mismatch
						break;
					}
				}

				if (match) { return startIndex; }
			}

			// Didn't find it if we come here
			return -1;
		}
	}
}


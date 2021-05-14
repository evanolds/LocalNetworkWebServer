// Author:
//    Evan Olds
// Creation Date:
//    March 16, 2015

using System;
using System.IO;
using System.Runtime.InteropServices;

namespace ETOF.IO
{
	public static class IOExts
	{
		public static int ReadInt32(this Stream stream)
		{
			unchecked
			{
				return (int)stream.ReadUInt32();
			}
		}

		/// <summary>
        /// Reads a 32 bit integer, using least-significant-byte-first byte order, 
        /// regardless of platform.
        /// </summary>
        public static uint ReadUInt32(this Stream stream)
		{
			byte[] bytes = new byte[4];
			stream.Read(bytes, 0, 4);
			uint value = (uint)bytes[0] | (((uint)bytes[1]) << 8) |
				(((uint)bytes[2]) << 16) | (((uint)bytes[3]) << 24);
			return value;
		}

		/// <summary>
		/// Creates a deep-copied substream that starts at the specified byte index in the 
		/// memory stream. The byte index must be greater than or equal to zero and less 
		/// than or equal to the stream length. In the case where it is exactly equal to 
		/// the stream length an empty memory stream is returned.
		/// </summary>
		public static MemoryStream SubStream(this MemoryStream ms, int startByteIndex)
		{
			if (startByteIndex < 0 || startByteIndex > ms.Length)
			{
				throw new ArgumentOutOfRangeException();
			}

			// A starting byte index equal to the length is valid, but it is a special 
			// case where we return an empty memory stream.
			if (startByteIndex == ms.Length)
			{
				return new MemoryStream();
			}

			int len = (int)(ms.Length - startByteIndex);
			MemoryStream sub = new MemoryStream(len);
			sub.Write(ms.GetBuffer(), startByteIndex, len);
			sub.Position = 0; // important
			return sub;
		}

        public static unsafe void Write(this BinaryWriter writer, void* data, int dataSizeInBytes)
        {
            // Copy the entire chunk of data to a managed array, then write it
            byte[] buf = new byte[dataSizeInBytes];
            Marshal.Copy(new IntPtr(data), buf, 0, dataSizeInBytes);
            writer.Write(buf);
        }

		public static unsafe void Write(this Stream stream, int value, bool mostSignificantByteFirst = false)
		{
			stream.Write((uint)value, mostSignificantByteFirst);
		}

		public static unsafe void Write(this Stream stream, uint value, bool mostSignificantByteFirst = false)
		{
			byte[] bytes = new byte[4];
			if (mostSignificantByteFirst)
			{
				bytes[0] = (byte)(value >> 24);
				bytes[1] = (byte)(value >> 16);
				bytes[2] = (byte)(value >> 8);
				bytes[3] = (byte)value;
			}
			else
			{
				bytes[3] = (byte)(value >> 24);
				bytes[2] = (byte)(value >> 16);
				bytes[1] = (byte)(value >> 8);
				bytes[0] = (byte)value;
			}
			stream.Write(bytes, 0, 4);
		}
	}   
}
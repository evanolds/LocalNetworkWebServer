// Author:
// Evan Thomas Olds
//
// Creation Date:
// December 10, 2016

using System;
using System.IO;
using System.IO.Compression;
using ETOF;
using ETOF.IO;

namespace ETOF.IO
{
    #if ETOF_PUBLIC
    public 
    #else
    internal 
    #endif
    static class FileSystemExts
    {
        /// <summary>
        /// Treats this file as a zip file and extracts it to the specified target directory. This file and 
        /// the target directory do NOT need to be in the same file system. 
        /// If this file is not a valid zip archive, or any error occurs, then false is returned.
        /// </summary>
        public static bool ExtractZip(this FSFile zipFile, FSDir targetDir, 
            ZipExtractionOptions options)
        {
			// First open the FSFile for reading
			Stream fsFileStream = zipFile.OpenReadOnly();

            // Create a zip archive (for reading) from the stream
            ZipArchive zipA = null;
            try
            {
                zipA = new ZipArchive(fsFileStream, ZipArchiveMode.Read);
            }
            catch (Exception)
            {
                fsFileStream.Dispose();
                return false;
            }

            // Call the utility function to do the actual extraction
            ExtractZip(zipA, targetDir, options);

            // Clean up and return
            zipA.Dispose();
            fsFileStream.Dispose();
            return true;
        }

        private static ZipExtractionStats ExtractZip(ZipArchive zipA, FSDir targetDirectory, ZipExtractionOptions options)
        {
            // Keep track of the number of entries encountered and extracted
            ZipExtractionStats stats = new ZipExtractionStats();

            // Loop through entries and extract
            foreach (ZipArchiveEntry entry in zipA.Entries)
            {
                stats.NumEntriesSeen++;
                bool skipThisEntry = false;
				FSDir targetDir = targetDirectory;

                // Split the full name on '/'
                string[] pieces = entry.FullName.Split('/');
                int i;
                for (i = 0; i < pieces.Length - 1; i++)
                {
                    targetDir = targetDir.CreateDir(pieces[i]);
                    if (null == targetDir)
                    {
                        skipThisEntry = true;
                        break;
                    }
                }

                // Skip this entry if need be
                if (skipThisEntry) { continue; }

                // At this point pieces[i] is the file we need to create in targetDir
                if (targetDir.GetFile(pieces[i]) != null && !options.Overwrite)
                {
                    // We can't overwrite the file
                    continue;
                }
                FSFile fileForEntry = targetDir.CreateFile(pieces[i]);
                if (null == fileForEntry) { continue; }

                // Open the zip entry stream for reading and the FSFile for writing. Notice that despite 
                // the possibility of failure to open the source stream from the zip file entry, we've 
                // already created the FSFile in the target directory. This is intentional. If we fail 
                // to extract a file, then a 0-byte placeholder is desired (for now, may change later).
                Stream src = entry.Open();
                if (src == null) { continue; }
                Stream dst = fileForEntry.OpenReadWrite();
                if (dst == null)
                {
                    src.Dispose();
                    continue;
                }

                // Copy the contents
                byte[] buf = new byte[8192];
                while (true)
                {
                    int bytesRead = src.Read(buf, 0, buf.Length);
                    if (bytesRead <= 0) { break; }
                    dst.Write(buf, 0, bytesRead);
                }

                // Clean up and increment number of files extracted
                src.Dispose();
                dst.Dispose();
                stats.NumEntriesExtracted++;
            }

            return stats;
        }
    }

    public class ZipExtractionOptions
    {
        public readonly bool Overwrite;

		public ZipExtractionOptions(bool overwrite)
		{
			Overwrite = overwrite;
		}
    }

    public struct ZipExtractionStats
    {
        public int NumEntriesSeen;

        public int NumEntriesExtracted;
    }
}
// Author:
// Evan Thomas Olds
//
// Creation Date:
// June 11, 2015
//
// Description:
//    Declarations for file system abstractions.
//    Design goals:
// 1. There should never be a need to use a path-separator character.
// 2. Anything implementing FSFileSystem must not offer access to content "above" its root directory. So as 
//    an example the StandardFileSystem implementation allows construction from any path on disk. If that 
//    path was "/home/whatever" then nothing outside of the "whatever" directory will be accessible through 
//    the file system object.
// 3. Avoid conflicts with things in the System.IO namespace. So classes/interfaces in this file should not 
//    have names that exactly match Path, Directory, or File. Use FS (for file system) prefix before class 
//    names that would otherwise conflict.
// 4. Use "Dir" instead of "Directory" in naming conventions.
// 5. Avoid throwing exceptions and instead use function return values to indicate success or failure.
// 6. Keep files and directories as two separate entities. (debating whether or not I should 
//    stick with this one long term or perhaps have a common base class such as FSFileSystemEntry 
//    for the two)
//
// Long term goals that intentionally are not going to be implemented until a much later date:
// 1. Provide the ability to work with permissions for files and directories
// 2. Support concurrent operations in the same way a normal file system would for all inheriting classes

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;

namespace ETOF.IO
{
    public abstract class FSEntry
    {
        /// <summary>
        /// Gets the directory that this FSEntry resides in. Will be null if and only if this entry is the  
        /// root directory of the file system. Must be non-null in all other cases.
        /// </summary>
        public abstract FSDir Parent { get; }
    }

	public abstract class FSFile : FSEntry
	{
		/// <summary>
		/// Copies this file to the destination directory. Returns the file produced by the copy on success, 
		/// null on failure. Note that although the returned file is the resulting file from the copy, there 
		/// is one case where it's exactly the same as the source file, and that's when the destination 
		/// directory is the same as the containing directory of this file. A reference to this file is 
		/// returned in this case.
		/// If the file cannot be created in the destination location, then null is returned.
		/// </summary>
		public virtual FSFile CopyTo(FSDir destination, bool overwrite)
		{
			// If we're trying to copy to the exact same directory then no action is necessary
			if (object.ReferenceEquals(Parent, destination))
			{
				return this;
			}

			// If we cannot overwrite and the destination already contains a file with the same 
			// name then we just return.
			if (!overwrite && null != destination.GetFile(Name))
			{
				return null;
			}

			// Create the file in the destination and open for writing
			FSFile dstFile = destination.CreateFile(Name);
			if (null == dstFile)
			{
				return null;
			}
			Stream dst = dstFile.OpenReadWrite();
			Stream src = OpenReadOnly();
			byte[] buf = new byte[4096];
			while (true)
			{
				int bytesRead = src.Read(buf, 0, buf.Length);
				if (0 == bytesRead)
				{
					break;
				}
				dst.Write(buf, 0, bytesRead);
			}
			src.Dispose();
			dst.Dispose();

			return dstFile;
		}

		public abstract DateTime CreationDateTime { get; }

		/// <summary>
		/// Deletes this file from the file system. If the file has already been deleted then true is 
		/// returned. A return value of false indicates that the file exists but was not deleted. This 
        /// may indicate that the file is open for reading or writing. 
		/// After successful deletion the FSFile object remains a valid object in memory, but the Exists 
		/// property will be false and no member functions should succeed after this point.
		/// </summary>
		public abstract bool Delete();

		/// <summary>
		/// Gets a boolean value indicating whether or not the file still exist. An FSFile object can 
		/// be returned from a function at a time when it DOES exist, but it can be deleted by a 
		/// variety of methods after that. This property is available to ask if it exists right now. 
		/// Implementations must NOT cache this value, unless they have a guarantee that the state 
		/// will persist, such as if the file cannot possibly be deleted from the file system.
		/// </summary>
		public abstract bool Exists { get; }

		public abstract DateTime LastWriteDateTime
		{
			get;
		}

		/// <summary>
		/// Gets the file name. This is only a file name and must never contain full path information. 
		/// If the directory is the root of the file system then it may or may not have a non-empty 
		/// name. It should be noted that an empty string can potentially be returned because of this, 
		/// but not all root directories will necessarily have empty names. See documentation specific 
		/// to classes that implement FSFile for clarifying details.
		/// </summary>
		public abstract string Name { get; }

		/// <summary>
		/// Gets a read-only stream for the file. The caller is responsible for disposing the stream 
		/// when done with it.
		/// Returns null under any of the following circumstances: 
		/// - the file does not exist
		/// - the file is not accessible due to account permissions
		/// - the file is open for writing
		/// Each class inheriting from FSFile is free to apply additional constraints, but must adhere 
		/// to those mentioned above.
		/// </summary>
		public abstract Stream OpenReadOnly();

		/// <summary>
		/// Gets a stream for the file that supports reading and writing. 
		/// The caller is responsible for disposing the stream when done with it.
		/// This function must not throw exceptions and instead must return null on failure.
		/// If the file exists but is not accessible then null is returned.
		/// If the file does not exist then it will be created. Should it not exist and also cannot be 
		/// created, then null will be returned.
		/// The returned stream must be at position 0 and no file contents are to be modified by the 
		/// process of merely obtaining the read/write stream.
		/// The underlying implementation may or may not lock the file, preventing other open calls 
		/// from working until this stream is disposed. The implementation may also choose to return 
		/// null if there are any active streams for the file that are currently opened for reading. 
		/// That functionality is specific to the implementation and the documentation for the 
		/// implementation must provide further details. 
		/// By default it would be safest to assume that most implementations will only allow for one 
		/// stream to be open for an FSFile object at any given moment.
		/// </summary>
		public abstract Stream OpenReadWrite();

		/// <summary>
		/// Reads the entire contents of the file into a single byte array and returns it. A null 
		/// array is returned on error. 
		/// Currently only supports files of length (2^31 - 1) bytes or smaller.
		/// </summary>
		public virtual byte[] ReadAllBytes()
		{
			Stream s = OpenReadOnly();
			if (null == s)
			{
				return null;
			}

			// Since we don't currently support it, reject call if stream length > int.MaxValue
			if (s.Length > (long)int.MaxValue)
			{
				return null;
			}

			// Allocate memory for the file data
			byte[] data = new byte[s.Length];
			if (null == data)
			{
				s.Dispose();
				return null;
			}

			// Read it all
			long bytesRead = 0;
			while (bytesRead < data.Length)
			{
				long amountLeft = data.LongLength - bytesRead;
				int readAmount = (int)Math.Min((long)int.MaxValue, amountLeft);
				int numRead = s.Read(data, (int)bytesRead, (int)readAmount);
				if (0 == numRead)
				{
					break;
				}
				bytesRead += numRead;
			}

			// Dispose the stream and return the data
			s.Dispose();
			return data;
		}

		public abstract string[] ReadAllLines();

		public abstract string ReadAllText();

		public abstract bool Rename(string newName);

		/// <summary>
		/// Gets the size, in bytes, of the file. Returns -1 if the file does not exist or cannot be 
		/// accessed.
		/// </summary>
		public abstract long Size
		{
			get;
		}

		public virtual bool WriteAllBytes(byte[] bytes)
		{
			Stream s = OpenReadWrite();
			if (null == s) { return false; }
			s.Write(bytes, 0, bytes.Length);
			s.Dispose();
			return true;
		}
	}

	/// <summary>
	/// Represents a directory within a file system. A directory can contain files and other directories.
	/// </summary>
	public abstract class FSDir : FSEntry, IEquatable<FSDir>
	{
		/// <summary>
		/// Copies all files and directories within this directory to the specified destination. The 
		/// destination is checked to see if it exists first and if it does not, no copy is performed 
		/// and false is returned. Otherwise all content is copied.
		/// The copy is recursive, so directories within directories are also copied.
		/// </summary>
		/// <param name="overwrite">
		/// True to overwrite files with the same name, false to not copy files with conflicting names.
		/// </param>
		public virtual bool CopyContentsTo(FSDir destination, bool overwrite)
		{
			if (!destination.Exists)
			{
				return false;
			}

			// First copy all the files
			foreach (FSFile ourFile in GetFiles())
			{
				ourFile.CopyTo(destination, overwrite);
			}

			// Now we need to copy directories. We need to create any directory that doesn't 
			// exist, then we can recursively copy files to it.
			foreach (FSDir ourDir in GetDirs())
			{
				FSDir theirDir = destination.GetDir(ourDir.Name);
				if (null == theirDir)
				{
					theirDir = destination.CreateDir(ourDir.Name);
				}
				if (null != theirDir)
				{
					ourDir.CopyContentsTo(theirDir, overwrite);
				}
			}

			return true;
		}

		/// <summary>
		/// Creates a directory within this one with the specified name. If the directory already exists it 
		/// will just be returned (functionally equivalent to calling GetDir). 
		/// If the directory cannot be created then null is returned.
		/// </summary>
		public abstract FSDir CreateDir(string name);

		/// <summary>
		/// Creates a file within this directory with the specified name. If the file already exists it will 
		/// be overwritten and truncated back to a length of 0 bytes. 
		/// The returned file must support opening for reading and writing.
		/// If the file cannot be created then null is returned.
		/// </summary>
		public abstract FSFile CreateFile(string fileName);

		/// <summary>
		/// Creates a file in this directory with text content from a string. If the file already exists 
		/// and "overwrite" is false, then null is returned. Otherwise the file will be created (or 
		/// overwritten) so that it has the text content.
		/// Returns null if this directory does not exist.
		/// </summary>
		public virtual FSFile CreateTextFile(string fileName, bool overwrite, string textContent)
		{
			if (!Exists)
			{
				return null;
			}

			// If we cannot overwrite and the file already exists then we return null
			if (!overwrite && null != GetFile(fileName))
			{
				return null;
			}

			FSFile file = CreateFile(fileName);
			byte[] textBytes = System.Text.Encoding.UTF8.GetBytes(textContent);
			file.WriteAllBytes(textBytes);
			return file;
		}

		/// <summary>
		/// Deletes this directory and all of its contents. If the directory is completely deleted then 
		/// true will be returned. A return value of false indicates that the directory was not 
		/// completely deleted. This could mean that nothing was deleted at all or a partial deletion 
		/// happened.
		/// After successful deletion the FSDir object remains a valid object in memory, but the Exists 
		/// property will be false and no file- or directory-oriented member functions are expected to 
		/// succeed after this point.
		/// If the directory doesn't exist then this function returns true, even though it will not 
		/// perform any file or directory deletions in that state (because it's already deleted).
		/// </summary>
		public abstract bool Delete();

		/// <summary>
		/// Deletes all files and directories within this directory. The directory itself is not deleted.
		/// </summary>
		public virtual void DeleteContents()
		{
			foreach (FSFile file in GetFiles())
			{
				file.Delete();
			}

			foreach (FSDir dir in GetDirs())
			{
				dir.Delete();
			}
		}

		public abstract bool Equals(FSDir other);

		/// <summary>
		/// Gets a value indicating whether or not this directory exists. If this is ever false then the 
		/// FSDir object must be considered invalid and not used for any further operations.
		/// </summary>
		public abstract bool Exists { get; }

		/// <summary>
		/// Gets the size, in bytes, of content within this directory. If includeDirs is false then the 
		/// combined size of all the files within the directory will be returned. Otherwise the size of 
		/// all files and contained directories will be returned.
		/// -1 is returned on error.
		/// </summary>
		public virtual long GetContentSize(bool includeDirs)
		{
			long size = 0;
			foreach (FSFile file in GetFiles())
			{
				size += file.Size;
			}

			// Add sizes of directories if it was requested
			if (includeDirs)
			{
				foreach (FSDir dir in GetDirs())
				{
					size += dir.GetContentSize(true);
				}
			}

			return size;
		}

		/// <summary>
		/// Gets the directory within this one that has the specified name. If no such directory exists then 
		/// null is returned.
		/// The name is NOT permitted to contain sub-directories and/or path separating characters. It must 
		/// be just a name of a single directory that is to be found directly within this one. Implementing 
		/// classes must ensure that name strings are checked and if they violate these rules then null is 
		/// returned.
		/// </summary>
		public abstract FSDir GetDir(string name);

		/// <summary>
		/// Gets an array of all the directories in this directory. This is non-recursive, so only the 
		/// directories that reside directly within this directory are in the returned array.
		/// In the case where there are no files in this directory an array of length 0 will be returned.
		/// The array will always be non-null unless memory allocation fails.
		/// </summary>
		public abstract FSDir[] GetDirs();

		/// <summary>
		/// Gets the file within this directory that has the specified name. If no such file exists then 
		/// null is returned.
		/// The name is NOT permitted to contain sub-directories and/or path separating characters. It must 
		/// be just a name of a single file that is to be found directly within this directory. Implementing 
		/// classes must ensure that name strings are checked and if they violate these rules then null is 
		/// returned.
		/// </summary>
		public abstract FSFile GetFile(string name);

		/// <summary>
		/// Gets an array of all the files in this directory. The array will always be non-null unless 
		/// memory allocation fails. In the case where there are no files in this directory an array 
		/// of length 0 will be returned.
		/// </summary>
		public abstract FSFile[] GetFiles();

		/// <summary>
		/// Gets an array of all the files in this directory whose name matches the regular expression. Only 
        /// files names that have a full match against the regex (i.e. a match of same length as the file name 
        /// string) will be included in the returned array.
		/// The array will always be non-null unless memory allocation fails. 
		/// In the case where there are no files in this directory with matching name, an array of length 0 
		/// will be returned.
		/// </summary>
		public virtual FSFile[] GetFiles(string regEx)
		{
            // First get ALL the files
            FSFile[] all = GetFiles();
            if (null == all) { return null; }

			// Intialize the regular expression and the list of filtered files
			Regex regExp = null;
			try
			{
				regExp = new Regex(regEx);
			}
			catch (Exception) { return new FSFile[0]; }
            var list = new List<FSFile>();

            // Go through all the files and find matches
            foreach (FSFile file in all)
            {
                var matchCollection = regExp.Matches(file.Name);
                foreach (Match match in matchCollection)
                {
                    if (match.Length == file.Name.Length)
                    {
                        list.Add(file);
                    }
                }
            }

            return list.ToArray();
		}

		/// <summary>
		/// Determines whether this instance is an ancestor of the specified directory. This means you can 
		/// start in this directory and traverse "down" by getting directories and eventually find dir. If 
		/// dir is the same directory as this instance or this directory is not an ancestor of it then 
		/// false is returned. If dir is null then false is returned. Otherwise true is returned.
		/// </summary>
		public virtual bool IsAncestorOf(FSDir dir)
		{
			if (null == dir)
			{
				return false;
			}

			dir = dir.Parent;
			while (dir != null)
			{
				if (dir.Equals(this))
				{
					return true;
				}
				dir = dir.Parent;
			}

			// If we got all the way up to the root without finding this directory then we're not 
			// an ancestor.
			return false;
		}

		/// <summary>
		/// Moves all of the contents from this directory into another. Several rules apply:
		/// 1. If newParent is null then false is returned and no other action is taken.
		/// 2. If this directory is empty then true is returned and no other action is taken.
		/// 3. If newParent is the same exact directory as this one then true is returned and no 
		///    other action is taken.
		/// 4. If this directory is an ancestor of newParent then false is returned (because we can't 
		///    move all our contents into something contained within all our contents) and no other 
		///    action is taken.
		/// This directory will not be deleted, but will be empty after a successful call.
		/// The default implementation performs the move by copying each item (directory or file) to 
		/// the target location and then deleting it here. For this reason the operation is considered 
		/// to be non-atomic.
		/// If overwrite is false a partial move is still potentially performed. This parameter just 
		/// prevents moving of files or directories that have the same name in the target location. In 
		/// the case that only a partial move occurs, false is returned. A value of true is returned 
		/// only when all content has been successfully moved.
		/// </summary>
		/// <returns><c>true</c>, if all content was moved, <c>false</c> otherwise.</returns>
		public virtual bool MoveContentsTo(FSDir newParent, bool overwrite)
		{
			// Check rule 1
			if (null == newParent) { return false; }

			if (this.Equals(newParent) || this.IsAncestorOf(newParent))
			{
				// Satifies rules 3 and 4
				return false;
			}

			bool copiedAll = true;
			foreach (FSFile file in GetFiles())
			{
				// If we cannot overwrite and the target directory already has a file of the same name 
				// then we mark copiedAll as false and move on.
				if (!overwrite && null != newParent.GetFile(file.Name))
				{
					copiedAll = false;
					continue;
				}

				file.CopyTo(newParent, overwrite);
				file.Delete();
			}

			// Now directories
			foreach (FSDir dir in GetDirs())
			{
				// If we cannot overwrite and the target directory already has a directory of the same 
				// name then we mark copiedAll as false and move on.
				if (!overwrite && null != newParent.GetDir(dir.Name))
				{
					copiedAll = false;
					continue;
				}

				// Create the directory in the target location, move contents, then delete old dir
				FSDir dest = newParent.CreateDir(dir.Name);
				if (null == dest)
				{
					copiedAll = false;
				}
				else
				{
					if (dir.MoveContentsTo(dest, overwrite))
					{
						dir.Delete();
					}
				}
			}

			return copiedAll;
		}

		/// <summary>
		/// Gets the directory name. This is only a directory name and must never contain full path information.
		/// </summary>
		public abstract string Name { get; }
	}

	public abstract class FSFileSystem
	{
		public abstract FSDir Root { get; }

        public virtual bool Contains(FSDir dir)
        {
            // Get the root of the file system that dir resides in
            while (dir.Parent != null)
            {
                dir = dir.Parent;
            }

            // It must be the same root as this file system
            return object.ReferenceEquals(this.Root, dir);
        }

        public virtual bool Contains(FSFile file)
        {
            // Get the root of the file system that file resides in
            FSDir dir = file.Parent;
            while (dir.Parent != null)
            {
                dir = dir.Parent;
            }

            // It must be the same root as this file system
            return object.ReferenceEquals(this.Root, dir);
        }
	}

	#if ETOF_PUBLIC
	public 
	#else
	internal 
	#endif
	class StandardFileSystem : FSFileSystem
	{
		private readonly StdDir r_root;

		private StandardFileSystem(string rootDir)
		{
			r_root = new StdDir(rootDir, null);
		}

		public static StandardFileSystem Create(string rootDir)
		{
			if (!Directory.Exists(rootDir)) { return null; }
			return new StandardFileSystem(rootDir);
		}

		public static string GetFullPath(FSEntry entry)
		{
			StdDir dir = entry as StdDir;
			if (dir != null) { return dir.FullPath; }
			StdFile file = entry as StdFile;
			if (file != null) { return file.FullPath; }
			return null;
		}

		public override FSDir Root
		{
			get { return r_root; }
		}

		#region Nested classes
		private class StdDir : FSDir
		{
			private readonly string r_fullPath;

			private StdDir m_parent;

			public StdDir(string fullPath, StdDir parent)
			{
				r_fullPath = fullPath;
				m_parent = parent;
			}

			/// <summary>
			/// Creates a directory within this one with the specified name. If the directory already exists it 
			/// will just be returned (functionally equivalent to calling GetDir). 
			/// If the directory cannot be created then null is returned.
			/// </summary>
			public override FSDir CreateDir(string name)
			{
				// Ensure that we don't have any path characters, we're not "..", and we have a non-null and 
				// non-empty name.
				if (name.Contains("\\") || name.Contains("/") || ".." == name ||
					string.IsNullOrEmpty(name))
				{
					return null;
				}

				string fullPath = Path.Combine(r_fullPath, name);
				if (!Directory.Exists(fullPath))
				{
					// We need to create it
					Directory.CreateDirectory(fullPath);
				}

				return new StdDir(fullPath, this);
			}

			/// <summary>
			/// Creates a file within this directory with the specified name. If the file already exists it will 
			/// be overwritten and truncated back to a length of 0 bytes. 
			/// The returned file must support opening for reading and writing.
			/// If the file cannot be created then null is returned.
			/// </summary>
			public override FSFile CreateFile(string fileName)
			{
				// Ensure that we don't have any path characters or a null or empty name
				if (fileName.Contains("\\") || fileName.Contains("/") ||
					string.IsNullOrEmpty(fileName))
				{
					return null;
				}

				string fullName = Path.Combine(r_fullPath, fileName);
				FileStream fs = null;
				try
				{
					fs = new FileStream(fullName, FileMode.Create, FileAccess.ReadWrite);
				}
				catch (Exception)
				{
					return null;
				}
				fs.Dispose();
				return new StdFile(fullName, this);
			}

			public override bool Delete()
			{
				if (!Exists)
				{
					// Specification says return true if we're already deleted
					return true;
				}

				Directory.Delete(r_fullPath, true);
				return true;
			}

			public override bool Equals(FSDir other)
			{
				StdDir sd = other as StdDir;
				if (null == sd)
				{
					// If it's not of the same type then it cannot be equal
					return false;
				}
				return r_fullPath == sd.r_fullPath;
			}

			public override bool Equals(object obj)
			{
				FSDir dir = obj as FSDir;
				if (null == dir)
				{
					return false;
				}
				return Equals(dir);
			}

			public override bool Exists
			{
				get
				{
					return Directory.Exists(r_fullPath);
				}
			}

			public string FullPath
			{
				get { return r_fullPath; }
			}

			/// <summary>
			/// Gets the directory within this one that has the specified name. If no such directory exists then 
			/// null is returned.
			/// </summary>
			public override FSDir GetDir(string name)
			{
				// Ensure that we don't have any path characters or ".."
				if (name.Contains("\\") || name.Contains("/") || ".." == name)
				{
					return null;
				}

				string path = Path.Combine(r_fullPath, name);
				if (Directory.Exists(path))
				{
					return new StdDir(path, this);
				}
				return null;
			}

			/// <summary>
			/// Gets an array of all the directories in this directory. This is non-recursive, so only the 
			/// directories that reside directly within this directory are in the returned array.
			/// In the case where there are no files in this directory an array of length 0 will be returned.
			/// The array will always be non-null unless memory allocation fails.
			/// </summary>
			public override FSDir[] GetDirs()
			{
				string[] dirs1 = Directory.GetDirectories(r_fullPath);
				StdDir[] dirs2 = new StdDir[dirs1.Length];
				for (int i = 0; i < dirs1.Length; i++)
				{
					dirs2[i] = new StdDir(dirs1[i], this);
				}
				return dirs2;
			}

			/// <summary>
			/// Gets the file within this directory that has the specified name. If no such file exists then 
			/// null is returned.
			/// The name is NOT permitted to contain sub-directories and/or path separating characters. It must 
			/// be just a name of a single file that is to be found directly within this directory. Implementing 
			/// classes must ensure that name strings are checked and if they violate these rules then null is 
			/// returned.
			/// </summary>
			public override FSFile GetFile(string name)
			{
				// Ensure that we don't have any path characters or ".."
				// For future versions I may want to also check for other invalid characters that aren't 
				// supported by the underlying file system (long term TODO)
				if (name.Contains("\\") || name.Contains("/")  || ".." == name)
				{
					return null;
				}

				string fullPath = Path.Combine(r_fullPath, name);
				if (File.Exists(fullPath))
				{
					return new StdFile(fullPath, this);
				}
				return null;
			}

			/// <summary>
			/// Gets an array of all the files in this directory. The array will always be non-null unless 
			/// memory allocation fails. In the case where there are no files in this directory an array 
			/// of length 0 will be returned.
			/// </summary>
			public override FSFile[] GetFiles()
			{
				string[] files1 = Directory.GetFiles(r_fullPath);
				StdFile[] files2 = new StdFile[files1.Length];
				for (int i = 0; i < files1.Length; i++)
				{
					files2[i] = new StdFile(files1[i], this);
				}
				return files2;
			}

			public override int GetHashCode()
			{
				// Since the full path should be a unique identifier for this object, we can 
				// just use its hash code.
				return r_fullPath.GetHashCode();
			}

			// TODO: Finish this implementation
			/*
			/// <summary>
			/// Moves this directory into another. This must fail if any of the following are true:
			/// 1. The directory attempting to be moved is the root of the file system
			/// 2. The new parent directory is the same directory as this directory
			/// 3. The new parent is contained somewhere within this directory (this directory is an 
			///    ancestory of the requested new parent).
			/// After a successful move this FSDir object will represent the same directory in its new 
			/// location.
			/// </summary>
			/// <returns><c>true</c>, if it was moved, <c>false</c> otherwise.</returns>
			public override bool MoveInto(FSDir newParent, bool overwrite)
			{
				if (null == m_parent)
				{
					// This means we're the root and we cannot move the root
					return false;
				}

				if (this.Equals(newParent))
				{
					// This means we're the same directory as the parent so obviously we can't move
					return false;
				}

				if (!overwrite && null != newParent.GetDir(Name))
				{
					// A directory with the same name already exists in the target location so and we 
					// cannot overwrite so we must return false.
					return false;
				}

				// We've got a special handler if the new parent is also a StdDir object
				StdDir sd = newParent as StdDir;
				if (null != sd)
				{
					return MoveInto(sd);
				}

				return base.MoveInto(newParent, overwrite);
			}

			private bool MoveInto(StdDir newParent)
			{
				try
				{
					Directory.Move(m_fullPath, Path.Combine(newParent.m_fullPath, Name));
				}
				catch (Exception) { return false; }
				return true;
			}
			*/

			/// <summary>
			/// Gets the directory name. This is only a directory name and will never contain full path information.
			/// </summary>
			public override string Name
			{
				get
				{
					return Path.GetFileName(r_fullPath);
				}
			}

			public override FSDir Parent
			{
				get
				{
					return m_parent;
				}
			}
		}

		private class StdFile : FSFile
		{
			private string m_fullPath;

			private readonly StdDir r_container;

			public StdFile(string fullPath, StdDir container)
			{
				m_fullPath = fullPath;
				r_container = container;
			}

			public override DateTime CreationDateTime
			{
				get
				{
					return (new FileInfo(FullPath)).CreationTime;
				}
			}

			/// <summary>
			/// Deletes this file from the file system. If the file has already been deleted then true is 
			/// returned. A return value of false indicates that the file exists but was not deleted.
			/// After successful deletion the FSFile object remains a valid object in memory, but the Exists 
			/// property will be false and no member functions are expected to succeed after this point.
			/// </summary>
			public override bool Delete()
			{
				if (!Exists)
				{
					// If it's already deleted then the specification says return true
					return true;
				}

				try { File.Delete(FullPath); }
				catch (Exception) { return false; }
				return true;
			}

			/// <summary>
			/// Gets the directory that this file resides in.
			/// </summary>
			public override FSDir Parent
			{
				get { return r_container; }
			}

			public override bool Exists
			{
				get
				{
					return File.Exists(FullPath);
				}
			}

			public string FullPath
			{
				get { return m_fullPath; }
			}

			public override DateTime LastWriteDateTime
			{
				get
				{
					return (new FileInfo(FullPath)).LastWriteTime;
				}
			}

			public override string Name
			{
				get
				{
					return Path.GetFileName(FullPath);
				}
			}

			public override Stream OpenReadOnly()
			{
				try
				{
					return new FileStream(FullPath, FileMode.Open, FileAccess.Read);
				}
				catch (Exception) { }
				return null;
			}

			public override Stream OpenReadWrite()
			{
				try
				{
					return new FileStream(FullPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
				}
				catch (Exception) { }
				return null;
			}

			public override byte[] ReadAllBytes()
			{
				byte[] result = null;
				try
				{
					result = File.ReadAllBytes(FullPath);
				}
				catch (Exception) { }
				return result;
			}

			public override string[] ReadAllLines()
			{
				return File.ReadAllLines(FullPath);
			}

			public override string ReadAllText()
			{
				return File.ReadAllText(FullPath);
			}

			public override bool Rename(string newName)
			{
				// First check for invalid characters
				if (newName.Contains("\\") || newName.Contains("/") || ".." == newName ||
					string.IsNullOrEmpty(newName))
				{
					return false;
				}

				// Next check to make sure the parent directory doesn't already contain a 
				// file with this name
				if (Parent.GetFile(newName) != null) { return false; }

				// Make the full path for the new file
				string newPath = Path.GetDirectoryName(FullPath);
				newPath = Path.Combine(newPath, newName);

				File.Move(FullPath, newPath);
				m_fullPath = newPath;
				return true;
			}

			/// <summary>
			/// Gets the size, in bytes, of the file. Returns -1 if the file does not exist or cannot be 
			/// accessed. 
			/// </summary>
			public override long Size
			{
				get
				{
					try
					{
						FileInfo fi = new FileInfo(FullPath);
						if (null != fi)
						{
							return fi.Length;
						}
					}
					catch (Exception) { }
					return -1;
				}
			}
		}
		#endregion
	}

	/// <summary>
	/// Represents a file system that is held entirely in memory. All directory and file properties as 
	/// well as file contents are held in memory and nothing should be read from or written to disk.
	/// </summary>
	#if ETOF_PUBLIC
	public 
	#else
	internal 
	#endif
	class MemoryFileSystem : FSFileSystem
	{
		private readonly MemDir r_root;

		public MemoryFileSystem()
		{
			r_root = new MemDir(string.Empty, null);
		}

		public override FSDir Root
		{
			get { return r_root; }
		}

		#region Nested classes
		private class MemDir : FSDir
		{
			private readonly Dictionary<string, MemDir> r_dirs = new Dictionary<string, MemDir>();

			/// <summary>
			/// Represents whether or not this directory exists. This can be set to false only by a call to 
			/// the Delete() function, after which no operations on the directory are valid.
			/// </summary>
			private bool m_exists = true;

			private string m_name;

			private MemDir m_parent;

			private readonly ConcurrentDictionary<string, MemFile> r_files = 
				new ConcurrentDictionary<string, MemFile>();

			public MemDir(string name, MemDir parent)
			{
				m_name = string.IsNullOrEmpty(name) ? string.Empty : name;
				m_parent = parent;
			}

			/// <summary>
			/// Creates a directory within this one with the specified name. If the directory already exists it 
			/// will just be returned (functionally equivalent to calling GetDir). 
			/// If the directory cannot be created then null is returned.
			/// </summary>
			public override FSDir CreateDir(string name)
			{
				// If we don't exist then we cannot create directories
				if (!Exists)
				{
					return null;
				}

				// Ensure that we don't have any path characters or a null or empty name
				if (name.Contains("\\") || name.Contains("/") || name == "." || name == ".." ||
					string.IsNullOrEmpty(name))
				{
					return null;
				}

				lock (r_dirs)
				{
					if (r_dirs.ContainsKey(name))
					{
						return r_dirs[name];
					}
					MemDir dir = new MemDir(name, this);
					r_dirs[name] = dir;
					return dir;
				}
			}

			/// <summary>
			/// Creates a file within this directory with the specified name. If the file already exists it will 
			/// be overwritten and truncated back to a length of 0 bytes. 
			/// The returned file will have read and write permissions.
			/// If the file cannot be created then null is returned.
			/// </summary>
			public override FSFile CreateFile(string fileName)
			{
				// If we don't exist then we cannot create files
				if (!m_exists)
				{
					return null;
				}

				// Ensure that we don't have any path characters
				if (fileName.Contains("\\") || fileName.Contains("/") ||
					string.IsNullOrEmpty(fileName))
				{
					return null;
				}

				lock (r_files)
				{
					MemFile file = new MemFile(fileName, this, r_files);
					r_files[fileName] = file;
					return file;
				}
			}

			public override bool Delete()
			{
				if (!Exists)
				{
					// If it's already deleted then the specification says return true
					return true;
				}

				// Clear contents first
				lock (r_dirs) { r_dirs.Clear(); }
				lock (r_files) { r_files.Clear(); } 

				// Remove this instance from our parent collection (if we have a parent)
				if (null != m_parent)
				{
					lock (m_parent.r_dirs)
					{
						m_parent.r_dirs.Remove(m_name);
					}
				}

				// Mark as deleted and return true
				m_exists = false;
				return true;
			}

			public override bool Equals(FSDir other)
			{
				// A reference comparison is OK here because the memory file system only creates one 
				// instance of each directory. There cannot be two distinct objects in memory that 
				// represent the same MemDir.
				return object.ReferenceEquals(this, other);
			}

			public override bool Equals(object other)
			{
				// A reference comparison is OK here because the memory file system only creates one 
				// instance of each directory. There cannot be two distinct objects in memory that 
				// represent the same MemDir.
				return object.ReferenceEquals(this, other);
			}

			public override bool Exists
			{
				get
				{
					if (null == m_parent)
					{
						// If we're the root we have a flag to represent this
						return m_exists;
					}

					// Otherwise we check the parent
					lock (m_parent.r_dirs) { return m_parent.r_dirs.ContainsKey(m_name); }
				}
			}

			/// <summary>
			/// Gets the directory within this one that has the specified name. If no such directory exists 
			/// then null is returned.
			/// </summary>
			public override FSDir GetDir(string name)
			{
				// Ensure that we don't have any path characters
				if (name.Contains("\\") || name.Contains("/") || name == "..")
				{
					return null;
				}

				lock (r_dirs)
				{
					if (r_dirs.ContainsKey(name))
					{
						return r_dirs[name];
					}
				}
				return null;
			}

			/// <summary>
			/// Gets an array of all the directories in this directory. This is non-recursive, so only the 
			/// directories that reside directly within this directory are in the returned array.
			/// In the case where there are no directories in this directory an array of length 0 will be 
			/// returned. The returned array will always be non-null unless memory allocation fails.
			/// </summary>
			public override FSDir[] GetDirs()
			{
				lock (r_dirs)
				{
					FSDir[] dirs = new FSDir[r_dirs.Count];
					if (null == dirs)
					{
						// To match the specification we return null as opposed to throwing an exception
						return null;
					}
					int i = 0;
					foreach (KeyValuePair<string, MemDir> kvp in r_dirs)
					{
						dirs[i++] = kvp.Value;
					}
					return dirs;
				}
			}

			public override FSFile GetFile(string name)
			{
				// Ensure that we don't have any path characters
				if (name.Contains("\\") || name.Contains("/"))
				{
					return null;
				}

				lock (r_files)
				{
					if (r_files.ContainsKey(name))
					{
						return r_files[name];
					}
				}
				return null;
			}

			/// <summary>
			/// Gets an array of all the files in this directory. The array will always be non-null unless 
			/// memory allocation fails. In the case where there are no files in this directory an array 
			/// of length 0 will be returned.
			/// </summary>
			public override FSFile[] GetFiles()
			{
				lock (r_files)
				{
					FSFile[] files = new FSFile[r_files.Count];
					if (null == files)
					{
						return null;
					}
					int i = 0;
					foreach (KeyValuePair<string, MemFile> kvp in r_files)
					{
						files[i++] = kvp.Value;
					}
					return files;
				}
			}

			/// <summary>
			/// Gets the directory name. This is only a directory name and will never contain full path 
			/// information.
			/// </summary>
			public override string Name
			{
				get { return m_name; }
			}

			public override FSDir Parent
			{
				get { return m_parent; }
			}
		}

		private class MemFile : FSFile
		{
			private class ObservableMemoryStream : MemoryStream
			{
				public event EventHandler OnClosing = delegate { };

				/// <summary>
				/// Invoked after the stream has been written to
				/// </summary>
				public event EventHandler OnWrite = delegate { };

                public ObservableMemoryStream() { }

                private ObservableMemoryStream(byte[] data, bool writable)
                    : base(data, writable) { }

				public override void Close()
				{
					// Note about stream disposal
                    // The MSDN documentation for the Dispose function in MemoryStream states:
                    // "This method disposes the stream, by writing any changes to the backing 
                    //  store and closing the stream to release resources."
                    // So in other words, this class doesn't need to override Dispose.

                    OnClosing(this, EventArgs.Empty);
					base.Close();
				}

                /// <summary>
                /// Creates a read-only observable memory stream, optionally with existing 
                /// data contents from a byte array. If the byte array is null, then an 
                /// empty read-only stream is returned. Otherwise a read-only stream with 
                /// contents from the byte array and a current Position of 0 is returned.
                /// </summary>
                public static ObservableMemoryStream CreateR(byte[] existingData)
                {
                    if (null == existingData) { existingData = new byte[0]; }
                    return new ObservableMemoryStream(existingData, false);
                }

                /// <summary>
                /// Creates a read/write observable memory stream, optionally with existing 
                /// data contents from a byte array. If the byte array is null, then an 
                /// empty read/write stream is returned. Otherwise a read/write stream with 
                /// contents from the byte array and a current Position of 0 is returned. 
                /// The returned stream will always support expanding the size.
                /// </summary>
                public static ObservableMemoryStream CreateRW(byte[] existingData)
                {
                    // Initialize with default constructor to create as RW
                    ObservableMemoryStream oms = new ObservableMemoryStream();

                    // Return now if we have no data to write
                    if (null == existingData) { return new ObservableMemoryStream(); }

                    // Otherwise write the data, seek back to 0 and return
                    oms.Write(existingData, 0, existingData.Length);
                    oms.Position = 0;
                    return oms;
                }

				public override void Write(byte[] buffer, int offset, int count)
				{
					base.Write(buffer, offset, count);
					OnWrite(this, EventArgs.Empty);
				}

				public override void WriteByte(byte value)
				{
					base.WriteByte(value);
					OnWrite(this, EventArgs.Empty);
				}
			}

			private ObservableMemoryStream m_activeRW = null;

			private byte[] m_fileData = new byte[0];

			private DateTime m_lastWriteTime;

			private string m_name;

            private volatile int m_numActiveReadOnlyStreams = 0;

			private MemDir m_parent;

			private IDictionary<string, MemFile> m_parentFiles;

			private readonly DateTime r_created;

            private readonly string r_lockHandle;

			public MemFile(string name, MemDir parent, IDictionary<string, MemFile> parentFiles)
			{
				m_name = name;
				m_parent = parent;
				m_parentFiles = parentFiles;
				m_lastWriteTime = r_created = DateTime.Now;
                r_lockHandle = r_created.ToString();
			}

			public override DateTime CreationDateTime
			{
				get { return r_created; }
			}

			/// <summary>
			/// Deletes this file from the file system. If the file has already been deleted then true is 
			/// returned. A return value of false indicates that the file exists but was not deleted.
			/// After successful deletion the FSFile object remains a valid object in memory, but the Exists 
			/// property will be false and no member functions are expected to succeed after this point.
			/// </summary>
			public override bool Delete()
			{
                lock (r_lockHandle)
                {
                    // We don't allow deletion if the file is open
                    if (m_activeRW != null || m_numActiveReadOnlyStreams > 0)
                    {
                        return false;
                    }

                    if (!Exists)
                    {
                        // If it's already deleted then the specification says return true
                        return true;
                    }

                    // Remove this instance from our parent collection
                    m_parentFiles.Remove(m_name);
                    return true;
                }
			}

			/// <summary>
			/// Gets the directory that this file resides in.
			/// </summary>
			public override FSDir Parent
			{
				get { return m_parent; }
			}

			public override bool Exists
			{
				get { return m_parentFiles.ContainsKey(m_name); }
			}

			public override DateTime LastWriteDateTime
			{
				get { return m_lastWriteTime; }
			}

			public override string Name
			{
				get { return m_name; }
			}

			public override Stream OpenReadOnly()
			{
                lock (r_lockHandle)
                {
                    // We don't support opening any more streams if there is one open RW
                    if (null != m_activeRW)
                    {
                        return null;
                    }

                    // Construct a read-only, observable memory stream from the byte array
                    var oms = ObservableMemoryStream.CreateR(m_fileData);
                    if (null == oms) { return null; }

                    // Set up the closing callback that decrements the count
                    oms.OnClosing += delegate(object sender, EventArgs e)
                    {
                        Interlocked.Decrement(ref m_numActiveReadOnlyStreams);
                    };

                    // If it allocated OK, then increment the number of active read-only streams, and 
                    // return the memory stream.
                    Interlocked.Increment(ref m_numActiveReadOnlyStreams);
                    return oms;
                }
			}

			public override Stream OpenReadWrite()
			{
				lock (r_lockHandle)
                {
                    if (null != m_activeRW)
                    {
                        // Multiple read/write streams for a single file in a memory file system are not 
                        // supported. One read/write stream must be closed before opening another.
                        return null;
                    }

                    // We also don't support opening for write if another stream is opened for read
                    if (m_numActiveReadOnlyStreams > 0) { return null; }

                    // Create an observable memory stream
                    m_activeRW = new ObservableMemoryStream();

                    // Write the existing file data to it and seek back to 0
                    if (null != m_fileData && m_fileData.Length > 0)
                    {
                        m_activeRW.Write(m_fileData, 0, m_fileData.Length);
                        m_activeRW.Position = 0;
                    }

                    // Set up the closing callback
                    m_activeRW.OnClosing += delegate(object sender, EventArgs e)
                    {
                        if (object.ReferenceEquals(m_activeRW, sender) &&
                            null != m_activeRW)
    					{
    						// Get the data from the stream first
    						m_fileData = m_activeRW.ToArray();
    						m_activeRW = null;
    					}
				    };

    				// Set up the write callback
    				m_activeRW.OnWrite += delegate(object sender, EventArgs e)
    				{
    					m_lastWriteTime = DateTime.Now;	
    				};

    				return m_activeRW;
                }
			}

			public override byte[] ReadAllBytes()
			{
				lock (r_lockHandle)
                {
                    byte[] copy = new byte[m_fileData.Length];
                    Array.Copy(m_fileData, copy, m_fileData.Length);
                    return copy;
                }
			}

			public override string[] ReadAllLines()
			{
				StreamReader sr = new StreamReader(new MemoryStream(m_fileData, false));
				List<string> lines = new List<string>();
				while (true)
				{
					string line = sr.ReadLine();
					if (null == line)
					{
						break;
					}
					lines.Add(line);
				}
				sr.Dispose();
				return lines.ToArray();
			}

			public override string ReadAllText()
			{
                // Don't need a lock because we only access m_fileData once (for a read 
                // operation), that array is always non-null, and reference assignment 
                // is atomic. So we get whatever data is in the file and the moment when 
                // we pass m_fileData to the MemoryStream constructor.

                StreamReader sr = new StreamReader(new MemoryStream(m_fileData, false));
				string content = sr.ReadToEnd();
				sr.Dispose();
				return content;
			}

			public override bool Rename(string newName)
			{
				// Can check the name outside of the lock
				if (newName.Contains("\\") || newName.Contains("/") ||
					newName == ".." || newName == "." || string.IsNullOrEmpty(newName))
				{
					return false;
				}

				lock (r_lockHandle)
				{
					if (!Exists) { return false; }

					// Make sure the parent doesn't already contain the new file
					if (m_parent.GetFile(newName) != null) { return false; }

                    // Can finally rename if we get here

                    // First remove the old name from the parent directory list of files
                    m_parentFiles.Remove(m_name);

                    // Rename and add to parent list of fiels
					m_name = newName;
                    m_parentFiles[m_name] = this;
				}

				return true;
			}

			public override long Size
			{
				get
				{
					lock (r_lockHandle)
                    {
                        if (!Exists)
                        {
                            return -1;
                        }
                        return m_fileData.Length;
                    }
				}
			}
		}
		#endregion
	}
}
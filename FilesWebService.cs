// Author:
// Evan Thomas Olds
//
// Creation Date:
// November 13, 2014
//
// Class Dependencies:
// ETOF.IO.MultiTextWriter
// ETOF.WebService

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Web;
using ETOF.IO;

namespace ETOF
{
	#if EOFC_PUBLIC
	public 
	#else
	internal 
	#endif
	class FilesWebService : WebService, IDisposable
	{
		private bool m_allowUploads = true;

        private bool m_disposed = false;

        private MultiTextWriter m_logger = new MultiTextWriter();

        private Action<FilesWebService, string, WebRequest> m_onFileNotFound = null;

		private Func<FSDir, string> m_onNeedDirList;

		/// <summary>
		/// Root folder for this server. Must always be non-null.
		/// </summary>
		private FSDir m_root;

		public FilesWebService(FSDir filesRoot, 
			Action<FilesWebService, string, WebRequest> onFileNotFound = null)
		{
			// Ensure that the root is non-null
			if (null == filesRoot)
			{
				throw new ArgumentNullException(
					"filesRoot",
					"Root folder of FilesWebService cannot be null");
			}

			m_root = filesRoot;
			m_onFileNotFound = onFileNotFound;

            // We have a default page builder for directory listings
            m_onNeedDirList = this.DefaultBuildDirHTML;
		}

        public void AddLogger(TextWriter logger)
        {
            m_logger.Add(logger);
        }

		private string DefaultBuildDirHTML(FSDir directory)
        {
            // Build an HTML file listing
            var sb = new StringBuilder("<html>");

            // We'll need a bit of script if uploading is allowed
            if (m_allowUploads)
            {
                sb.AppendLine(
@"<script>
function selectedFileChanged(fileInput, urlPrefix)
{
    document.getElementById('uploadHdr').innerText = 'Uploading ' + fileInput.files[0].name + '...';

    // Need XMLHttpRequest to do the upload
    if (!window.XMLHttpRequest)
    {
        alert('Your browser does not support XMLHttpRequest. Please update your browser.');
        return;
    }

    // Hide the file selection controls while we upload
    var uploadControl = document.getElementById('uploader');
    if (uploadControl)
    {
        uploadControl.style.visibility = 'hidden';
    }

    // Build a URL for the request
    if (urlPrefix.lastIndexOf('/') != urlPrefix.length - 1)
    {
        urlPrefix += '/';
    }
    var uploadURL = urlPrefix + fileInput.files[0].name;

    // Create the service request object
    var req = new XMLHttpRequest();
    req.open('PUT', uploadURL);
    req.onreadystatechange = function()
    {
        document.getElementById('uploadHdr').innerText = 'Upload (request status == ' + req.status + ')';
        // Un-comment the line below and comment-out the line above if you want the page to 
        // refresh after the upload
        //location.reload();
    };
    req.send(fileInput.files[0]);
}
</script>
");
            }

			// Get the files and directories
			FSFile[] files = directory.GetFiles();
			FSDir[] dirs = directory.GetDirs();

            // Put all the directories first
			if (dirs.Length > 0) { sb.Append("<h3>Folders</h3><hr><br>"); }
            foreach (var dir in dirs)
            {
                sb.AppendFormat(
					"<a href=\"{0}\">{1}</a><br>",
					GetHREF(dir, true), dir.Name);
            }

			// Then all files follow
			if (files.Length > 0) { sb.Append("<h3>Files</h3><hr><br>"); }
			bool highlightRow = false;
			foreach (var file in files)
            {
                // Make an actual image in the page for an image file
                if (HasImgExt(file.Name))
                {
					sb.AppendFormat("<img src=\"{0}\"><br>", GetHREF(file, true));
                }
                else
                {
                    sb.AppendFormat(
						"<div style=\"padding:10 px;{0}\"> <a href=\"{1}\">{2}</a></div> <br>",
						highlightRow ? " background-color:#F0F0F0;" : string.Empty,
						GetHREF(file, true), file.Name);
                }

				highlightRow = !highlightRow;
            }

			// If uploading is allowed, put the uploader at the bottom
			if (m_allowUploads)
			{
				sb.AppendFormat(
					"<hr><h3 id='uploadHdr'>Upload</h3><br>" + 
                    "<input id=\"uploader\" type='file' " + 
                    "onchange='selectedFileChanged(this,\"{0}\")' /><hr>",
                    GetHREF(directory, true));
			}

            sb.Append("</html>");
            return sb.ToString();
        }
		
		public void Dispose()
		{
            m_disposed = true;
			if (null != m_logger)
			{
				m_logger.Dispose();
				m_logger = null;
			}
		}

        private FSEntry GetEntry(string uri)
        {
            // Request URIs must never contain \ (only /)
            if (uri.Contains("\\"))
            {
                return null;
            }

            // Make sure the URI starts with the service URI, and prodived it does, strip 
            // off that part.
            if (!uri.StartsWith(ServiceURI))
            {
                return null;
            }
            uri = uri.Substring(ServiceURI.Length);

            // Get the file or directory from the URI
            string[] parts = uri.Split(
                new char[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            FSDir dir = m_root, parent = null;
            for (int i = 0; i < parts.Length; i++)
            {
                parent = dir;
                dir = dir.GetDir(parts[i]);

                // Should we get a null directory we have 2 cases:
                // 1. This is NOT the last loop iteration, implying that we've hit a not found.
                // 2. This IS the last loop iteration, meaning that the last part of the file 
                //    string may be a file name and/or may be a not found. We fall through to 
                //    the code after this loop in that case.
                if (null == dir && i != parts.Length - 1)
                {
                    return null;
                }
            }

            // If 'dir' is non-null at this point then we return it
            if (null != dir)
            {
                return dir;
            }

            // Coming here implies it's a file name
            return parent.GetFile(parts[parts.Length - 1]);
        }

		public string GetHREF(FSDir directory, bool urlEncoded)
		{
			// The service URL is not required to end with '/' but we must have this 
			// after it when generating an HREF for the page.
			string start = ServiceURI;
			if (!start.EndsWith("/"))
			{
				start += "/";
			}

			// Special case for root
			if (object.ReferenceEquals(directory, m_root))
			{
				return urlEncoded ? Uri.EscapeUriString(start) : start;
			}

			string href = directory.Name;
			FSDir dir = directory.Parent;
			while (!object.ReferenceEquals(dir, m_root))
			{
				href = dir.Name + "/" + href;
				dir = dir.Parent;

				// We should always hit the root before null, provided the file is in fact 
				// somewhere within the hosted directory. Should we get null then the file 
				// is outside of the shared content and it's an invalid call.
				if (null == dir)
				{
					throw new InvalidOperationException(
						"GetHREF was called for a directory that's not in the shared content directories");
				}
			}
			href = start + href;

			if (urlEncoded)
			{
				return Uri.EscapeUriString(href);
			}
			return href;
		}

		public string GetHREF(FSFile file, bool urlEncoded)
		{
			// The service URL is not required to end with '/' but we must have this 
			// after it when generating an HREF for the page.
			string start = ServiceURI;
			if (!start.EndsWith("/"))
			{
				start += "/";
			}

			string href = file.Name;
			FSDir dir = file.Parent;
			while (!object.ReferenceEquals(dir, m_root))
			{
				href = dir.Name + "/" + href;
				dir = dir.Parent;

				// We should always hit the root before null, provided the file is in fact 
				// somewhere within the hosted directory. Should we get null then the file 
				// is outside of the shared content and it's an invalid call.
				if (null == dir)
				{
					throw new InvalidOperationException(
						"GetHREF was called for a file that's not in the shared content directories");
				}
			}
			href = start + href;

			if (urlEncoded)
			{
				return Uri.EscapeUriString(href);
			}
			return href;
		}

		private void HandlePost(WebRequest req)
		{
			if (!m_allowUploads)
			{
				req.WriteHTMLResponse(
					"<html>Uploads are not currently allowed on this server</html>");
				return;
			}

			req.WriteNotImplementedResponse();
			return;

			// TODO: Revisit this at some point. I didn't finish it because PUT-based uploads are working 
			// in other web services I've implemented in other apps, so this wasn't really a priority.
			/*
			// First we look for the Content-Length header
			if (!req.Headers.ContainsKey("Content-Length"))
			{
				req.WriteLengthRequiredResponse();
				return;
			}

			// Next we look for the Content-Type header
			if (!req.Headers.ContainsKey("Content-Type"))
			{
				// Without the content type we cannot determine how to do the post so 
				// we'll call this a bad request.
				req.WriteBadRequestResponse(
					"<html><h1>400 Bad Request - <b>Content-Type</b> header is required for a POST</h1></html>");
				return;
			}

			string ctVal = req.Headers["Content-Type"];
			string[] ctPieces = ctVal.Split(';');
			// Remove leading spaces on each piece
			for (int i = 0; i < ctPieces.Length; i++)
			{
				while (ctPieces[i].StartsWith(" "))
				{
					ctPieces[i] = ctPieces[i].Substring(1);
				}
			}
			// Look for "multipart/form-data" since it's all we support for the POST
			if (-1 == Array.IndexOf<string>(ctPieces, "multipart/form-data"))
			{
				req.WriteBadRequestResponse();
				return;
			}

			// Look for the boundary
			string boundary = null;
			for (int i = 0; i < ctPieces.Length; i++)
			{
				if (ctPieces[i].StartsWith("boundary="))
				{
					boundary = ctPieces[i].Substring(9);
					break;
				}
			}
			if (null == boundary)
			{
				req.WriteBadRequestResponse(
					"<html><h1>400 Bad Request - Missing form boundary data</h1></html>");
				return;
			}

			// I can't find the exacts standards document that says this, but it's obvious from tests that 
			// the actual boundary is preceded by -- every time it occurs in the body.
			boundary = "--" + boundary;

			// At this point we know the boundary so we can start downloading the body. The challenge here is that 
			// our uploader had two parts, one for the file and one for an "overwrite" option, and we may not 
			// get the overwrite field info before the file data. In addition, it isn't just as simple as 
			// boundary followed by data, there are additional headers after the boundary but before the data.
			// TODO: Address this (right now it's just saving the file data)

			// Read from the request body into a memory stream until we hit \r\n\r\n
			MemoryStream bodyMem = new MemoryStream(); //ReadToDblBreak(req.Body, out dblBreakIndex);
			if (!req.ReadBodyUpTo(r_rnrn, bodyMem))
			{
				req.WriteBadRequestResponse();
				return;
			}

			// Get the boundary and headers before the form item data
			byte[] bodyMemBuf = bodyMem.GetBuffer();
			string bodyStr = Encoding.UTF8.GetString(bodyMemBuf, 0, (int)bodyMem.Length);
			string[] lines = bodyStr.Split(new string[]{"\r\n"}, StringSplitOptions.None);

			// Make sure the body starts with the boundary
			if (0 == lines.Length || boundary != lines[0])
			{
				req.WriteBadRequestResponse(
					"<html><h1>400 Bad Request - Request body doesn't start with form boundary</h1></html>");
				return;
			}

			// Parse the lines into a dictionary of headers
			var hdrs = SimpleWebServer.ParseHeaders(lines, 1);
			// Find the Content-Disposition header, which we require
			if (!hdrs.ContainsKey("Content-Disposition"))
			{
				req.WriteBadRequestResponse(
					"<html><h1>400 Bad Request - Content-Disposition missing</h1></html>");
				return;
			}
			string disp = hdrs["Content-Disposition"];

			string fnEqualsQuote = "filename=\"";
			string formFileName;
			int i1 = disp.IndexOf(fnEqualsQuote);
			if (-1 == i1)
			{
				req.WriteBadRequestResponse(
					"<html><h1>400 Bad Request - Missing file name for file upload</h1></html>");
				return;
			}
			i1 += fnEqualsQuote.Length;
			int i2 = disp.IndexOf("\"", i1);
			if (-1 == i2)
			{
				req.WriteBadRequestResponse(
					"<html><h1>400 Bad Request - Bad file name for file upload</h1></html>");
				return;
			}
			formFileName = disp.Substring(i1, i2 - i1);

			// Convert to a local file system file name
			string fileToWrite = GetLocalFileName("/" + formFileName);
			if (null == fileToWrite)
			{
				req.WriteForbiddenResponse();
				return;
			}

			// Make sure the directory exists
			if (!Directory.Exists(Path.GetDirectoryName(fileToWrite)))
			{
				req.WriteBadRequestResponse(
					"<html><h1>400 Bad Request - Bad file name for file upload</h1></html>");
				return;
			}

			// We've read UP TO the \r\n\r\n in the body. The actual file data starts after this, 
			// so we need to read those 4 bytes first.
			byte[] junk = new byte[4];
			req.Body.Read(junk, 0, 4);

			m_logger.WriteLine("Uploading \"{0}\"...", formFileName);

			// Open a file stream to write the file
			FileStream fsOut = new FileStream(fileToWrite, FileMode.Create, FileAccess.ReadWrite);

			// After the file data stops, there's a \r\n line break and then the boundary. So our 
			// actual terminating sequence is "\r\n" + boundary
			req.ReadBodyUpTo(Encoding.UTF8.GetBytes("\r\n" + boundary), fsOut);
			fsOut.Dispose();

			// Write success response
			req.WriteHTMLResponse("<html>Uploaded file: " + formFileName + "</html>");

			m_logger.WriteLine("Done uploading \"{0}\"", formFileName);
			*/
		}

        private void HandlePut(WebRequest req)
        {
            if (!m_allowUploads)
            {
                req.WriteBadRequestResponse();
                return;
            }

            // For now, don't allow overwriting
            if (GetEntry(req.URIDecoded) != null)
            {
                req.WriteBadRequestResponse();
                //req.WriteHTMLResponse("<html>Cannot overwrite files</html>");
                return;
            }

            // Make sure it starts with the service URI then remove that part
            string toPut = req.URIDecoded;
            if (!toPut.StartsWith(ServiceURI))
            {
                req.WriteBadRequestResponse();
                return;
            }
            toPut = toPut.Substring(ServiceURI.Length);

            // Parse the path and get the directories
            string[] pieces = toPut.Split('/');
            FSDir dir = m_root;
            for (int i = 0; i < pieces.Length - 1; i++)
            {
                dir = dir.GetDir(pieces[i]);
                if (dir == null)
                {
                    req.WriteBadRequestResponse();
                    return;
                }
            }

            // By the time we get here we expected the last piece of the URI to be the file name
            FSFile theUploadedFile = dir.CreateFile(pieces[pieces.Length - 1]);
            if (null == theUploadedFile)
            {
                // If we can't create the file at this point, we'll call it a bad request (because 
                // it's most likely because of a bad file name)
                req.WriteBadRequestResponse();
                return;
            }

            // Stream in the body of request from the socket and out to the file
            Stream outS = theUploadedFile.OpenReadWrite();
            if (outS == null)
            {
                req.WriteInternalServerErrorResponse();
                return;
            }
            byte[] buf = new byte[8192];
            while (true)
            {
                int bytesRead = req.Body.Read(buf, 0, buf.Length);
                if (bytesRead <= 0) { break; }
                outS.Write(buf, 0, bytesRead);
            }
            long fileLength = outS.Length;
            outS.Dispose();
            req.WriteHTMLResponse(string.Format("<html>OK: Uploaded {0} bytes</html>", fileLength));
        }
		
		/// <summary>
		/// Request handler function
		/// </summary>
		public override void Handler(WebRequest req)
		{
			if (m_disposed)
			{
				throw new ObjectDisposedException("FileWebServer");
			}

			if ("POST" == req.Method)
			{
				HandlePost(req);
				return;
			}

            // Handle uploads
            if ("PUT" == req.Method)
            {
                HandlePut(req);
                return;
            }

			// If it's anything other than GET at this point then it's not implemented
			if (req.Method != "GET")
			{
				m_logger.WriteLine(
					"  Method was \"" + req.Method + "\" -> sending not implemented response");
				req.WriteNotImplementedResponse(
					"<html><h1>HTTP method &quot;" + req.Method + "&quot; is not implemented</h1></html>");
				return;
			}

            // First get rid of formatting on requested file
            string toGet = req.URIDecoded;

			// Requested file/content name must never contain \ (only /)
			if (toGet.Contains("\\"))
            {
                m_logger.WriteLine(
                    "  URI contains \"\\\" -> sending 400 response");
				req.WriteBadRequestResponse();
				return;
            }

			m_logger.WriteLine("Got request for \"" + toGet + "\"");

			// Make sure the URL starts with the service URL, and prodived it does, strip 
			// off that part.
			if (!toGet.StartsWith(ServiceURI))
			{
				req.WriteNotFoundResponse();
				return;
			}
			toGet = toGet.Substring(ServiceURI.Length);
            
            // Get the file or directory that we need to serve
			string[] parts = toGet.Split(
				new char[]{ '/' }, StringSplitOptions.RemoveEmptyEntries);
			FSDir dir = m_root, parent = null;
			for (int i = 0; i < parts.Length; i++)
			{
				parent = dir;
				dir = dir.GetDir(parts[i]);

				// Should we get a null directory we have 2 cases:
				// 1. This is NOT the last loop iteration, implying that we've hit a not found.
				// 2. This IS the last loop iteration, meaning that the last part of the file 
				//    string may be a file name and/or may be a not found. We fall through to 
				//    the code after this loop in that case.
				if (null == dir && i != parts.Length - 1)
				{
					if (null != m_onFileNotFound)
					{
						// Invoke custom handler
						m_onFileNotFound(this, toGet, req);
					}
					else
					{
						req.WriteNotFoundResponse();
					}
					return;
				}
			}

			// If 'dir' is non-null at this point then it's a request for a listing of files in 
			// that directory.
			if (null != dir)
			{
				m_logger.WriteLine("  Sending directory listing as response");

				// Build response and send it
				string response = m_onNeedDirList(dir);
				req.WriteHTMLResponse(response);

				// That's all we need for a directory listing
				return;
			}

			// Coming here implies a file name
			FSFile file = parent.GetFile(parts[parts.Length - 1]);

			// If it doesn't exist then we respond with a file-not-found response
			if (null == file)
			{
				if (null != m_onFileNotFound) // custom handler
				{
					m_logger.WriteLine("  File not found -> invoking external handler");
					m_onFileNotFound(this, parts[parts.Length - 1], req);
				}
				else
				{
					m_logger.WriteLine("  File not found -> sending 404 response");

					// If we don't have a handler for file-not-found then we use the generic 404
					req.WriteNotFoundResponse();
				}
				return;
			}

			// File DOES exist, so we write the file data as the response
			m_logger.WriteLine("  Sending file contents as response");
            WriteFileResponse(req, file);
		}

        private static bool HasImgExt(string fileName)
        {
            string lwr = fileName.ToLower();
            string[] imgExts = { ".jpg", ".jpeg", ".bmp", ".gif", ".png", ".tga" };
            foreach (string ext in imgExts)
            {
                if (lwr.EndsWith(ext)) { return true; }
            }
            return false;
        }

		protected static int IndexOfDblBreak(MemoryStream ms)
		{
			byte[] buf = ms.GetBuffer();
			int bufLen = (int)ms.Length;

			for (int i = 0; i < bufLen - 3; i++)
			{
				if ((byte)'\r' == buf[i])
				{
					// If the next 3 are \n\r\n then we've found it
					if ((byte)'\n' == buf[i + 1] &&
					    (byte)'\r' == buf[i + 2] &&
					    (byte)'\n' == buf[i + 3])
					{
						return i;
					}
				}
			}
			return -1;
		}

        /// <summary>
        /// Function that can be used to build the directory listing HTML. If this is set to a non-null 
		/// function then it will be called each time a directory listing needs to be made. The 
		/// callback function must return an entire html string for the page. Utility functions in this 
		/// class such as GetHREF can be used to map a file or directory to the appropriate HREF for 
		/// the page content.
        /// </summary>
		public Func<FSDir, string> OnNeedDirList
        {
            get { return m_onNeedDirList; }
            set
            {
                m_onNeedDirList = value;
                // If it has just been set to null then we switch back to our default
                if (null == m_onNeedDirList)
                {
                    m_onNeedDirList = this.DefaultBuildDirHTML;
                }
            }
        }

		/// <summary>
		/// Attempts to read at least n bytes from one stream and write it into another. On success, 
		/// true is returned. If the end of the stream that is being read from is hit before n bytes 
		/// are read, then false is returned.
		/// In the success case there will be at least n bytes that have been read from the first 
		/// stream and written into the second.
		/// In the failure case there will be some amount less than n bytes, which may or may not 
		/// be 0, that have been read from the first stream and written into the second.
		/// </summary>
		/// <returns><c>true</c>, if at least n bytes were read, <c>false</c> otherwise.</returns>
		private static bool ReadAtLeast(Stream readFrom, Stream readInto, int n)
		{
			int readSoFar = 0;
			byte[] buf = new byte[4096];
			while (readSoFar < n)
			{
				int numRead = readFrom.Read(buf, 0, buf.Length);
				if (numRead <= 0)
				{
					break;
				}
				readInto.Write(buf, 0, numRead);
				readSoFar += numRead;
			}
			return readSoFar >= n;
		}

		/// <summary>
		/// Reads from a stream into memory until the \r\n\r\n double line break has been encountered. If 
		/// this is encountered before the end of the stream and before reading the maximum number of 
		/// bytes allowed, a memory stream with all the data that was read will be returned.
		/// Otherwise null will be returned.
		/// </summary>
		private static MemoryStream ReadToDblBreak(Stream readFrom, out int dblBreakIndex, int maxBytesToRead = 100 * 1024)
		{
			MemoryStream ms = new MemoryStream();
			byte[] buf = new byte[4096];
			while (ms.Length < maxBytesToRead)
			{
				int numRead = readFrom.Read(buf, 0, buf.Length);
				if (numRead <= 0) { break; }
				ms.Write(buf, 0, numRead);

				// Look for the double line break in the memory stream
				dblBreakIndex = IndexOfDblBreak(ms);
				if (-1 != dblBreakIndex)
				{
					return ms;
				}
			}

			// We only come here if we reach end of stream or exceed read size
			// In either case we failed to read to the double break
			dblBreakIndex = -1;
			ms.Dispose();
			return null;
		}

		public override string ServiceURI
		{
			get
			{
				return "/files";
			}
		}

        private void WriteFileResponse(WebRequest req, FSFile file)
        {
			Stream fileStream = file.OpenReadOnly();
			if (null == fileStream)
			{
				m_logger.WriteLine(string.Format(
                    "{0}:ERROR:Failed to open file \"" + file.Name + "\"", DateTime.Now));

				// Send a 500. We should only enter this method when the file DOES 
				// exist, so we shouldn't treat this as a 404.
				req.WriteInternalServerErrorResponse();
				return;
			}

			// Log info before sending
			var range = req.GetRangeHeader(file.Size - 1);
			string msg = "  Sending file contents for " + file.Name;
			if (range != null)
			{
				msg += string.Format(" ({0}-{1})", range.Item1, range.Item2);
			}
			m_logger.WriteLine(msg);

			// Send the file data as a response. This function will handle the Range header, if present.
			req.WriteFileResponse(file.Name, fileStream, false);

            // Clean up
            fileStream.Dispose();
        }

	} // end FilesWebService
}
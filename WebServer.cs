// Author:
// Evan Thomas Olds
//
// Creation Date:
// November 10, 2014
//
// References:
//    http://en.wikipedia.org/wiki/Internet_media_type
//    http://en.wikipedia.org/wiki/List_of_HTTP_status_codes
//    http://www.w3.org/TR/cors/
//    https://tools.ietf.org/html/rfc5735
//    https://msdn.microsoft.com/

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using ETOF.IO;

namespace ETOF
{
	internal class WebServer : IDisposable
    {   
		/// <summary>
		/// Maximum size, in bytes, that will be allowed for the very first line of a request. Should 
		/// this amount of data (or more) be received and the received data does not have a \r\n line 
		/// break in it, then the connection will be dropped.
		/// </summary>
		private const int c_maxFirstLnByteSizeBeforeDrop = 2048;

		/// <summary>
		/// 100 KiB should be more than enough considering this is just for the size of the headers
		/// </summary>
		private const int c_maxHdrsSizeBeforeDrop = 100 * 1024;

		private bool m_acceptClients = true;
        
        private bool m_disposed = false;
        
        private TcpListener m_listener = null;
        
        private Options m_opts;

        /// <summary>
        /// Time, in milliseconds, that we allow a read call to block before terminating the call and 
        /// ultimately the connection for the request
        /// </summary>
        private int m_readTimeoutMilli = 3000;

        /// <summary>
        /// Time, in milliseconds, that we allow for a client to send us all the data up to the body 
        /// of the request. Only checked after a read call unblocks, so is expected to be signficantly 
        /// more than the single Read call timeout.
        /// </summary>
        private int m_readToBodyTimeoutMilli = 30000;

		private readonly ConcurrentBag<WebService> m_services = new ConcurrentBag<WebService>();
        
        private BlockingCollection<TcpClient> m_tasks = new BlockingCollection<TcpClient>();
        
        private Thread[] m_threads;

		public event EventHandler OnHandlerException = delegate { };

		/// <summary>
		/// Maps a file extension to a MIME content type. The extensions are all lower-case and  
		/// do NOT include the '.' character. Files with no extension must use the empty string 
		/// as the key.
		/// </summary>
		private static ConcurrentDictionary<string, string> s_fileExtContentTypes = null;

        /// <summary>
        /// Creates the server and starts listening on port specified in the options.
        /// </summary>
        public WebServer(Options options, IEnumerable<WebService> services = null)
        {
        	if (null == options)
        	{
        		throw new ArgumentNullException("options");
        	}
        	m_opts = options;

			if (m_opts.MaxThreads < 1)
			{
				throw new ArgumentException(
					"At least 1 thread is required for a SimpleWebServer object. An invalid " + 
					"maximum thread count of " + m_opts.MaxThreads.ToString() + 
					" was passed in the options to the constructor.");
			}
        	
			// A collection of services is optional. If it was provided, then add them to 
			// our list of services.
			if (null != services)
			{
				foreach (var service in services)
				{
					if (null != service)
					{
						m_services.Add (service);
					}
				}
			}
        	
            // Create and start the worker threads
        	m_threads = new Thread[m_opts.MaxThreads];
        	for (int i = 0; i < m_opts.MaxThreads; i++)
        	{
        		ParameterizedThreadStart pts = this.WorkerThreadProc;
        		m_threads[i] = new Thread(pts);
        		m_threads[i].Start(m_tasks);
        	}
            
            // Start the listener thread
        	ThreadStart ts = new ThreadStart(this.ListenThreadProc);
            Thread listenThread = new Thread(ts);
            listenThread.Start();
        }
        
        ~WebServer()
        {
        	Dispose();
        }

        /// <summary>
        /// Gets a value indicating whether or not new client connections are being accepted.
        /// </summary>
        public bool AcceptingClients
        {
			get { return m_acceptClients; }
        }

		public int ActiveHandlerThreads
		{
			get
			{
				if (null == m_threads) { return 0; }
				int count = 0;
				foreach (Thread t in m_threads)
				{
					if (t.IsAlive)
					{
						count++;
					}
				}
				return count;
			}
		}
        
        private WebRequest BuildRequest(Stream readStream, EndPoint clientEndPoint)
        {
            if (m_disposed) { throw new ObjectDisposedException(this.GetType().Name); }
            if (null == readStream) { return null; }

            // Set the read timeout for the read stream (which is likely a NetworkStream). Not all 
            // streams will support this, so we must catch a possible exception. From MSDN:
            // "If the stream does not support timing out, this property should raise an 
            // InvalidOperationException."
            try
            {
                readStream.ReadTimeout = m_readTimeoutMilli;
            }
            catch (InvalidOperationException) { }
            
            // Next we need to read everything up to the beginning of the body
            MemoryStream ms = new MemoryStream();
            string method = null;
            Stopwatch timer = new Stopwatch();
            timer.Start();
            while (true)
            {
                // If the total elapsed time exceeds our threshold, then terminate the connection (by 
                // returning null)
                if (timer.ElapsedMilliseconds >= m_readToBodyTimeoutMilli)
                {
                    return null;
                }

                int bytesRead = 0;
                byte[] buf = new byte[4096];
                try
                {
                    bytesRead = readStream.Read(buf, 0, buf.Length);
                }
                catch (Exception ex)
                {
                    // Note: Tests are showing socket exceptions for weird reasons so there may be 
                    // something that we want to do here besides just giving up and returning null.
                    // TODO: Revisit this at a later date.
                    return null;
                }

                if (0 == bytesRead)
                    return null;

                // Add to the memory stream
                ms.Write(buf, 0, bytesRead);

				// Get a reference to the memory stream buffer so we can check the data
				byte[] msBuffer = ms.GetBuffer();

				// We need at least one \r\n line break before we can process meaningful information
				int singleBreakIndex = GetCRLFIndex(msBuffer, 0, (int)ms.Length);
				if (-1 == singleBreakIndex)
				{
					// We need to make sure we don't go beyond the allowed size
					if (ms.Length >= c_maxFirstLnByteSizeBeforeDrop)
					{
						// Drop connection
						return null;
					}

					continue;
				}

				// The first line must have the method
				method = GetMethod(msBuffer);
                if (null == method) { return null; }

                // If we have a double line break then we've got all the headers
				int dblBreakIndex = GetCRLFCRLFIndex(msBuffer, 0, (int)ms.Length);
				if (dblBreakIndex >= 0)
				{
					string preDblBreak = Encoding.UTF8.GetString(msBuffer, 0, dblBreakIndex);
					string[] lines = preDblBreak.Split(
						new string[] { "\r\n" }, StringSplitOptions.None);

					// The first line should have the method, then a single space, then the URI, then 
					// another single space, then the HTTP version.
					string[] args = lines[0].Substring(method.Length + 1).Split(' ');
					if (args.Length != 2) { return null; }
					string uri = args[0];
					string version = args[1];

					// We can build the request headers
					ConcurrentDictionary<string, string> hdrs = new ConcurrentDictionary<string, string>();
					for (int i = 1; i < lines.Length; i++)
					{
						if (string.IsNullOrEmpty(lines[i]))
						{
							break;
						}
                        
						int cIndex = lines[i].IndexOf(':');
						if (-1 == cIndex)
						{
							continue;
						}

						// Make sure the colon isn't the last character
						if (cIndex == lines[i].Length - 1)
						{
							continue;
						}

						string val = lines[i].Substring(cIndex + 1);
						val.TrimStart(' ', '\t');
						hdrs[lines[i].Substring(0, cIndex)] = val;
					}

					// Note that the HTTP specification says 0 or more headers, so even if we didn't parse any headers 
					// from the request, that's still acceptable.

					// There are multiple possibilities with respect to the body of the request:
					// 1.  We have a ContentLength header
					// 1A. We've already read in the entire body into memory. If this is the case then the stream for the 
					//     Body can just be a subset of the memory stream that we've already read data into.
					// 1B. We haven't read the entire body into memory and therefore need to build a stream that 
					//     concatenates what we have read with the network stream.
					// 2.  We have no Content-Length header or we have not read the entire body into memory. Two subcases:
					// 2A. The very last 4 bytes of what we've read so far from the network stream is the double-break after 
					//     the headers and before the body. This implies that we're exactly at the beginning of the body and 
					//     can just use the network stream for the request body stream.
					// 2B. If this is not the case, then we've already read some of the request body and need to take 
					//     a chunk from memory and concatenate it with what has yet to be read from the network stream.
					Stream reqBody = null;
					long contentLength = WebRequest.GetContentLengthOrDefault(hdrs, int.MinValue);
					if (contentLength >= 0)
					{
						long totalSize = dblBreakIndex + 4 + contentLength;

                        // Coming here means the content (request body) length is known. If the server options say that 
                        // we should buffer the entire body into memory, then do so before proceeding.
                        if (m_opts.BodyMemBufferMax >= contentLength)
                        {
                            if (!TryReadFullRequest(readStream, ms, totalSize)) { return null; }
                        }

						if (ms.Length >= totalSize)
						{
							// This means that the memory stream has everything we need, but the body starts at some offset 
							// within it. We'll copy the body portion into a new memory stream.
							reqBody = ms.SubStream(dblBreakIndex + 4);
						}
						else
						{
							// First get the body bit that we've already read in
							MemoryStream msBodyStart = new MemoryStream();
							msBodyStart.Write(msBuffer, dblBreakIndex + 4, (int)(ms.Length - (dblBreakIndex + 4)));

							// Give the request this concatenated with the network stream. Make sure we create a 
							// fixed-length stream to match the content length.
							reqBody = ConcatStream.CreateReadOnlyWithFixedLength(
								msBodyStart, readStream, contentLength);
						}
					}
					if (null == reqBody) // case 2
					{
						if (dblBreakIndex == ms.Length - 4)
						{
							reqBody = readStream;
						}
						else
						{
							// First get the body bit that we've already read in
							MemoryStream msBodyStart = new MemoryStream();
							msBodyStart.Write(msBuffer, dblBreakIndex + 4, (int)(ms.Length - (dblBreakIndex + 4)));

							// Give the request this concatenated with the network stream
							reqBody = new ConcatStream(msBodyStart, readStream);
						}
					}

					// Now build the request object that we will ultimately return
					var req = new WebRequest(
						method, uri, version, hdrs,
						(clientEndPoint as IPEndPoint).Address,
						MIMETypes, reqBody, readStream);
					return req;
				}
				else
				{
					// This means we still have no double line break and therefore are still reading in 
					// headers. If we get too much header data we drop the connection.
					if (ms.Length > c_maxHdrsSizeBeforeDrop)
					{
						// Returning null causes the request to be dropped
						return null;
					}
				}
            }

            return null;
        }

        public void Dispose()
        {
        	m_disposed = true;        	
        	m_acceptClients = false;
        	
        	if (null != m_listener)
        	{
        		m_listener.Stop();
        		m_listener = null;
        	}
        	
        	// Add a null task for each thread to terminate it
        	for (int i = 0; i < m_threads.Length; i++)
        	{
        		m_tasks.Add(null);
        	}
        	
        	// Wait for each thread to complete
        	foreach (Thread t in m_threads)
        	{
        		t.Join();
        	}
        }

		private static int GetCRLFIndex(byte[] data, int startIndex, int count)
		{
			for (int i = 0; i < count - 1; i++)
			{
				if (data[i + startIndex] == (byte)'\r' &&
				    data[i + startIndex + 1] == (byte)'\n')
				{
					return i + startIndex;
				}
			}
			return -1;
		}

		/// <summary>
		/// Gets the starting byte index of a double line break.
		/// </summary>
		private static int GetCRLFCRLFIndex(byte[] data, int startIndex, int count)
		{
			for (int i = 0; i < count - 3; i++)
			{
				if (data[i + startIndex] == (byte)'\r' &&
					data[i + startIndex + 1] == (byte)'\n' &&
					data[i + startIndex + 2] == (byte)'\r' &&
					data[i + startIndex + 3] == (byte)'\n')
				{
					return i + startIndex;
				}
			}
			return -1;
		}

        private static string GetMethod(byte[] requestData)
        {
            // The first line of an HTTP requested is expected to start with the HTTP 
            // method name.
            // We'll see if it's one that we know.
            // Note that HTTP 1.1 added: OPTIONS, PUT, DELETE, TRACE and CONNECT
			string[] methods = { "GET", "POST", "HEAD", "OPTIONS", "PUT", "DELETE", "TRACE", "CONNECT" };
			foreach (string method in methods)
			{
				if (method.Length > requestData.Length) { continue; }

				// Go through characters in method string and compare as bytes to request data
				bool allMatch = true;
				for (int i = 0; i < method.Length; i++)
				{
					if ((byte)method[i] != requestData[i])
					{
						allMatch = false;
						break;
					}
				}

				// If we have a match then return
				if (allMatch) { return method; }
			}
            
            // If it's not recognized then we return null
            return null;
        }

		/// <summary>
		/// Checks to see if the IP address is a loopback IP. Only IPv4 and IPv6 addresses are supported.
		/// </summary>
		public static bool IsLoopbackIP(IPAddress addr)
		{
			// Only IPv4 and IPv6 are supported

			if (System.Net.Sockets.AddressFamily.InterNetwork == addr.AddressFamily)
			{
				// From https://tools.ietf.org/html/rfc5735:
				// 127.0.0.0/8 - This block is assigned for use as the Internet host
				// loopback address.  A datagram sent by a higher-level protocol to an
				// address anywhere within this block loops back inside the host.

				byte[] addrBytes = addr.GetAddressBytes();
				return 127 == addrBytes[0];
			}
			else if (System.Net.Sockets.AddressFamily.InterNetworkV6 == addr.AddressFamily)
			{
				byte[] addrBytes = addr.GetAddressBytes();
				if (8 != addrBytes.Length) { return false; }
				if (addrBytes[0] == 1)
				{
					// All the remaining must be 0
					for (int i = 1; i < 8; i++)
					{
						if (addrBytes[i] != 0)
						{
							return false;
						}
					}
					return true;
				}
				return false;
			}

			return false;
		}

		/// <summary>
		/// Utility function to check whether or not an IP address is reserved for private networks. If 
		/// it is specifically designated as a private network IP address then true is returned. If not 
		/// then false is returned. Note that "localhost" and other loopback addresses ARE included in 
		/// the category of private IP addresses and so true will be returned for them.
		/// Current supports only IPv4 addresses - false will be returned if the addr parameter is not 
		/// an IPv4 address. (planning to also support IVv6 in future versions)
		/// </summary>
		public static bool IsPrivateIP(IPAddress addr)
		{
			// For now we're only supporting IPv4
			if (System.Net.Sockets.AddressFamily.InterNetwork != addr.AddressFamily)
			{
				return false;
			}

			// From https://tools.ietf.org/html/rfc5735:
			//
			// 10.0.0.0/8 - This block is set aside for use in private networks.
			// Its intended use is documented in [RFC1918].  As described in that
			// RFC, addresses within this block do not legitimately appear on the
			// public Internet.
			//
			// 127.0.0.0/8 - This block is assigned for use as the Internet host
			// loopback address.  A datagram sent by a higher-level protocol to an
			// address anywhere within this block loops back inside the host.
			//
			// 192.168.0.0/16 - This block is set aside for use in private networks.
			// Its intended use is documented in [RFC1918].  As described in that
			// RFC, addresses within this block do not legitimately appear on the
			// public Internet.  These addresses can be used without any
			// coordination with IANA or an Internet registry.

			byte[] addrBytes = addr.GetAddressBytes();
			if (10 == addrBytes[0] || 127 == addrBytes[0])
			{
				return true;
			}
			return addrBytes[0] == 192 && addrBytes[1] == 168;
		}

        private void ListenThreadProc()
        {
            m_listener = new TcpListener(IPAddress.Any, m_opts.Port);

            try
            {
                // Start listening for incoming connections
                lock (m_listener)
                {
                    m_listener.Start();
                }
			}
			catch (SocketException)
			{
				// If we can't start listening then we can't do anything
				m_acceptClients = false;
				return;
			}

            while (m_acceptClients)
            {
                try
				{
					TcpClient client = m_listener.AcceptTcpClient();

                    client.NoDelay = true;

					// Add the client to the tasks list and one of the worker threads 
					// will process it when available.
					m_tasks.Add(client);
				}
				catch (SocketException)
				{
					// We could fail to accept a client if the network goes down or something like 
					// that. But we want the server to recover from this if the network connectivity 
					// is restored. Since it could go down and stay down we don't want a busy-waiting 
					// loop, so we wait 2 seconds before trying again.
					Thread.Sleep(2000);
				}
            }
        }

		public IPAddress LocalIPAddr
		{
			get
			{
				var network_interfaces = NetworkInterface.GetAllNetworkInterfaces();

				int attempt = 0;

				while (attempt < 2)
				{
					foreach (NetworkInterface item in network_interfaces)
					{
						// We only care about interfaces that are up
						if (0 == attempt && item.OperationalStatus != OperationalStatus.Up) { continue; }

						foreach (UnicastIPAddressInformation ip in item.GetIPProperties().UnicastAddresses)
						{
							if (ip.Address.AddressFamily == AddressFamily.InterNetwork &&
							   !IPAddress.Loopback.Equals(ip.Address) &&
							   !IPAddress.IPv6Loopback.Equals(ip.Address))
							{
								//var ipbytes = ip.Address.GetAddressBytes(); // DEBUG
								return ip.Address;
							}
						}
					}
					attempt++;
				}
				throw new Exception();
			}
		}

        /// <summary>
        /// Maps a file extension to a MIME content type. The extensions are all lower-case and  
        /// do NOT include the '.' character. Files with no extension must use the empty string 
        /// as the key.
        /// </summary>
        public static ConcurrentDictionary<string, string> MIMETypes
        {
            get
			{
				if (null == s_fileExtContentTypes)
				{
					// Initialize on first access
                    s_fileExtContentTypes = new ConcurrentDictionary<string, string>();

					// Make a list of some known (file extension, content type) pairs
					// Keep this list alphabetized by extension
					s_fileExtContentTypes.TryAdd("avi", "video/avi");
					s_fileExtContentTypes.TryAdd("bmp", "image/bmp");
					s_fileExtContentTypes.TryAdd("flv", "video/x-flv");
					s_fileExtContentTypes.TryAdd("gif", "image/gif");
					s_fileExtContentTypes.TryAdd("htm", "text/html");
					s_fileExtContentTypes.TryAdd("html", "text/html");
					s_fileExtContentTypes.TryAdd("jpg", "image/jpeg");
					s_fileExtContentTypes.TryAdd("jpeg", "image/jpeg");
					s_fileExtContentTypes.TryAdd("mkv", "video/x-matroska");
					s_fileExtContentTypes.TryAdd("mpeg", "video/mpeg");
                    s_fileExtContentTypes.TryAdd("mpg", "video/mpeg");
					s_fileExtContentTypes.TryAdd("mp3", "audio/mpeg3");
					s_fileExtContentTypes.TryAdd("mp4", "video/mp4");
					s_fileExtContentTypes.TryAdd("m4v", "video/mp4");
					s_fileExtContentTypes.TryAdd("pdf", "application/pdf");
					s_fileExtContentTypes.TryAdd("png", "image/png");
					s_fileExtContentTypes.TryAdd("txt", "text/plain");
				}
				return s_fileExtContentTypes;
			}
        }

		/// <summary>
		/// Gets the collection of web services for this server. These services allow requests to be delegated 
		/// to appropriate handlers.
		/// </summary>
		public ConcurrentBag<WebService> Services
		{
			get { return m_services; }
		}

		public static string URLDecode(string encodedURL)
		{
			string[] reserved =
            {
				" ", "!", "#", "$", "&", "'", "(", ")", "*", "+",
				",", "/", ":", ";", "=", "?", "@", "[", "]"
			};
			string[] replacements =
            {
				"%20", "%21","%23","%24","%25","%27","%28","%29","%2A","%2B",
				"%2C","%2F","%3A","%3B","%3D","%3F","%40","%5B","%5D",
			};

			// Since we're decoding we'll find the replacement and repace with 
			// the reserved.
			string s = encodedURL;
			for (int i = 0; i < replacements.Length; i++)
			{
				s = s.Replace(replacements[i], reserved[i]);
			}

			return s;
		}

        /// <summary>
        /// Attempts to read from readStream and write data into the memory stream until the length 
        /// of the memory stream is greater than or equal to the target size.
        /// </summary>
        private bool TryReadFullRequest(Stream readStream, MemoryStream ms, long targetSize)
        {
            byte[] buf = new byte[8192];
            while (ms.Length < targetSize)
            {
                int readCount = Math.Min(buf.Length, (int)(targetSize - ms.Length));
                try
                {
                    int amountReceived = readStream.Read(buf, 0, readCount);
                    if (amountReceived <= 0)
                    {
                        // This means we're at the end of the stream. We wouldn't be in this 
                        // loop if we'd already read enough bytes, so this is an indication 
                        // of failure to read the requested amount.
                        return false;
                    }

                    // Write the data that was read to the memory stream
                    ms.Write(buf, 0, amountReceived);
                }
                catch (Exception) { return false; }
            }

            // Coming here implies we read to (at least) the requested size
            return true;
        }
        
        private void WorkerThreadProc(object taskListObject)
        {
			// Get a reference to the task list
        	BlockingCollection<TcpClient> tasks =
				taskListObject as BlockingCollection<TcpClient>;
        	
        	// The worker threads stay in a loop
			while (true)
			{
				TcpClient client = tasks.Take();
				
				// Special case if null -> this tells us to break the loop and thus 
				// end the thread execution.
				if (null == client) { break; }

				// We will perform several operations that could potentially throw exceptions, hence 
				// the large try block that follows.
				NetworkStream ns = null;
				IPAddress clientAddr = null;
				try
				{
					// First we need to validate the client IP address
					var end = client.Client.RemoteEndPoint;
					clientAddr = ((IPEndPoint)end).Address;
                    if (!m_opts.AllowConnection(clientAddr))
                    {
                        continue;
                    }
					
					// Next get the network stream from the TCP client
	                ns = client.GetStream();
				}
				catch (Exception)
				{
					client.Close();
					continue;
				}
				if (null == ns)
				{
					client.Close();
					continue;
				}
                
                // Build the request object
				WebRequest request = BuildRequest(ns, client.Client.RemoteEndPoint);
                
                // If it's non-null then we look for a service to handle the request
                if (null != request)
                {
					bool handled = false;
					foreach (WebService service in m_services)
					{
						if (request.URI.StartsWith(service.ServiceURI))
						{
							// The service can request to only get requests from same machine
							if (service.AllowConnectionsOnlyFromThisMachine)
							{
								if (!IsLoopbackIP(clientAddr))
								{
									// This service can't handle it, move on to try others
									continue;
								}
							}

							// Try to use the handler
							try { service.Handler(request); }
							catch (Exception e)
							{
								// Give a server error response
								request.WriteInternalServerErrorResponse(
									"<html>Internal server error<br>Exception details:<br>" + e.Message + "</html>");

								// Invoke the event for handler exception
								OnHandlerException(
									this,
									new HandlerExceptionEventArgs(service, e.Message));
							}
							handled = true;
							break;
						}
					}

					if (!handled)
					{
						// If we don't have a service handler then we use the generic 404
						request.WriteNotFoundResponse();
					}
                }
                
                // Dispose the network stream and TCP client
                ns.Dispose();
                client.Close();
			}
        }

		#region Nested class declarations

		public class HandlerExceptionEventArgs : EventArgs
		{
			public readonly WebService Handler;

			public readonly string Message;

			public HandlerExceptionEventArgs(WebService handler, string message)
			{
				Handler = handler;
				Message = message;
			}
		}

		public class Options
		{
			private Func<IPAddress, bool> m_ipValidator = null;

			private int m_maxThreads = 16;

			private int m_port = 80;

            private readonly int r_bodyMemBufferMax;

			public Options(
				int port, Func<IPAddress, bool> allowConnectionQueryFunc = null)
			{
				m_port = port;
				m_ipValidator = allowConnectionQueryFunc;
                r_bodyMemBufferMax = 1024 * 1024;
			}

            public Options(int port, int bodyMemBufferMax)
            {
            	m_port = port;
                r_bodyMemBufferMax = Math.Max(bodyMemBufferMax, 1024);
            }

			/// <summary>
			/// Constructs the Options object with a port number, a maximum number of threads, and 
			/// an optional connection-querying function. 
			/// If any non-positive value is specified for the maximum number of threads, 1 is used 
			/// instead. It is highly recommended that this value be at least equal to the number 
			/// of cores on the system.
			/// </summary>
			public Options(int port, int maxThreads,
				Func<IPAddress, bool> allowConnectionQueryFunc = null)
			{
				m_port = port;
				m_maxThreads = Math.Max(1, maxThreads);
				m_ipValidator = allowConnectionQueryFunc;
			}

			public bool AllowConnection(System.Net.IPAddress address)
			{
				if (null == m_ipValidator)
				{
					return true;
				}
				return m_ipValidator(address);
			}

            /// <summary>
            /// Returns a size, in bytes, indicating the maximum body memory buffer size. For 
            /// any requests with a known body size that is less than or equal to this size, 
            /// the entire body will be read into memory before the request is sent to a 
            /// handler. This value is guaranteed to be at least 1024 bytes. The default 
            /// is (1024*1024) bytes.
            /// </summary>
            public int BodyMemBufferMax
            {
                get { return r_bodyMemBufferMax; }
            }

			/// <summary>
			/// Gets the maximum number of threads that the server should use to handle 
			/// requests. The default is 16.
			/// </summary>
			public int MaxThreads
			{
				get { return m_maxThreads; }
			}

			public int Port
			{
				get { return m_port; }
			}
		}

		#endregion
	} // end of WebServer class

	public class WebRequest
	{
		private bool m_abort = false;

		private bool m_allowAsCrossOrigin = true;

		private Stream m_body;

		private ConcurrentDictionary<string, string> m_extContentTypes;

		private ConcurrentDictionary<string, string> m_headers;

        private readonly ConcurrentDictionary<string, string> r_headersLwr = 
            new ConcurrentDictionary<string, string>();

		private string m_method;

        private Stream m_response;

		private readonly IPAddress r_ipAddr;

		private readonly string r_uri;

		internal WebRequest(string method, string uri, string version, ConcurrentDictionary<string, string> headers,
			IPAddress ipAddress, ConcurrentDictionary<string, string> extContentTypes, Stream body,
			Stream responseStream)
		{
			m_method = method;
			r_uri = uri;
			m_headers = headers;
			m_extContentTypes = extContentTypes;
			r_ipAddr = ipAddress;
			m_body = body;
			m_response = responseStream;

            // Build dictionary with lower-case header names
            foreach (KeyValuePair<string, string> kvp in headers)
            {
                // RFC 7230 section 3.2 states:
                // Each header field consists of a case-insensitive field name followed by a colon (":"), optional 
                // leading whitespace, the field value, and optional trailing whitespace.
                //
                // Therefore, we will trim off leading and trailing whitespace from the values
                r_headersLwr[kvp.Key.ToLower()] = kvp.Value.Trim();
            }
		}

		/// <summary>
		/// Sets a flag indicating that the request should abort any currently pending transfers
		/// </summary>
		private void Abort()
		{
			m_abort = true;
		}

		public bool AllowAsCrossOrigin
        {
            get { return m_allowAsCrossOrigin; }
            set { m_allowAsCrossOrigin = value; }
        }

		/// <summary>
		/// Gets a reference to a stream that is the body of the request. The stream will most likely 
		/// not support seeking or querying of length. The server will provide a stream that supports 
		/// querying of length only if the Content-Length header was present in the request.
		/// May be a MemoryStream if the entire request body has been read into memory. 
		/// </summary>
		public System.IO.Stream Body
        {
            get { return m_body; }
        }

		public string ClientIPAddressString
        {
            get { return r_ipAddr.ToString(); }
        }

		/// <summary>
		/// Looks for the Content-Length header within the headers collection and, if it exists 
		/// and can be parsed to an integer value, then that value is returned.
		/// If the header is not present or contains a non-integer-parsable value, then the 
		/// default value specified as a parameter is returned.
		/// </summary>
		public long GetContentLengthOrDefault(long defaultValue)
		{
			return GetContentLengthOrDefault(m_headers, defaultValue);
		}

		/// <summary>
		/// Looks for the Content-Length header within the headers collection and, if it exists 
		/// and can be parsed to an integer value, then that value is returned.
		/// If the header is not present or contains a non-integer-parsable value, then the 
		/// default value specified as a parameter is returned.
		/// </summary>
		public static long GetContentLengthOrDefault(ConcurrentDictionary<string, string> headers, long defaultValue)
		{
            // RFC 7230 section 3.2 states:
            // Each header field consists of a case-insensitive field name followed by a colon (":"), optional 
            // leading whitespace, the field value, and optional trailing whitespace.

            // TODO: Change this since now I've added r_headersLwr

            // This is why we need to go through all entries in the dictionary and compare keys as lower-case
            foreach (KeyValuePair<string, string> kvp in headers)
            {
                if (kvp.Key.ToLower() == "content-length")
                {
                    long len;
                    if (long.TryParse(kvp.Value, out len))
                    {
                        return len;
                    }
                    return defaultValue;
                }
            }
			return defaultValue;
		}

		/// <summary>
		/// Parses the byte range header if present and returns the result as a (start,end) tuple. If 
		/// the header is not present or cannot be parsed then null is returned. 
		/// Since the specification allows the range to omit the end byte index, we need a value to 
		/// use in this case. This is what the maxEndByteIndex parameter is for.
		/// </summary>
		public Tuple<long, long> GetRangeHeader(long maxEndByteIndex)
		{
            // Look for the "range" header
            if (!r_headersLwr.ContainsKey("range"))
            {
                return null;
            }

            string rangeHdrVal = r_headersLwr["range"];

            // Make sure it starts with "bytes="
            if (!rangeHdrVal.StartsWith("bytes=")) { return null; }

			// Get what's after "bytes="
			rangeHdrVal = rangeHdrVal.Substring (6);
			int index = rangeHdrVal.IndexOf ('-');
			if (-1 != index)
            {
				long startByteIndex, endByteIndex;
				if (!long.TryParse (rangeHdrVal.Substring (0, index), out startByteIndex))
                {
					return null;
				}
				if (index == rangeHdrVal.Length - 1)
                {
					// The range header is allowed to have nothing after the '-'
					endByteIndex = maxEndByteIndex;
				}
                else
                {
					if (!long.TryParse (rangeHdrVal.Substring (index + 1), out endByteIndex))
                    {
						return null;
					}
				}
				return new Tuple<long, long> (startByteIndex, endByteIndex);
			}

            return null;
		}

		public string Method
		{
			get { return m_method; }
		}

        public bool HasContentLengthHeader
        {
            get { return r_headersLwr.ContainsKey("content-length"); }
        }

		public ConcurrentDictionary<string, string> Headers
		{
			get { return m_headers; }
		}

		/// <summary>
		/// Reads from the body stream up to the point when a specific termination pattern is encountered or the end 
		/// of the stream has been reached. If the pattern is present, the body stream is read right up to the point 
		/// where it starts. So after calling this method, the body stream position will either be at the end of the 
		/// stream and the next read would return 0, or the next read would read in the termination pattern.
		/// Example:
		/// If the body stream at the current position contained:
		/// Hello world!ABCHello again world!
		/// and this was called with the termination pattern: ABC
		/// Then "Hello world!" would be written into the stream specified by the second parameter and the body 
		/// stream at the current position after this would contain:
		/// ABCHello again world!
		/// </summary>
		/// <returns>
		/// True if it stopped before the termination pattern, false if it stopped at the end of the stream.
		/// </returns>
		public bool ReadBodyUpTo(byte [] terminationPattern, Stream readIntoThis)
		{
			// Special case if the body is a memory stream
			if (Body is MemoryStream) {
				return ReadMSBodyUpTo (terminationPattern, readIntoThis);
			}

			// Remember the position at start
			long bodyPosAtStart = Body.Position;

			// Keep track of how many bytes we write to 'readIntoThis'
			long bytesWritten = 0;

			SearchBuf search = new SearchBuf (terminationPattern, false);

			// Obviously we have to read in the termination pattern eventually in order to be able to recognize it. 
			// But we have the ability to reassign the body stream, so when we read in the termination pattern 
			// we'll write everything before it to the "readIntoThis" stream and then take what we've read after 
			// that and put it into a memory stream that will be concatenated with the network stream.

			ConcatStream bodyAsConcat = Body as ConcatStream;
			NetworkStream ns = bodyAsConcat.StreamB as NetworkStream;
			if (null == ns) {
				// This should never happen, so this is primarily a message for developers if some change should 
				// break the guarantee that the body stream is either a memory stream or a concat stream that 
				// has a network stream as the second stream.
				throw new InvalidOperationException (
					"Body stream for request was expected to consist of a concatenation of a memory stream " +
					"and a network stream, but the second stream is not a network stream!");
			}

			byte [] buf = new byte [Math.Max (8192, terminationPattern.Length * 2)];
			MemoryStream pending = new MemoryStream ();
			while (true)
            {
				int bytesRead = Body.Read (buf, 0, buf.Length);
				if (0 == bytesRead) {
					// This means we hit the end of the stream
					// Write pending content and return false
					WriteAll (pending, readIntoThis);
					return false;
				}

				// After a successful read, first add the content to the memory stream
				pending.Write (buf, 0, bytesRead);
				// If we have < termination pattern length then continue to read more
				if (pending.Length < terminationPattern.Length) {
					continue;
				}

				// Search what's in memory for the termination pattern
				byte [] pendingBuf = pending.GetBuffer ();
				int index = search.GetIndex (pendingBuf, 0, (int)pending.Length);

				// If we found it we need to finish up
				if (index >= 0) {
					// First write all pending content before the terminating pattern to the output
					readIntoThis.Write (pendingBuf, 0, index);
					bytesWritten += index;

					// Next create a memory stream that starts where the terminating pattern starts
					pending = pending.SubStream (index);

					// Now update the body stream as Concat(pending, networkStream)
					if (bodyAsConcat.IsFixedLength) {
						m_body = ConcatStream.CreateReadOnlyWithFixedLength (
							pending, ns, bodyAsConcat.Length - bodyPosAtStart - bytesWritten);
					} else {
						// Nothing else we can do here
						m_body = new ConcatStream (pending, ns);
					}

					return true;
				}

				// If the termination pattern isn't present, then write all the data from the memory 
				// stream to "readIntoThis" except for (terminationPattern.Length - 1) bytes, then 
				// truncate the memory stream appropriately.
				int bytesToLeave = terminationPattern.Length - 1;
				readIntoThis.Write (pendingBuf, 0, (int)(pending.Length - bytesToLeave));
				bytesWritten += (pending.Length - bytesToLeave);
				pending = pending.SubStream ((int)(pending.Length - bytesToLeave));
			}
		}

		private bool ReadMSBodyUpTo(byte [] terminationPattern, Stream readIntoThis)
		{
			MemoryStream ms = Body as MemoryStream;
			SearchBuf search = new SearchBuf (terminationPattern, false);

			// Search for the termination pattern at the current position in the Body stream
			byte [] buf = ms.GetBuffer ();
			int index = search.GetIndex (buf, (int)ms.Position, (int)ms.Length);

			// Two cases: either we found it or we didn't
			if (-1 == index)
            {
				// This means we didn't find it so we must read the entire body into 
				// 'readIntoThis' and then return false.
				WriteAll (ms, readIntoThis);
				return false;
			}

			// If we did find it then we write up to the terminating pattern
			readIntoThis.Write (buf, (int)ms.Position, index - (int)ms.Position);
			// We also need to advance the stream's position
			ms.Position = index;
			// Success
			return true;
		}

        #if DEBUG
        public Stream Response
        {
            get { return m_response; }
        }
        #endif

		/// <summary>
		/// Gets the URI for the request, sometimes also referred to as the request target.
		/// </summary>
		public string URI
        {
            get { return r_uri; }
        }

		/// <summary>
		/// Gets the percent-decoded URI for the request
		/// </summary>
		public string URIDecoded
		{
			get { return WebServer.URLDecode(r_uri); }
		}

		private static void WriteAll(Stream source, Stream destination)
		{
			byte [] buf = new byte [4096];
			while (true) {
				int bytesRead = source.Read (buf, 0, buf.Length);
				if (0 == bytesRead) {
					return;
				}
				destination.Write (buf, 0, bytesRead);
			}
		}

		public void WriteAllowedMethodsOPTIONSResponse (bool allowPost, bool allowPut, bool allowDelete)
		{
			// Need to find out if there was an Access-Control-Request-Headers we need a response for it
			string extra = string.Empty;
			if (m_headers.ContainsKey ("Access-Control-Request-Headers"))
            {
				extra = "Access-Control-Allow-Headers: " + m_headers ["Access-Control-Request-Headers"];
			}

			// Note: we're assuming GET is always allowed here

			string allowed;
			if (allowPost)
            {
				if (allowPut)
                {
					allowed = allowDelete ? "GET, POST, PUT, DELETE" : "GET, POST, PUT";
				}
                else
                {
					allowed = allowDelete ? "GET, POST, DELETE" : "GET, POST";
				}
			}
            else
            {
				if (allowPut)
                {
					allowed = allowDelete ? "GET, PUT, DELETE" : "GET, PUT";
				}
                else
                {
					allowed = allowDelete ? "GET, DELETE" : "GET";
				}
			}
			byte [] response = Encoding.UTF8.GetBytes (
				"HTTP/1.1 200 OK\r\n" +
				//"Content-Type: text/html\r\n"+
				"Access-Control-Allow-Origin: *\r\n" +
				extra + "\r\n" +
				"Access-Control-Allow-Methods: " + allowed +
				"\r\n\r\n");
			m_response.Write (response, 0, response.Length);
		}

		public void WriteBadRequestResponse ()
		{
			WriteBadRequestResponse (null);
		}

		/// <summary>
		/// Writes a "400 Bad Request" response.
		/// </summary>
		public void WriteBadRequestResponse(string optionalPageHTML)
		{
			if (string.IsNullOrEmpty(optionalPageHTML))
            {
				optionalPageHTML = "<html><h1>400 Bad Request</h1></html>";
			}
			WriteHTMLResponse("400 Bad Request", optionalPageHTML);
		}

		/// <summary>
		/// Writes a "400 Bad Request" response, with an XML content type response.
		/// </summary>
		public void WriteBadRequestXMLResponse(string xmlString)
		{
			WriteXMLResponse("400 Bad Request", xmlString);
		}

		public void WriteForbiddenResponse()
		{
			WriteForbiddenResponse (null);
		}

		public void WriteForbiddenResponse(string optionalPageHTML)
		{
			if (string.IsNullOrEmpty (optionalPageHTML)) {
				optionalPageHTML = "<html><h1>403 Forbidden</h1></html>";
			}
			WriteHTMLResponse ("403 Forbidden", optionalPageHTML);
		}

		/// <summary>
		/// Writes a file response with proper HTTP reponse codes and headers, followed by 
		/// all the data in the stream. The stream must support seeking and the Length 
		/// property. The entire stream is sent, implying that first a seek is made to the 
		/// beginning of it.
		/// </summary>
		public bool WriteGenericFileResponse(Stream fileData)
		{
			return WriteStreamResponse("application/octet-stream", fileData, null);
		}

		public bool WriteHTMLResponse (string htmlString)
		{
			return WriteHTMLResponse("200 OK", htmlString);
		}

		private bool WriteHTMLResponse(string statusString, string htmlString)
		{
			int byteSize = Encoding.UTF8.GetByteCount(htmlString);
			byte[] response = Encoding.UTF8.GetBytes(
				"HTTP/1.1 " + statusString + "\r\n" +
				"Content-Type: text/html\r\n" +
				(m_allowAsCrossOrigin ? "Access-Control-Allow-Origin: *\r\n" : "") +
				"Content-Length: " + byteSize.ToString() +
				"\r\n\r\n" + htmlString);
            try
            {
                // We absolutely need to have all exceptions caught here. This is going to be 
                // called on one of the response threads, which are intended to stay alive for 
                // the duration of the parent server (at least that's the design at the time of 
                // this writing). So we risk killing a response thread if we let an exception 
                // bubble up unhandled.
                m_response.Write(response, 0, response.Length);
            }
            catch (Exception) { return false; }
			return true;
		}

		public bool WriteInternalServerErrorResponse()
		{
			return WriteInternalServerErrorResponse ("<html><h1>500 Internal Server Error</h1></html>");
		}

		public bool WriteInternalServerErrorResponse(string htmlContent)
		{
			if (null == htmlContent)
			{
				htmlContent = "<html><h1>500 Internal Server Error</h1></html>";
			}
			byte [] response = Encoding.UTF8.GetBytes(
				"HTTP/1.1 500 Internal Server Error\r\n\r\n" + htmlContent);
			try
			{
				m_response.Write (response, 0, response.Length);
			}
			catch (Exception) { return false; }
			return true;
		}

		/// <summary>
		/// Writes a file as the response, attempting to determine the content-type by 
		/// using the file name. The file name is provided for the purpose of determining 
		/// the content type and file name information in the response headers. In other 
		/// words the file name is not used to load anything from disk. The stream is 
		/// expected to contain all the file data that is to be written as the body of 
		/// the response.
		/// </summary>
		public bool WriteFileResponse(string fileName, Stream fileData, bool wantClientToSaveToDisk)
		{
			// First get the file extension
			int lastIndex = fileName.LastIndexOf ('.');
			string ext = string.Empty;
			if (-1 != lastIndex) {
				ext = fileName.Substring (lastIndex + 1).ToLower ();
			}

			// See if the dictionary has a corresponding content type
			string ct = "application/octet-stream";
			if (m_extContentTypes.ContainsKey (ext)) {
				ct = m_extContentTypes [ext];
			}

			// From http://www.w3.org/Protocols/rfc2616/rfc2616-sec19.html:
			// The Content-Disposition response-header field has been proposed as a means for the origin 
			// server to suggest a default filename if the user requests that the content is saved to a file
			string [] hdrs = null;
			if (wantClientToSaveToDisk) {
				hdrs = new string [] {
						string.Format(
							"Content-Disposition: attachment; filename=\"{0}\"",
							Path.GetFileName(fileName))
					};
			};

			return WriteStreamResponse (ct, fileData, hdrs);
		}

		public void WriteLengthRequiredResponse ()
		{
			WriteHTMLResponse ("411 Length Required", "<html><h1>411 Length Required</h1></html>");
		}

		/// <summary>
		/// Writes a "404 Not Found" response.
		/// </summary>
		public void WriteNotFoundResponse(string optionalPageHTML = null)
		{
			if (string.IsNullOrEmpty(optionalPageHTML)) {
				optionalPageHTML = "<html><h1>404 Not Found</h1></html>";
			}
			WriteHTMLResponse ("404 Not Found", optionalPageHTML);
		}

		public void WriteNotImplementedResponse ()
		{
			WriteNotImplementedResponse (null);
		}

		public void WriteNotImplementedResponse (string optionalPageHTML)
		{
			if (string.IsNullOrEmpty (optionalPageHTML)) {
				optionalPageHTML = "<html><h1>501 Not Implemented</h1></html>";
			}
			WriteHTMLResponse ("501 Not Implemented", optionalPageHTML);
		}

		/// <summary>
		/// Writes a response for the request that includes the stream data, or a subset of it, in 
		/// the response body. If the Range header was included in the headers for this request 
		/// then the appropriate subset of the stream will written as the response.
		/// True is returned if all data was sent successfully, false is returned otherwise. Note 
		/// that if the request is aborted before all data is sent succesfully then false will be 
		/// returned in this case.
		/// </summary>
		private bool WriteStreamResponse(string contentType, Stream data, string[] extraHeaders)
		{
			// Make sure we can seek and query the stream length
			long fileByteSize = -1;
            try
            {
                data.Position = 0;
                fileByteSize = data.Length;
            }
            catch (Exception) { return false; }

			StringBuilder sb;

			// Look for the "Range" header
			Tuple<long, long> range = GetRangeHeader(data.Length - 1);
			if (null == range)
            {
				sb = new StringBuilder("HTTP/1.1 200 OK");
            }
            else
            {
				long startByteIndex = range.Item1;
				long endByteIndex = range.Item2;
				data.Position = startByteIndex;
				fileByteSize = endByteIndex - startByteIndex + 1;

				// Make sure we have the correct response code and a Content-Range header
				sb = new StringBuilder ("HTTP/1.1 206 Partial Content\r\n" +
					"Content-Range: bytes " + data.Position.ToString () + "-" +
					endByteIndex.ToString () + "/" + data.Length.ToString ());
			}

			// Append required headers
			sb.Append("\r\nContent-Type: " + contentType +
				"\r\nContent-Length: " + fileByteSize.ToString());
			if (m_allowAsCrossOrigin)
            {
				sb.Append ("\r\nAccess-Control-Allow-Origin: *");
			}

			// Append extra headers if need be
			if (null != extraHeaders)
            {
				foreach (string header in extraHeaders)
                {
					sb.Append ("\r\n" + header);
				}
			}

			// Write the non-stream-data descriptive stuff first
			sb.Append("\r\n\r\n");
			byte [] begin = Encoding.UTF8.GetBytes(sb.ToString ());
			try { m_response.Write (begin, 0, begin.Length); }
            catch (Exception) { return false; }

			// Then write the file data (use fileByteSize to keep track of how many 
			// bytes we have left to write)
			byte [] buf = new byte [1024];
			while (fileByteSize > 0)
            {
				// Stop if abort was requested and return false according to specification
				if (m_abort)
                {
					m_abort = false; // reset abort flag
					return false;
				}

				int bytesRead = data.Read(buf, 0, buf.Length);
				if (bytesRead <= 0) { break; }
				try
                {
					if (fileByteSize >= bytesRead)
                    {
						m_response.Write(buf, 0, bytesRead);
						fileByteSize -= bytesRead;
					}
                    else
                    {
						// Last write
						m_response.Write(buf, 0, (int)fileByteSize);
						break;
					}
				}
                catch (Exception) { return false; }
			}

			return true;
		}

		public bool WriteXMLResponse(string xmlString)
		{
			return WriteXMLResponse("200 OK", xmlString);
		}

		private bool WriteXMLResponse(string statusString, string xmlString)
		{
			int byteSize = Encoding.UTF8.GetByteCount(xmlString);
			byte[] response = Encoding.UTF8.GetBytes(
				"HTTP/1.1 " + statusString + "\r\n" +
				"Content-Type: application/xml\r\n" +
				(m_allowAsCrossOrigin ? "Access-Control-Allow-Origin: *\r\n" : "") +
				"Content-Length: " + byteSize.ToString() +
				"\r\n\r\n" + xmlString);
			try
			{
				// We absolutely need to have all exceptions caught here. This is going to be 
				// called on one of the response threads, which are intended to stay alive for 
				// the duration of the parent server (at least that's the design at the time of 
				// this writing). So we risk killing a response thread if we let an exception 
				// bubble up unhandled.
				m_response.Write(response, 0, response.Length);
			}
			catch (Exception) { return false; }
			return true;
		}

	} // end of WebRequest class

	internal abstract class WebService
	{
		/// <summary>
		/// When overridden to return true (the default is false) in an inheriting class, the server will 
		/// only pass requests to this server if they came from the same machine. This is severely 
		/// restrictive and is offered for special circumstances such as:
		/// - administrative services that you don't want to be accessible on any machine other than the 
		///   machine that's running the actual server
		/// - unit testing where you want to ensure that other connections cannot come from other machines
		/// There can be other scenarios as well. The above 2 are just examples.
		/// </summary>
		public virtual bool AllowConnectionsOnlyFromThisMachine
		{
			get { return false; }
		}

		public abstract void Handler(WebRequest req);

		/// <summary>
		/// Gets the service URI. This is a string of the form:
		/// /MyServiceName.whatever
		/// If a request hits the server and the request target starts with this string then 
		/// it will be routed to this service to handle.
		/// </summary>
		public abstract string ServiceURI
		{
			get;
		}
	}
}
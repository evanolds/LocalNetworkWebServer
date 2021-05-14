// Author:
// Evan Thomas Olds
//
// Creation Date:
// November 20, 2014

using System;
using System.IO;
using ETOF;

namespace LNWSConsole
{
    class Program
    {
        private const bool c_allowLocalNetworkOnly = false;

        static void Main(string[] args)
        {
            if (0 == args.Length)
            {
                Usage();
                return;
            }
            
            string sharedDirStr = null;
			string portStr = "8086";
            
			// Look at command-line arguments
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "-f" || args[i] == "-F")
                {
                    // Make sure there's another argument following
                    if (i == args.Length - 1)
                    {
                        Console.WriteLine(
                            "ERROR: -f parameter was not followed by a folder/directory name");
                        return;
                    }
					sharedDirStr = args[i + 1];
                    i++;
                }
				else if (args[i] == "-p" || args[i] == "-P")
				{
					// Make sure there's another argument following
					if (i == args.Length - 1)
					{
						Console.WriteLine(
							"ERROR: -p parameter was not followed by a port number argument");
						return;
					}
					portStr = args[i + 1];
					i++;
				}
            }

			int port = 8086;
			if (!int.TryParse(portStr, out port))
			{
				port = 8086;
			}

            // Check the shared directory
			if (null == sharedDirStr)
            {
                Console.WriteLine(
                    "ERROR: Missing required parameter for folder/directory to share (-f)");
                return;
            }
			if (!Directory.Exists(sharedDirStr))
            {
                Console.WriteLine(
					"ERROR: Directory does not exist: " + sharedDirStr);
                return;
            }

			// Create the file system object used by the web server to host files
            var fs = ETOF.IO.StandardFileSystem.Create(sharedDirStr);
			if (null == fs)
			{
				Console.WriteLine("ERROR: Could not create file system for directory: " + sharedDirStr);
				return;
			}
			var sharedDir = fs.Root;

            // Start the web server
			WebServer.Options opts = new WebServer.Options(port, ValidateIP);
			WebServer server = new WebServer(opts);
            var fws = new FilesWebService(sharedDir, null);
            fws.AddLogger(Console.Out);
			server.Services.Add(fws);

            // Was using this service to text webcam streaming
            //server.Services.Add(new MemoryImageWebService());

            // Add some services for testing purposes as well
            server.Services.Add(new SlowService());

            Console.WriteLine(
				"Web server is running\n  Server Address: http://{0}:{1}/files\n  Sharing: {2}",
				server.LocalIPAddr, port, sharedDirStr);

            // Enter the read-key loop
            while (true)
            {
                Console.WriteLine("(press q to quit)");
                var keyInfo = Console.ReadKey();
                if (keyInfo.KeyChar == 'q' || keyInfo.KeyChar == 'Q')
                {
                    break;
                }
            }

            fws.Dispose();
            fws = null;
			server.Dispose();
            Console.WriteLine();
            Console.WriteLine("Web server closed");
        }

        private static void Usage()
        {
            Console.WriteLine("Local Network Web Share usage:");
            Console.WriteLine("-f (folder_name):");
			Console.WriteLine("  (required) Specifies name of folder to share");
        }

        private static bool ValidateIP(System.Net.IPAddress addr)
        {
            if (c_allowLocalNetworkOnly)
                return WebServer.IsPrivateIP(addr);
            return true;
        }
    }
}

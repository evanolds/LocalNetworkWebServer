// Author:
// Evan Thomas Olds
//
// Creation Date:
// March 7, 2015

using System;
using System.Text;
using ETOF;

internal class DemoService : WebService
{
    private const string c_template =
        "<html>This is the response to the request:<br>" + 
        "<b>Method: {0}<br>Request-Target/URI:</b> {1}<br>" + 
        "<b>Request body size, in bytes:</b> {2}<br>" + 
        "<b>Body:</b>{3}</html>";

    public DemoService() { }

	#region implemented abstract members of SimpleWebService

	public override void Handler(WebRequest req)
	{
		long bodySizeBytes = req.GetContentLengthOrDefault(0);
        string body = string.Empty;
		if (bodySizeBytes > 0)
		{
			StringBuilder sbBody = new StringBuilder("<div style='border: 1px solid black'><br>");
			int bytesRead = 0;
			while (bytesRead < bodySizeBytes)
			{
				byte[] buf = new byte[1024];
				try
				{
					int amount = req.Body.Read(buf, 0, buf.Length);
					bytesRead += amount;
					if (0 == amount)
					{
						break;
					}
					sbBody.Append(Encoding.ASCII.GetString(buf, 0, amount).Replace("\r\n", "<font color='blue'>\\r\\n</font><br>"));
				}
				catch (Exception e)
				{
					sbBody.AppendFormat(
						"<font color='red'>Exception when reading from body stream: {0}</font><br>",
						e.Message);
					break;
				}
			}
            sbBody.Append("</div>");
            body = sbBody.ToString();
		}

        // If the content-length header was present, then the numerical value will be shown, otherwise 
        // we'll show a message indicating that it's unknown.
        string lengthStr;
        if (req.HasContentLengthHeader)
        {
            lengthStr = bodySizeBytes.ToString();
        }
        else
        {
            lengthStr = "Unknown. Content-Length header missing.";
        }

        StringBuilder response = new StringBuilder(string.Format(
            c_template, req.Method, req.URI, lengthStr, body));

		req.WriteHTMLResponse(response.ToString());
	}

	public override string ServiceURI
	{
		get
		{
			return "/";
		}
	}

	#endregion
}


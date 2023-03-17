using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Xml;

namespace DynamicDnsMonitor
{
    internal class NamecheapHelper
    {
        public static (bool success, string errorMessage) ParseNamecheapUpdateDnsResponse(IPAddress currentIPAddress, string body)
        {
            XmlDocument xmlDoc = new XmlDocument();
            using (XmlTextReader xmlTextReader = new XmlTextReader(new StringReader(body)))
            {
                xmlTextReader.Namespaces = false;
                xmlDoc.Load(xmlTextReader);
            }

            if (xmlDoc.DocumentElement == null) return new(false, $"DocumentElement is null");

            {
                var command = xmlDoc.DocumentElement.SelectSingleNode("/interface-response/Command")?.InnerText;
                if (!string.Equals("SETDNSHOST", command)) return new (false, $"Command={command}");
            }
            {
                var ip = xmlDoc.DocumentElement.SelectSingleNode("/interface-response/IP")?.InnerText;
                if (!currentIPAddress.Equals(IPAddress.Parse(ip))) return new (false, $"Invalid IP: responseIP={ip} currentIPAddress={currentIPAddress.ToString()}");
            }
            {
                var errCount = xmlDoc.DocumentElement.SelectSingleNode("/interface-response/ErrCount")?.InnerText;
                if (int.Parse(errCount) != 0) return new (false, $"ErrCount={errCount}");
            }
            {
                var done = xmlDoc.DocumentElement.SelectSingleNode("/interface-response/Done")?.InnerText;
                if (!bool.Parse(done)) return new (false, $"Done={done}");
            }
            return new (true, string.Empty);
        }
    }
}

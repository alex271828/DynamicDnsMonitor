using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace DynamicDnsMonitor
{
    internal class DnsNameConfig
    {
        public string DnsHost { get; set; }
        public string DnsFullName { get; set; }
        public string DnsUpdateUrl { get; set; }
        
        [JsonIgnore]
        public IPAddress IPAddressToCheck { get; set; }

        [JsonIgnore]
        public DateTime WhenToCheckIPAddress { get; set; }

        [JsonIgnore]
        public DateTime TimeOfLastUpdate { get; set; }
    }
}

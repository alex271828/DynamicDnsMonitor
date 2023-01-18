using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;

namespace DynamicDnsMonitor
{
    public class ServiceWorker : BackgroundService
    {
        IHttpClientFactory _httpClientFactory;
        DynamicDnsMonitor.BufferedFileLogger _logger;
        List<DnsNameConfig> _dnsNamesToMonitor;
        IPAddress _dnsServer;
        string _ipAddressProvider;
        int _ipRefreshInterval;
        int _dnsRefreshInterval;

        public ServiceWorker(IConfiguration configuration, DynamicDnsMonitor.BufferedFileLogger logger, IHttpClientFactory httpClientFactory)
        {
            _httpClientFactory = httpClientFactory;
            _logger = logger;

            var dnsServer = configuration["DnsServer"];
            if (string.IsNullOrEmpty(dnsServer) || !IPAddress.TryParse(dnsServer, out _dnsServer))
            {
                _dnsServer = IPAddress.Parse("8.8.8.8");
            }

            _ipAddressProvider = configuration["IpAddressProvider"];
            if (string.IsNullOrEmpty(_ipAddressProvider))
            {
                _ipAddressProvider = "https://api.ipify.org/";
            }

            if (!int.TryParse(configuration["IpRefreshInterval"], out _ipRefreshInterval)) _ipRefreshInterval = 60;
            if (!int.TryParse(configuration["DnsRefreshInterval"], out _dnsRefreshInterval)) _dnsRefreshInterval = 3600;

            try
            {
                var dnsUpdateUrl = configuration["DnsUpdateUrl"];
                var dnsDomain = configuration["DnsDomain"];
                var dnsPassword = configuration["DnsPassword"];

                _dnsNamesToMonitor = configuration.GetSection("DnsNames").Get<List<DnsNameConfig>>();

                foreach (var dnsNameToMonitor in _dnsNamesToMonitor)
                {
                    dnsNameToMonitor.TimeOfLastUpdate = DateTime.MinValue;

                    if (string.IsNullOrEmpty(dnsNameToMonitor.DnsHost))
                    {
                        _logger.Log($"Failed to read config values: DnsHost");
                        _dnsNamesToMonitor = null;
                        return;
                    }
                    if (string.IsNullOrEmpty(dnsNameToMonitor.DnsUpdateUrl))
                    {
                        if (dnsUpdateUrl == null || dnsDomain == null || dnsPassword == null)
                        {
                            _logger.Log($"Failed to read config values: DnsUpdateUrl or DnsDomain or DnsPassword");
                            _dnsNamesToMonitor = null;
                            return;
                        }
                        dnsNameToMonitor.DnsUpdateUrl = dnsUpdateUrl
                            .Replace("{host}", dnsNameToMonitor.DnsHost)
                            .Replace("{domain}", dnsDomain)
                            .Replace("{password}", dnsPassword);
                    }
                    if (string.IsNullOrEmpty(dnsNameToMonitor.DnsFullName))
                    {
                        if (dnsDomain == null)
                        {
                            _logger.Log($"Failed to read config values: DnsUpdateUrl or DnsDomain or DnsPassword");
                            _dnsNamesToMonitor = null;
                            return;
                        }
                        dnsNameToMonitor.DnsFullName = $"{dnsNameToMonitor.DnsHost}.{dnsDomain}";
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Log($"Failed to read config. Exception={ex.ToString()}");
                _dnsNamesToMonitor = null;
                return;
            }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (_dnsNamesToMonitor != null)
            {
                IPAddress lastIPAddress = null;
                while (!stoppingToken.IsCancellationRequested)
                {
                    await Task.Delay(_ipRefreshInterval * 1000, stoppingToken);

                    // check each host
                    foreach (var dnsNameToMonitor in _dnsNamesToMonitor)
                    {
                        var iPAddressToCheck = dnsNameToMonitor.IPAddressToCheck;
                        if (iPAddressToCheck == null)
                        {
                            if (dnsNameToMonitor.WhenToCheckIPAddress < DateTime.UtcNow) iPAddressToCheck = lastIPAddress;
                            if (iPAddressToCheck == null) continue;
                        }

                        // get dns info for the host
                        DnsClient.IDnsQueryResponse dnsResult;
                        try
                        {
                            DnsClient.LookupClientOptions options = new DnsClient.LookupClientOptions(_dnsServer);
                            options.UseCache = false;
                            var lookup = new DnsClient.LookupClient(options);
                            dnsResult = await lookup.QueryAsync(dnsNameToMonitor.DnsFullName, DnsClient.QueryType.A);
                        }
                        catch (Exception ex)
                        {
                            _logger.Log($"Failed in GetHostEntryAsync({dnsNameToMonitor.DnsFullName}). Exception={ex.ToString()}");
                            continue;
                        }

                        bool hostNeedsUpdating = true;
                        int ttl = -1;
                        foreach (var ipAddress in dnsResult.Answers.ARecords())
                        {
                            var dnsName = ipAddress.DomainName.Value.EndsWith('.') ? ipAddress.DomainName.Value.Substring(0, ipAddress.DomainName.Value.Length - 1) : ipAddress.DomainName.Value;
                            if (!dnsNameToMonitor.DnsFullName.Equals(dnsName, StringComparison.InvariantCultureIgnoreCase))
                            {
                                continue;
                            }
                            if (ipAddress.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) ttl = Math.Max(ttl, ipAddress.TimeToLive);

                            if (iPAddressToCheck.Equals(ipAddress.Address))
                            {
                                hostNeedsUpdating = false;
                                break;
                            }
                        }
                        if (ttl < 0)
                        {
                            var properties = new List<KeyValuePair<string, string>>();
                            foreach (var ipAddress in dnsResult.Answers.ARecords())
                            {
                                properties.Add(new KeyValuePair<string, string>(ipAddress.DomainName.Value, $" InitialTimeToLive={ipAddress.InitialTimeToLive} TimeToLive={ipAddress.TimeToLive} Address={ipAddress.Address.ToString()} RecordClass={ipAddress.RecordClass} RecordType={ipAddress.RecordType}"));
                            }
                            _logger.Log($"DNS query did not return entry for {dnsNameToMonitor.DnsFullName} host.", properties);
                        }
                        if (!hostNeedsUpdating)
                        {
                            dnsNameToMonitor.WhenToCheckIPAddress = DateTime.UtcNow.AddSeconds(Math.Max(_dnsRefreshInterval, ttl));
                            dnsNameToMonitor.IPAddressToCheck = null;
                            continue;
                        }

                        // perform the dns update
                        var dnsUpdateUrlResult = await PerformHttpGet(dnsNameToMonitor.DnsUpdateUrl.Replace("{ip}", iPAddressToCheck.ToString()), stoppingToken);
                        bool success = dnsUpdateUrlResult.success;
                        // OK response, still need to check response body
                        if (success)
                        {
                            success = ParseNamecheapUpdateDnsResponse(iPAddressToCheck, dnsUpdateUrlResult.body);
                        }
                        if (success)
                        {
                            _logger.Log($"Updated DNS entry for {dnsNameToMonitor.DnsFullName} host. Server status: {dnsUpdateUrlResult.statusCode} response: {dnsUpdateUrlResult.body}");
                            dnsNameToMonitor.WhenToCheckIPAddress = DateTime.UtcNow.AddSeconds(Math.Max(_dnsRefreshInterval, ttl));
                            dnsNameToMonitor.IPAddressToCheck = null;
                            dnsNameToMonitor.TimeOfLastUpdate = DateTime.UtcNow;
                        }
                        else 
                        {
                            _logger.Log($"Failed to update DNS entry for {dnsNameToMonitor.DnsFullName} host. Server status: {dnsUpdateUrlResult.statusCode} response: {dnsUpdateUrlResult.body}");
                            dnsNameToMonitor.WhenToCheckIPAddress = DateTime.UtcNow;
                        }
                    }

                    // get current IP address
                    IPAddress currentIPAddress = null;
                    var ipAddressProviderResult = await PerformHttpGet(_ipAddressProvider, stoppingToken);
                    if (ipAddressProviderResult.success)
                    {
                        if (!IPAddress.TryParse(ipAddressProviderResult.body, out currentIPAddress)) currentIPAddress = null;
                    }
                    if (currentIPAddress == null)
                    {
                        _logger.Log($"Failed to get current IP Address: {ipAddressProviderResult.body}");
                        continue;
                    }

                    // compare last and current IPAddresses
                    if (lastIPAddress != null)
                    {
                        if (lastIPAddress.Equals(currentIPAddress)) continue;
                        _logger.Log($"IPAddress changed from {lastIPAddress} to {currentIPAddress}");
                    }
                    lastIPAddress = currentIPAddress;

                    // make sure all names are compared even if there were issues
                    _dnsNamesToMonitor.ForEach(dnsNameToMonitor =>
                    {
                        dnsNameToMonitor.WhenToCheckIPAddress = DateTime.UtcNow;
                        dnsNameToMonitor.IPAddressToCheck = currentIPAddress;
                    });
                }
            }

            Program.Exit("ExecuteAsync() Exiting");
        }

        async Task<(bool success, HttpStatusCode statusCode, string body)> PerformHttpGet(string url, CancellationToken stoppingToken)
        {
            try
            {
                using (var client = _httpClientFactory.CreateClient())
                {
                    using (var requestMessage = new HttpRequestMessage(HttpMethod.Get, url))
                    {
                        using (var httpResponseMessage = await client.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead, stoppingToken))
                        {
                            var statusCode = httpResponseMessage.StatusCode;
                            var success = statusCode == HttpStatusCode.OK || statusCode == HttpStatusCode.NoContent;
                            using (var ms = new MemoryStream())
                            {
                                await httpResponseMessage.Content.CopyToAsync(ms, stoppingToken);
                                ms.Flush();
                                var serverResponse = Encoding.UTF8.GetString(ms.ToArray());
                                return new(success, statusCode, serverResponse);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                return new(false, HttpStatusCode.InternalServerError, $"Failed to get {url}. Exception={ex.ToString()}");
            }
        }

        bool ParseNamecheapUpdateDnsResponse(IPAddress currentIPAddress, string body)
        {
            try
            {
                XmlDocument xmlDoc = new XmlDocument();
                using (XmlTextReader xmlTextReader = new XmlTextReader(new StringReader(body)))
                {
                    xmlTextReader.Namespaces = false;
                    xmlDoc.Load(xmlTextReader);
                }

                if (xmlDoc.DocumentElement == null) throw new ArgumentNullException($"DocumentElement");

                {
                    var command = xmlDoc.DocumentElement.SelectSingleNode("/interface-response/Command")?.InnerText;
                    if (!string.Equals("SETDNSHOST", command)) throw new ArgumentOutOfRangeException($"Command={command}");
                }
                {
                    var ip = xmlDoc.DocumentElement.SelectSingleNode("/interface-response/IP")?.InnerText;
                    if (!currentIPAddress.Equals(IPAddress.Parse(ip))) throw new ArgumentOutOfRangeException($"Invalid IP: responseIP={ip} currentIPAddress={currentIPAddress.ToString()}");
                }
                {
                    var errCount = xmlDoc.DocumentElement.SelectSingleNode("/interface-response/ErrCount")?.InnerText;
                    if (int.Parse(errCount) != 0) throw new ArgumentOutOfRangeException($"ErrCount={errCount}");
                }
                {
                    var done = xmlDoc.DocumentElement.SelectSingleNode("/interface-response/Done")?.InnerText;
                    if (!bool.Parse(done)) throw new ArgumentOutOfRangeException($"Done={done}");
                }
                return true;
            }
            catch (Exception ex)
            {
                var properties = new List<KeyValuePair<string, string>>();
                properties.Add(new KeyValuePair<string, string>("currentIPAddress", currentIPAddress.ToString()));
                properties.Add(new KeyValuePair<string, string>("body", body));
                _logger.Log($"Failed in ParseUpdateDnsResponse()");
                return false;
            }
        }
    }
}

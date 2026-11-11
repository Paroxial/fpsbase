using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using UnityEngine;

namespace FpsBase
{
    // Asks the home router, over UPnP, to forward a UDP port to this machine so players outside the
    // LAN can join. Runs entirely off the main thread; the main thread only reads the outcome.
    public class UpnpMapping : IDisposable
    {
        private const string SearchTarget = "urn:schemas-upnp-org:device:InternetGatewayDevice:1";
        private const int DiscoveryMilliseconds = 2500;
        private const int ReleaseMilliseconds = 500;
        private const int PortAttempts = 16;

        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(3) };

        private class Gateway
        {
            public Uri ControlUrl;
            public string ServiceType;
            public IPAddress LocalAddress;
        }

        // A SOAP fault: the router understood the request and refused it.
        private class FaultException : Exception
        {
            public FaultException(string message) : base(message)
            {
            }
        }

        private readonly int port;
        private readonly Task opening;
        private Gateway gateway;
        private volatile int externalPort;
        private volatile bool disposed;
        private volatile string externalAddress;
        private volatile string failure;

        public UpnpMapping(int port)
        {
            this.port = port;
            opening = Task.Run(Open);
        }

        public bool IsPending => externalAddress == null && failure == null;

        // The router's public address once the port is open, otherwise null.
        public string ExternalAddress => externalAddress;

        // Can differ from the host's own port when the router already forwards that one elsewhere.
        public int ExternalPort => externalPort;

        public string Failure => failure;

        // Closes the port again. Blocks briefly so the request is sent before the process exits.
        public void Dispose()
        {
            disposed = true;
            opening.Wait(ReleaseMilliseconds);
            if (externalPort == 0)
            {
                return;
            }

            try
            {
                Task.Run(Release).Wait(ReleaseMilliseconds);
            }
            catch (AggregateException)
            {
                // The router drops the mapping on its own when it restarts.
            }
        }

        private Task<XDocument> Release()
        {
            return Send("DeletePortMapping", ("NewRemoteHost", ""), ("NewExternalPort", externalPort), ("NewProtocol", "UDP"));
        }

        private async Task Open()
        {
            try
            {
                gateway = await Discover();
                if (gateway == null)
                {
                    failure = "No UPnP Router Found";
                    return;
                }

                if (!await MapExternalPort())
                {
                    failure = "UPnP Rejected Every Port";
                    return;
                }
                if (disposed)
                {
                    // Closed while the router was still answering, so nothing else will release the port.
                    await Release();
                    return;
                }

                string address = DescendantValue(await Send("GetExternalIPAddress"), "NewExternalIPAddress");
                if (!IPAddress.TryParse(address, out IPAddress parsed) || IsPrivate(parsed))
                {
                    failure = "Router Has No Public Address";
                    return;
                }
                externalAddress = address;
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"UPnP port mapping failed: {exception}");
                failure = "UPnP Request Failed";
            }
        }

        // Routers refuse an external port that another machine, or a stale mapping, already holds, so
        // this walks up from the host's own port. The internal port never changes.
        private async Task<bool> MapExternalPort()
        {
            for (int candidate = port; candidate < port + PortAttempts && candidate <= IPEndPoint.MaxPort && !disposed; candidate++)
            {
                try
                {
                    await Send("AddPortMapping", ("NewRemoteHost", ""), ("NewExternalPort", candidate), ("NewProtocol", "UDP"),
                        ("NewInternalPort", port), ("NewInternalClient", gateway.LocalAddress), ("NewEnabled", 1),
                        ("NewPortMappingDescription", "FpsBase"), ("NewLeaseDuration", 0));
                    externalPort = candidate;
                    return true;
                }
                catch (FaultException fault)
                {
                    Debug.LogWarning($"UPnP rejected external port {candidate}: {fault.Message}");
                }
            }
            return false;
        }

        private static async Task<Gateway> Discover()
        {
            List<Task<Gateway>> searches = LocalAddresses().Select(DiscoverVia).ToList();
            foreach (Task<Gateway> search in searches)
            {
                Gateway found = await search;
                if (found != null)
                {
                    return found;
                }
            }
            return null;
        }

        // Searches from one network interface, so a VPN or virtual adapter can't swallow the multicast.
        private static async Task<Gateway> DiscoverVia(IPAddress local)
        {
            try
            {
                using var client = new UdpClient(new IPEndPoint(local, 0));
                byte[] request = Encoding.ASCII.GetBytes(
                    $"M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: 2\r\nST: {SearchTarget}\r\n\r\n");
                await client.SendAsync(request, request.Length, new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900));

                Task timeout = Task.Delay(DiscoveryMilliseconds);
                while (true)
                {
                    Task<UdpReceiveResult> receive = client.ReceiveAsync();
                    if (await Task.WhenAny(receive, timeout) != receive)
                    {
                        return null;
                    }

                    string location = HeaderValue(Encoding.ASCII.GetString(receive.Result.Buffer), "location");
                    Gateway found = location != null ? await Describe(location, local) : null;
                    if (found != null)
                    {
                        return found;
                    }
                }
            }
            catch (Exception exception) when (exception is SocketException or HttpRequestException or TaskCanceledException or XmlException or UriFormatException)
            {
                return null;
            }
        }

        private static async Task<Gateway> Describe(string location, IPAddress local)
        {
            using Stream content = await Http.GetStreamAsync(location);
            XDocument description = XDocument.Load(content);
            XElement service = description.Descendants().FirstOrDefault(element =>
                element.Name.LocalName == "service" &&
                (ChildValue(element, "serviceType") ?? "").Contains(":WAN", StringComparison.Ordinal) &&
                (ChildValue(element, "serviceType") ?? "").Contains("Connection:", StringComparison.Ordinal));
            string control = service != null ? ChildValue(service, "controlURL") : null;
            return control == null
                ? null
                : new Gateway { ControlUrl = new Uri(new Uri(location), control), ServiceType = ChildValue(service, "serviceType"), LocalAddress = local };
        }

        private async Task<XDocument> Send(string action, params (string Name, object Value)[] arguments)
        {
            string body = string.Concat(arguments.Select(argument => $"<{argument.Name}>{argument.Value}</{argument.Name}>"));
            string envelope = "<?xml version=\"1.0\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" " +
                              "s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\"><s:Body>" +
                              $"<u:{action} xmlns:u=\"{gateway.ServiceType}\">{body}</u:{action}></s:Body></s:Envelope>";

            using var request = new HttpRequestMessage(HttpMethod.Post, gateway.ControlUrl)
            {
                Content = new StringContent(envelope, Encoding.UTF8, "text/xml"),
            };
            request.Headers.TryAddWithoutValidation("SOAPAction", $"\"{gateway.ServiceType}#{action}\"");
            using HttpResponseMessage response = await Http.SendAsync(request);
            // Parsed from raw bytes: many routers send charset="utf-8" quoted, which Mono's string decoding rejects.
            using Stream content = await response.Content.ReadAsStreamAsync();
            XDocument reply = XDocument.Load(content);
            if (!response.IsSuccessStatusCode)
            {
                throw new FaultException($"{(int)response.StatusCode} {DescendantValue(reply, "errorCode")} {DescendantValue(reply, "errorDescription")}");
            }
            return reply;
        }

        private static IEnumerable<IPAddress> LocalAddresses()
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up && adapter.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses)
                .Select(unicast => unicast.Address)
                .Where(address => address.AddressFamily == AddressFamily.InterNetwork);
        }

        private static string ChildValue(XElement parent, string name)
        {
            return parent.Elements().FirstOrDefault(element => element.Name.LocalName == name)?.Value;
        }

        private static string DescendantValue(XDocument document, string name)
        {
            return document.Descendants().FirstOrDefault(element => element.Name.LocalName == name)?.Value;
        }

        private static string HeaderValue(string response, string name)
        {
            foreach (string line in response.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries))
            {
                int colon = line.IndexOf(':');
                if (colon > 0 && line.Substring(0, colon).Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    return line.Substring(colon + 1).Trim();
                }
            }
            return null;
        }

        // Private, link-local and carrier-grade NAT (100.64.0.0/10) ranges can't be reached from the internet.
        private static bool IsPrivate(IPAddress address)
        {
            byte[] bytes = address.GetAddressBytes();
            return bytes[0] == 10 ||
                   bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31 ||
                   bytes[0] == 192 && bytes[1] == 168 ||
                   bytes[0] == 169 && bytes[1] == 254 ||
                   bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127;
        }
    }
}

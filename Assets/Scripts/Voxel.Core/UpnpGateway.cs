using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace Voxel.Core
{
    // A home router that accepts UPnP requests to forward a port to this machine. Every call talks to
    // the router and blocks until it answers or times out: call them from a background thread.
    public sealed class UpnpGateway
    {
        private const string Protocol = "UDP";
        private const int RequestTimeout = 2000;
        private const int MaxResponseLength = 256 * 1024;

        // Routers that only keep a forwarding forever answer this error to a limited duration.
        private const int OnlyPermanentLeases = 725;
        private const int ConflictWithAnotherMachine = 718;

        // The forwarding expires by itself if the server dies without removing it.
        private const int LeaseSeconds = 7200;

        private readonly Uri controlUrl;
        private readonly string serviceType;

        private UpnpGateway(Uri controlUrl, string serviceType, IPAddress localAddress)
        {
            this.controlUrl = controlUrl;
            this.serviceType = serviceType;
            LocalAddress = localAddress;
        }

        // Address of this machine on the network it shares with the router.
        public IPAddress LocalAddress { get; }

        // Looks for a router on the local network. Returns null if none answers in time.
        public static UpnpGateway Discover(int timeoutMilliseconds = 3000)
        {
            var sockets = new List<Socket>();
            try
            {
                foreach (IPAddress local in LocalAddresses())
                {
                    Socket socket = OpenSearchSocket(local);
                    if (socket != null) sockets.Add(socket);
                }

                return sockets.Count > 0 ? ListenForGateway(sockets, timeoutMilliseconds) : null;
            }
            finally
            {
                foreach (Socket socket in sockets)
                {
                    socket.Dispose();
                }
            }
        }

        // Reads the description a device publishes at the given address. Returns null if the device
        // does not answer or does not forward ports.
        public static UpnpGateway FromDescription(Uri location, IPAddress localAddress)
        {
            if (!TrySend(location, null, null, out string description)) return null;
            if (!UpnpProtocol.TryFindService(description, location, out Uri controlUrl, out string serviceType)) return null;

            return new UpnpGateway(controlUrl, serviceType, localAddress);
        }

        // The address the router has on the internet, as it reports it.
        public bool TryGetExternalAddress(out IPAddress address)
        {
            address = null;
            return TryInvoke("GetExternalIPAddress", out string response)
                && UpnpProtocol.TryReadValue(response, "NewExternalIPAddress", out string text)
                && IPAddress.TryParse(text, out address);
        }

        // Forwards the UDP port from the router to the same port of this machine. Calling it again
        // for a port already forwarded here renews it. Returns false, with the reason, if the router refuses.
        public bool TryAddPortMapping(int port, string description, out string error)
        {
            if (TryAddPortMapping(port, description, LeaseSeconds, out int code, out error)) return true;

            return code == OnlyPermanentLeases && TryAddPortMapping(port, description, 0, out _, out error);
        }

        public bool TryDeletePortMapping(int port)
        {
            return TryInvoke(
                "DeletePortMapping",
                out _,
                ("NewRemoteHost", ""),
                ("NewExternalPort", port.ToString()),
                ("NewProtocol", Protocol));
        }

        private bool TryAddPortMapping(int port, string description, int leaseSeconds, out int code, out string error)
        {
            code = 0;
            error = null;
            bool added = TryInvoke(
                "AddPortMapping",
                out string response,
                ("NewRemoteHost", ""),
                ("NewExternalPort", port.ToString()),
                ("NewProtocol", Protocol),
                ("NewInternalPort", port.ToString()),
                ("NewInternalClient", LocalAddress.ToString()),
                ("NewEnabled", "1"),
                ("NewPortMappingDescription", description),
                ("NewLeaseDuration", leaseSeconds.ToString()));
            if (added) return true;

            if (response == null || !UpnpProtocol.TryReadError(response, out code, out string text))
            {
                error = "the router did not answer the request";
            }
            else if (code == ConflictWithAnotherMachine)
            {
                error = "the port is already forwarded to another machine";
            }
            else
            {
                error = text.Length > 0 ? $"the router refused, error {code}: {text}" : $"the router refused, error {code}";
            }

            return false;
        }

        private bool TryInvoke(string action, out string response, params (string Name, string Value)[] arguments)
        {
            return TrySend(
                controlUrl,
                UpnpProtocol.SoapAction(serviceType, action),
                UpnpProtocol.ActionRequest(serviceType, action, arguments),
                out response);
        }

        // Addresses of this machine on the networks that lead somewhere: those with a gateway, or all
        // of them when the system does not tell.
        private static List<IPAddress> LocalAddresses()
        {
            var routed = new List<IPAddress>();
            var others = new List<IPAddress>();
            foreach (NetworkInterface network in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (network.OperationalStatus != OperationalStatus.Up) continue;
                if (network.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                IPInterfaceProperties properties = network.GetIPProperties();
                bool hasGateway = false;
                foreach (GatewayIPAddressInformation gateway in properties.GatewayAddresses)
                {
                    hasGateway |= gateway.Address.AddressFamily == AddressFamily.InterNetwork && !gateway.Address.Equals(IPAddress.Any);
                }

                foreach (UnicastIPAddressInformation unicast in properties.UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork) continue;

                    (hasGateway ? routed : others).Add(unicast.Address);
                }
            }

            return routed.Count > 0 ? routed : others;
        }

        // Sends the discovery messages from the given address. Returns null if that network is unusable.
        private static Socket OpenSearchSocket(IPAddress local)
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            try
            {
                socket.Bind(new IPEndPoint(local, 0));
                var target = new IPEndPoint(IPAddress.Parse(UpnpProtocol.MulticastAddress), UpnpProtocol.MulticastPort);

                // UDP may lose a message: each is sent twice.
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    foreach (string searchTarget in UpnpProtocol.SearchTargets)
                    {
                        socket.SendTo(Encoding.ASCII.GetBytes(UpnpProtocol.SearchRequest(searchTarget)), target);
                    }
                }

                return socket;
            }
            catch (SocketException)
            {
                socket.Dispose();
                return null;
            }
        }

        private static UpnpGateway ListenForGateway(List<Socket> sockets, int timeoutMilliseconds)
        {
            var buffer = new byte[4096];
            var tried = new HashSet<string>();
            var timer = Stopwatch.StartNew();

            while (timer.ElapsedMilliseconds < timeoutMilliseconds)
            {
                var readable = new List<Socket>(sockets);
                int wait = (int)Math.Min(timeoutMilliseconds - timer.ElapsedMilliseconds, 250);
                Socket.Select(readable, null, null, wait * 1000);

                foreach (Socket socket in readable)
                {
                    EndPoint sender = new IPEndPoint(IPAddress.Any, 0);
                    int length;
                    try
                    {
                        length = socket.ReceiveFrom(buffer, ref sender);
                    }
                    catch (SocketException)
                    {
                        continue;
                    }

                    string response = Encoding.ASCII.GetString(buffer, 0, length);
                    if (!UpnpProtocol.TryReadLocation(response, out Uri location)) continue;

                    // Only the device that answered is asked for its description: an answer cannot
                    // make us contact another machine.
                    bool fromSender = IPAddress.TryParse(location.Host, out IPAddress host) && host.Equals(((IPEndPoint)sender).Address);
                    if (!fromSender || !tried.Add(location.AbsoluteUri)) continue;

                    UpnpGateway gateway = FromDescription(location, ((IPEndPoint)socket.LocalEndPoint).Address);
                    if (gateway != null) return gateway;
                }
            }

            return null;
        }

        // Sends an HTTP request: a GET without body, otherwise a POST of the action. Returns true with
        // the answer when the device accepts; false with its answer, or with null if there is none,
        // when it refuses or does not respond.
        private static bool TrySend(Uri url, string soapAction, string body, out string response)
        {
            response = null;
            try
            {
                var request = (HttpWebRequest)WebRequest.Create(url);
                request.Timeout = RequestTimeout;
                request.ReadWriteTimeout = RequestTimeout;
                request.AllowAutoRedirect = false;
                request.KeepAlive = false;

                // The router is on the local network: never through the system's proxy.
                request.Proxy = null;

                // Some routers do not understand being asked for permission before the body is sent.
                request.ServicePoint.Expect100Continue = false;

                if (body != null)
                {
                    byte[] data = Encoding.UTF8.GetBytes(body);
                    request.Method = "POST";
                    request.ContentType = "text/xml; charset=\"utf-8\"";
                    request.Headers["SOAPAction"] = soapAction;
                    request.ContentLength = data.Length;
                    using (Stream stream = request.GetRequestStream())
                    {
                        stream.Write(data, 0, data.Length);
                    }
                }

                using (WebResponse answer = request.GetResponse())
                {
                    response = ReadText(answer);
                    return true;
                }
            }
            catch (WebException e)
            {
                // A refusal comes as an HTTP error whose body says why.
                if (e.Response != null)
                {
                    using (e.Response)
                    {
                        response = ReadTextOrNull(e.Response);
                    }
                }

                return false;
            }
            catch (IOException)
            {
                return false;
            }
            catch (SocketException)
            {
                return false;
            }
        }

        private static string ReadTextOrNull(WebResponse answer)
        {
            try
            {
                return ReadText(answer);
            }
            catch (IOException)
            {
                return null;
            }
            catch (WebException)
            {
                return null;
            }
        }

        // Reads at most MaxResponseLength characters: a device cannot make us fill the memory.
        private static string ReadText(WebResponse answer)
        {
            using (Stream stream = answer.GetResponseStream())
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                var text = new StringBuilder();
                var buffer = new char[4096];
                int read;
                while (text.Length < MaxResponseLength && (read = reader.Read(buffer, 0, buffer.Length)) > 0)
                {
                    text.Append(buffer, 0, read);
                }

                return text.ToString();
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using NUnit.Framework;

namespace Voxel.Core.Tests
{
    // These tests talk to a fake router served on this machine: they never touch the real network.
    public class UpnpGatewayTests
    {
        private static readonly IPAddress LocalAddress = IPAddress.Parse("192.168.1.16");

        private FakeRouter router;

        [SetUp]
        public void StartRouter()
        {
            router = new FakeRouter();
        }

        [TearDown]
        public void StopRouter()
        {
            router.Dispose();
        }

        [Test]
        public void FromDescription_DeviceThatForwardsPorts_ReturnsAGateway()
        {
            UpnpGateway gateway = UpnpGateway.FromDescription(router.Location, LocalAddress);

            Assert.IsNotNull(gateway);
            Assert.AreEqual(LocalAddress, gateway.LocalAddress);
        }

        [Test]
        public void FromDescription_DeviceThatDoesNotAnswer_ReturnsNull()
        {
            Uri location = router.Location;
            router.Dispose();

            Assert.IsNull(UpnpGateway.FromDescription(location, LocalAddress));
        }

        [Test]
        public void FromDescription_DeviceThatDoesNotForwardPorts_ReturnsNull()
        {
            router.ServiceType = "urn:schemas-upnp-org:service:RenderingControl:1";

            Assert.IsNull(UpnpGateway.FromDescription(router.Location, LocalAddress));
        }

        [Test]
        public void TryAddPortMapping_ForwardsTheUdpPortToThisMachine()
        {
            UpnpGateway gateway = UpnpGateway.FromDescription(router.Location, LocalAddress);

            Assert.IsTrue(gateway.TryAddPortMapping(50000, "Test game", out string error));

            Assert.IsNull(error);
            Assert.AreEqual(1, router.Requests.Count);
            Assert.AreEqual("AddPortMapping", router.Requests[0].Action);
            Assert.AreEqual("50000", router.Requests[0].Value("NewExternalPort"));
            Assert.AreEqual("50000", router.Requests[0].Value("NewInternalPort"));
            Assert.AreEqual("UDP", router.Requests[0].Value("NewProtocol"));
            Assert.AreEqual("192.168.1.16", router.Requests[0].Value("NewInternalClient"));
            Assert.AreEqual("1", router.Requests[0].Value("NewEnabled"));
            Assert.AreEqual("Test game", router.Requests[0].Value("NewPortMappingDescription"));
            Assert.AreNotEqual("0", router.Requests[0].Value("NewLeaseDuration"), "the forwarding expires if the server dies");
        }

        [Test]
        public void TryAddPortMapping_RouterThatOnlyKeepsForwardingsForever_AsksAgainWithoutDuration()
        {
            router.OnlyPermanentLeases = true;
            UpnpGateway gateway = UpnpGateway.FromDescription(router.Location, LocalAddress);

            Assert.IsTrue(gateway.TryAddPortMapping(50000, "Test game", out _));

            Assert.AreEqual(2, router.Requests.Count);
            Assert.AreEqual("0", router.Requests[1].Value("NewLeaseDuration"));
        }

        [Test]
        public void TryAddPortMapping_PortForwardedToAnotherMachine_FailsWithTheReason()
        {
            router.ErrorCode = 718;
            UpnpGateway gateway = UpnpGateway.FromDescription(router.Location, LocalAddress);

            Assert.IsFalse(gateway.TryAddPortMapping(50000, "Test game", out string error));

            StringAssert.Contains("another machine", error);
            Assert.AreEqual(1, router.Requests.Count, "only the duration error is worth a second try");
        }

        [Test]
        public void TryAddPortMapping_OtherRefusal_FailsWithTheRoutersError()
        {
            router.ErrorCode = 606;
            UpnpGateway gateway = UpnpGateway.FromDescription(router.Location, LocalAddress);

            Assert.IsFalse(gateway.TryAddPortMapping(50000, "Test game", out string error));

            StringAssert.Contains("606", error);
        }

        [Test]
        public void TryGetExternalAddress_ReadsTheRoutersInternetAddress()
        {
            UpnpGateway gateway = UpnpGateway.FromDescription(router.Location, LocalAddress);

            Assert.IsTrue(gateway.TryGetExternalAddress(out IPAddress address));

            Assert.AreEqual(IPAddress.Parse("203.0.113.7"), address);
        }

        [Test]
        public void TryDeletePortMapping_RemovesTheUdpPort()
        {
            UpnpGateway gateway = UpnpGateway.FromDescription(router.Location, LocalAddress);

            Assert.IsTrue(gateway.TryDeletePortMapping(50000));

            Assert.AreEqual("DeletePortMapping", router.Requests[0].Action);
            Assert.AreEqual("50000", router.Requests[0].Value("NewExternalPort"));
            Assert.AreEqual("UDP", router.Requests[0].Value("NewProtocol"));
        }

        [Test]
        public void PortForwarding_Open_ThenClose_AddsThenRemovesThePort()
        {
            UpnpGateway gateway = UpnpGateway.FromDescription(router.Location, LocalAddress);

            PortForwarding forwarding = PortForwarding.Open(gateway, 50000, "Test game");

            Assert.IsTrue(forwarding.IsOpen);
            Assert.IsTrue(forwarding.IsReachable);
            Assert.AreEqual(IPAddress.Parse("203.0.113.7"), forwarding.PublicAddress);
            StringAssert.Contains("203.0.113.7", forwarding.Summary);

            Assert.IsTrue(forwarding.Renew());
            forwarding.Close();

            Assert.AreEqual(
                new[] { "AddPortMapping", "GetExternalIPAddress", "AddPortMapping", "DeletePortMapping" },
                router.Requests.ConvertAll(request => request.Action).ToArray());
        }

        [Test]
        public void PortForwarding_RouterBehindASharedAddress_IsOpenButNotReachable()
        {
            router.ExternalAddress = "100.64.12.34";
            UpnpGateway gateway = UpnpGateway.FromDescription(router.Location, LocalAddress);

            PortForwarding forwarding = PortForwarding.Open(gateway, 50000, "Test game");

            Assert.IsTrue(forwarding.IsOpen);
            Assert.IsFalse(forwarding.IsReachable);
            StringAssert.Contains("no public address", forwarding.Summary);
        }

        [Test]
        public void PortForwarding_RouterThatRefuses_IsNotOpenAndClosingDoesNothing()
        {
            router.ErrorCode = 718;
            UpnpGateway gateway = UpnpGateway.FromDescription(router.Location, LocalAddress);

            PortForwarding forwarding = PortForwarding.Open(gateway, 50000, "Test game");
            forwarding.Close();

            Assert.IsFalse(forwarding.IsOpen);
            Assert.IsFalse(forwarding.IsReachable);
            Assert.IsFalse(forwarding.Renew());
            StringAssert.Contains("another machine", forwarding.Summary);
            Assert.AreEqual(1, router.Requests.Count, "nothing to remove, nothing to renew");
        }

        [Test]
        public void PortForwarding_WithoutARouter_IsNotOpen()
        {
            PortForwarding forwarding = PortForwarding.Open(null, 50000, "Test game");

            Assert.IsFalse(forwarding.IsOpen);
            Assert.AreEqual(50000, forwarding.Port);
            StringAssert.Contains("UPnP", forwarding.Summary);
            Assert.DoesNotThrow(forwarding.Close);
        }

        private sealed class Request
        {
            public string Action;
            public string Body;

            public string Value(string name) => UpnpProtocol.TryReadValue(Body, name, out string value) ? value : null;
        }

        // A router reduced to what the gateway uses: a description and a service answering three actions.
        private sealed class FakeRouter : IDisposable
        {
            private const int OnlyPermanentLeasesError = 725;

            private readonly HttpListener listener = new HttpListener();
            private readonly List<Request> requests = new List<Request>();
            private readonly Thread thread;

            public FakeRouter()
            {
                int port = FreePort();
                Location = new Uri($"http://127.0.0.1:{port}/desc.xml");
                listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                listener.Start();
                thread = new Thread(Serve) { IsBackground = true };
                thread.Start();
            }

            public Uri Location { get; }

            public string ServiceType { get; set; } = "urn:schemas-upnp-org:service:WANIPConnection:1";

            public string ExternalAddress { get; set; } = "203.0.113.7";

            public bool OnlyPermanentLeases { get; set; }

            // When not zero, every AddPortMapping is refused with this error.
            public int ErrorCode { get; set; }

            public List<Request> Requests
            {
                get
                {
                    lock (requests) return new List<Request>(requests);
                }
            }

            public void Dispose()
            {
                if (!listener.IsListening) return;

                listener.Stop();
                listener.Close();
                thread.Join(2000);
            }

            private static int FreePort()
            {
                var probe = new TcpListener(IPAddress.Loopback, 0);
                probe.Start();
                int port = ((IPEndPoint)probe.LocalEndpoint).Port;
                probe.Stop();
                return port;
            }

            private void Serve()
            {
                while (true)
                {
                    HttpListenerContext context;
                    try
                    {
                        context = listener.GetContext();
                    }
                    catch (Exception e) when (e is HttpListenerException || e is ObjectDisposedException || e is InvalidOperationException)
                    {
                        return;
                    }

                    Answer(context);
                }
            }

            private void Answer(HttpListenerContext context)
            {
                if (context.Request.HttpMethod == "GET")
                {
                    Write(context, 200, "<root><device><serviceList><service>"
                        + $"<serviceType>{ServiceType}</serviceType><controlURL>/control</controlURL>"
                        + "</service></serviceList></device></root>");
                    return;
                }

                string body;
                using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8))
                {
                    body = reader.ReadToEnd();
                }

                string soapAction = context.Request.Headers["SOAPAction"] ?? "";
                var request = new Request { Action = soapAction.Trim('"').Substring(soapAction.Trim('"').IndexOf('#') + 1), Body = body };
                lock (requests) requests.Add(request);

                if (request.Action == "AddPortMapping" && ErrorCode != 0)
                {
                    Write(context, 500, Error(ErrorCode, "Refused"));
                }
                else if (request.Action == "AddPortMapping" && OnlyPermanentLeases && request.Value("NewLeaseDuration") != "0")
                {
                    Write(context, 500, Error(OnlyPermanentLeasesError, "OnlyPermanentLeasesSupported"));
                }
                else if (request.Action == "GetExternalIPAddress")
                {
                    Write(context, 200, $"<answer><NewExternalIPAddress>{ExternalAddress}</NewExternalIPAddress></answer>");
                }
                else
                {
                    Write(context, 200, "<answer />");
                }
            }

            private static string Error(int code, string description)
            {
                return $"<fault><UPnPError><errorCode>{code}</errorCode><errorDescription>{description}</errorDescription></UPnPError></fault>";
            }

            private static void Write(HttpListenerContext context, int status, string text)
            {
                byte[] data = Encoding.UTF8.GetBytes(text);
                context.Response.StatusCode = status;
                context.Response.ContentType = "text/xml";
                context.Response.ContentLength64 = data.Length;
                context.Response.OutputStream.Write(data, 0, data.Length);
                context.Response.Close();
            }
        }
    }
}

using System;
using System.Net;
using NUnit.Framework;

namespace Voxel.Core.Tests
{
    public class UpnpProtocolTests
    {
        private const string WanIpService = "urn:schemas-upnp-org:service:WANIPConnection:1";

        private static readonly Uri Location = new Uri("http://192.168.1.254:5678/desc/root");

        // Shaped like what home routers publish: the service sits two devices deep, among others.
        private const string Description = @"<?xml version=""1.0""?>
<root xmlns=""urn:schemas-upnp-org:device-1-0"">
  <device>
    <deviceType>urn:schemas-upnp-org:device:InternetGatewayDevice:1</deviceType>
    <serviceList>
      <service>
        <serviceType>urn:schemas-upnp-org:service:Layer3Forwarding:1</serviceType>
        <controlURL>/control/l3f</controlURL>
      </service>
    </serviceList>
    <deviceList>
      <device>
        <deviceType>urn:schemas-upnp-org:device:WANDevice:1</deviceType>
        <deviceList>
          <device>
            <deviceType>urn:schemas-upnp-org:device:WANConnectionDevice:1</deviceType>
            <serviceList>
              <service>
                <serviceType>urn:schemas-upnp-org:service:WANIPConnection:1</serviceType>
                <controlURL>/control/wan_ip_connection</controlURL>
              </service>
            </serviceList>
          </device>
        </deviceList>
      </device>
    </deviceList>
  </device>
</root>";

        [Test]
        public void SearchRequest_AsksForTheGivenTarget()
        {
            string request = UpnpProtocol.SearchRequest(UpnpProtocol.SearchTargets[0]);

            StringAssert.StartsWith("M-SEARCH * HTTP/1.1\r\n", request);
            StringAssert.Contains("HOST: 239.255.255.250:1900\r\n", request);
            StringAssert.Contains("MAN: \"ssdp:discover\"\r\n", request);
            StringAssert.Contains("ST: urn:schemas-upnp-org:device:InternetGatewayDevice:1\r\n", request);
            StringAssert.EndsWith("\r\n\r\n", request);
        }

        [TestCase("LOCATION")]
        [TestCase("Location")]
        [TestCase("location")]
        public void TryReadLocation_FindsTheHeaderWhateverItsCase(string header)
        {
            string response = "HTTP/1.1 200 OK\r\nCACHE-CONTROL: max-age=1800\r\n" + header + ": http://192.168.1.254:5678/desc/root\r\nST: upnp:rootdevice\r\n\r\n";

            Assert.IsTrue(UpnpProtocol.TryReadLocation(response, out Uri location));

            Assert.AreEqual(Location, location);
        }

        [TestCase("HTTP/1.1 200 OK\r\nST: upnp:rootdevice\r\n\r\n")]
        [TestCase("HTTP/1.1 200 OK\r\nLOCATION: not an address\r\n\r\n")]
        [TestCase("HTTP/1.1 200 OK\r\nLOCATION: file:///c:/windows/win.ini\r\n\r\n")]
        [TestCase("HTTP/1.1 200 OK\r\nLOCATION: https://192.168.1.254/desc\r\n\r\n")]
        [TestCase("")]
        public void TryReadLocation_WithoutAnHttpAddress_Fails(string response)
        {
            Assert.IsFalse(UpnpProtocol.TryReadLocation(response, out _));
        }

        [Test]
        public void TryFindService_FindsThePortForwardingServiceAndItsAddress()
        {
            Assert.IsTrue(UpnpProtocol.TryFindService(Description, Location, out Uri controlUrl, out string serviceType));

            Assert.AreEqual(new Uri("http://192.168.1.254:5678/control/wan_ip_connection"), controlUrl);
            Assert.AreEqual(WanIpService, serviceType);
        }

        [Test]
        public void TryFindService_AcceptsAPppConnection()
        {
            string description = Description.Replace("WANIPConnection:1", "WANPPPConnection:1");

            Assert.IsTrue(UpnpProtocol.TryFindService(description, Location, out _, out string serviceType));

            Assert.AreEqual("urn:schemas-upnp-org:service:WANPPPConnection:1", serviceType);
        }

        [Test]
        public void TryFindService_DeviceThatDoesNotForwardPorts_Fails()
        {
            string description = Description.Replace("WANIPConnection", "RenderingControl");

            Assert.IsFalse(UpnpProtocol.TryFindService(description, Location, out _, out _));
        }

        [TestCase("http://203.0.113.9/control")]
        [TestCase("https://192.168.1.254/control")]
        [TestCase("file:///c:/control")]
        public void TryFindService_ControlAddressOutsideTheDevice_Fails(string controlUrl)
        {
            string description = Description.Replace("/control/wan_ip_connection", controlUrl);

            Assert.IsFalse(UpnpProtocol.TryFindService(description, Location, out _, out _));
        }

        [TestCase("")]
        [TestCase("not xml at all")]
        [TestCase("<root><device>")]
        [TestCase("<!DOCTYPE root [<!ENTITY a \"b\">]><root>&a;</root>")]
        public void TryFindService_MalformedDescription_Fails(string description)
        {
            Assert.IsFalse(UpnpProtocol.TryFindService(description, Location, out _, out _));
        }

        [Test]
        public void ActionRequest_CarriesTheActionAndItsArguments()
        {
            string request = UpnpProtocol.ActionRequest(
                WanIpService, "AddPortMapping", ("NewExternalPort", "50000"), ("NewRemoteHost", ""), ("NewPortMappingDescription", "Tom & <Jerry>"));

            StringAssert.Contains("<u:AddPortMapping xmlns:u=\"" + WanIpService + "\">", request);
            StringAssert.Contains("<NewExternalPort>50000</NewExternalPort>", request);
            StringAssert.Contains("<NewRemoteHost></NewRemoteHost>", request);
            Assert.IsTrue(UpnpProtocol.TryReadValue(request, "NewPortMappingDescription", out string description));
            Assert.AreEqual("Tom & <Jerry>", description);
        }

        [Test]
        public void SoapAction_IsTheQuotedServiceAndAction()
        {
            Assert.AreEqual("\"" + WanIpService + "#AddPortMapping\"", UpnpProtocol.SoapAction(WanIpService, "AddPortMapping"));
        }

        [Test]
        public void TryReadValue_ReadsAnAnswer()
        {
            string response = "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body>"
                + "<u:GetExternalIPAddressResponse xmlns:u=\"" + WanIpService + "\">"
                + "<NewExternalIPAddress>203.0.113.7</NewExternalIPAddress>"
                + "</u:GetExternalIPAddressResponse></s:Body></s:Envelope>";

            Assert.IsTrue(UpnpProtocol.TryReadValue(response, "NewExternalIPAddress", out string value));
            Assert.AreEqual("203.0.113.7", value);
            Assert.IsFalse(UpnpProtocol.TryReadValue(response, "NewInternalClient", out _));
        }

        [Test]
        public void TryReadError_ReadsTheCodeAndTheDescription()
        {
            string response = "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body><s:Fault>"
                + "<faultcode>s:Client</faultcode><faultstring>UPnPError</faultstring><detail>"
                + "<UPnPError xmlns=\"urn:schemas-upnp-org:control-1-0\"><errorCode>725</errorCode>"
                + "<errorDescription>OnlyPermanentLeasesSupported</errorDescription></UPnPError>"
                + "</detail></s:Fault></s:Body></s:Envelope>";

            Assert.IsTrue(UpnpProtocol.TryReadError(response, out int code, out string description));

            Assert.AreEqual(725, code);
            Assert.AreEqual("OnlyPermanentLeasesSupported", description);
        }

        [TestCase("")]
        [TestCase("<html><body>Internal Server Error</body></html>")]
        [TestCase("<UPnPError><errorCode>many</errorCode></UPnPError>")]
        public void TryReadError_WithoutAnErrorCode_Fails(string response)
        {
            Assert.IsFalse(UpnpProtocol.TryReadError(response, out _, out _));
        }

        [TestCase("203.0.113.7", true)]
        [TestCase("8.8.8.8", true)]
        [TestCase("172.32.0.1", true)]
        [TestCase("100.128.0.1", true)]
        [TestCase("10.0.0.1", false)]
        [TestCase("172.16.0.1", false)]
        [TestCase("172.31.255.254", false)]
        [TestCase("192.168.1.254", false)]
        [TestCase("100.64.0.1", false)]
        [TestCase("100.127.255.254", false)]
        [TestCase("169.254.10.10", false)]
        [TestCase("127.0.0.1", false)]
        [TestCase("0.0.0.0", false)]
        [TestCase("224.0.0.1", false)]
        [TestCase("::1", false)]
        public void IsPublic_TellsInternetAddressesFromPrivateAndSharedOnes(string address, bool expected)
        {
            Assert.AreEqual(expected, UpnpProtocol.IsPublic(IPAddress.Parse(address)));
        }

        [Test]
        public void IsPublic_NoAddress_IsFalse()
        {
            Assert.IsFalse(UpnpProtocol.IsPublic(null));
        }
    }
}

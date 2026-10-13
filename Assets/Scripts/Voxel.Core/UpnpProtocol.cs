using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Xml;
using System.Xml.Linq;

namespace Voxel.Core
{
    // The text side of UPnP port forwarding: the messages sent to a home router and the reading of its
    // answers. Everything a router sends is untrusted: anything malformed is refused, without an exception.
    public static class UpnpProtocol
    {
        public const string MulticastAddress = "239.255.255.250";
        public const int MulticastPort = 1900;

        private const string WanIpService = "urn:schemas-upnp-org:service:WANIPConnection:";
        private const string WanPppService = "urn:schemas-upnp-org:service:WANPPPConnection:";
        private const string SoapEnvelope = "http://schemas.xmlsoap.org/soap/envelope/";
        private const string SoapEncoding = "http://schemas.xmlsoap.org/soap/encoding/";

        // What a router that forwards ports may call itself, depending on its age.
        public static readonly IReadOnlyList<string> SearchTargets = new[]
        {
            "urn:schemas-upnp-org:device:InternetGatewayDevice:1",
            "urn:schemas-upnp-org:device:InternetGatewayDevice:2",
            WanIpService + "1",
            WanPppService + "1"
        };

        // Discovery message, to send by UDP to the multicast address: routers answer with their description's address.
        public static string SearchRequest(string target)
        {
            return "M-SEARCH * HTTP/1.1\r\n"
                + $"HOST: {MulticastAddress}:{MulticastPort}\r\n"
                + "MAN: \"ssdp:discover\"\r\n"
                + "MX: 2\r\n"
                + $"ST: {target}\r\n\r\n";
        }

        // Reads the address of the device description out of an answer to the discovery message.
        public static bool TryReadLocation(string response, out Uri location)
        {
            location = null;
            foreach (string line in response.Split('\n'))
            {
                int colon = line.IndexOf(':');
                if (colon < 0 || !line.Substring(0, colon).Trim().Equals("LOCATION", StringComparison.OrdinalIgnoreCase)) continue;

                return Uri.TryCreate(line.Substring(colon + 1).Trim(), UriKind.Absolute, out location)
                    && location.Scheme == Uri.UriSchemeHttp;
            }

            return false;
        }

        // Finds, in a device description, the service that forwards ports and the address to send it
        // actions to. That address must be on the device itself: a description cannot send us elsewhere.
        public static bool TryFindService(string description, Uri location, out Uri controlUrl, out string serviceType)
        {
            controlUrl = null;
            serviceType = null;
            if (!TryParse(description, out XDocument document)) return false;

            foreach (XElement service in document.Descendants().Where(element => element.Name.LocalName == "service"))
            {
                string type = ChildValue(service, "serviceType");
                string control = ChildValue(service, "controlURL");
                if (type == null || control == null) continue;
                if (!type.StartsWith(WanIpService, StringComparison.Ordinal) && !type.StartsWith(WanPppService, StringComparison.Ordinal)) continue;

                if (!Uri.TryCreate(location, control, out Uri url)) continue;
                if (url.Scheme != Uri.UriSchemeHttp || url.Host != location.Host) continue;

                controlUrl = url;
                serviceType = type;
                return true;
            }

            return false;
        }

        // Body of an action sent to the service, by HTTP POST with the header given by SoapAction.
        public static string ActionRequest(string serviceType, string action, params (string Name, string Value)[] arguments)
        {
            XNamespace soap = SoapEnvelope;
            XNamespace service = serviceType;
            var call = new XElement(service + action, new XAttribute(XNamespace.Xmlns + "u", serviceType));
            foreach ((string name, string value) in arguments)
            {
                call.Add(new XElement(name, value));
            }

            var envelope = new XElement(
                soap + "Envelope",
                new XAttribute(XNamespace.Xmlns + "s", SoapEnvelope),
                new XAttribute(soap + "encodingStyle", SoapEncoding),
                new XElement(soap + "Body", call));

            return "<?xml version=\"1.0\"?>\n" + envelope.ToString(SaveOptions.DisableFormatting);
        }

        public static string SoapAction(string serviceType, string action) => $"\"{serviceType}#{action}\"";

        // Reads one value out of the answer to an action.
        public static bool TryReadValue(string response, string name, out string value)
        {
            value = null;
            if (!TryParse(response, out XDocument document)) return false;

            value = document.Descendants().FirstOrDefault(element => element.Name.LocalName == name)?.Value.Trim();
            return value != null;
        }

        // Reads the error a router answers with when it refuses an action.
        public static bool TryReadError(string response, out int code, out string description)
        {
            code = 0;
            description = "";
            if (!TryReadValue(response, "errorCode", out string codeText) || !int.TryParse(codeText, out code)) return false;

            if (TryReadValue(response, "errorDescription", out string text)) description = text;
            return true;
        }

        // True for an address reachable from the internet. Private ranges, and the range internet
        // providers use when several subscribers share one address, are not.
        public static bool IsPublic(IPAddress address)
        {
            if (address == null || address.AddressFamily != AddressFamily.InterNetwork) return false;

            byte[] bytes = address.GetAddressBytes();
            int first = bytes[0];
            int second = bytes[1];
            if (first == 0 || first == 10 || first == 127 || first >= 224) return false;
            if (first == 100 && second >= 64 && second <= 127) return false;
            if (first == 169 && second == 254) return false;
            if (first == 172 && second >= 16 && second <= 31) return false;
            if (first == 192 && second == 168) return false;
            return true;
        }

        private static string ChildValue(XElement parent, string name)
        {
            return parent.Elements().FirstOrDefault(element => element.Name.LocalName == name)?.Value.Trim();
        }

        private static bool TryParse(string xml, out XDocument document)
        {
            document = null;
            if (string.IsNullOrEmpty(xml)) return false;

            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            try
            {
                using (var reader = XmlReader.Create(new StringReader(xml), settings))
                {
                    document = XDocument.Load(reader);
                    return true;
                }
            }
            catch (XmlException)
            {
                return false;
            }
        }
    }
}

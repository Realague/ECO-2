using System;
using System.Net;

namespace Voxel.Core
{
    // The outcome of asking the home router to forward the server's port, so that players outside the
    // local network can join without configuring the router by hand. Open, Renew and Close talk to
    // the router and block: call them from a background thread.
    public sealed class PortForwarding
    {
        private readonly UpnpGateway gateway;
        private readonly string description;

        private PortForwarding(int port, string description, UpnpGateway gateway, IPAddress publicAddress, string problem)
        {
            Port = port;
            this.description = description;
            this.gateway = gateway;
            PublicAddress = publicAddress;
            Problem = problem;
        }

        public int Port { get; }

        public bool IsOpen => Problem == null;

        // Why the port could not be opened, or null if it was.
        public string Problem { get; }

        // The router's address on the internet, or null if it did not tell.
        public IPAddress PublicAddress { get; }

        // False when the router itself sits behind an address shared with other subscribers: the port
        // is then open on the router, but nobody can reach the router from the internet.
        public bool IsReachable => IsOpen && (PublicAddress == null || UpnpProtocol.IsPublic(PublicAddress));

        // One line for the log and the screen.
        public string Summary
        {
            get
            {
                if (!IsOpen)
                {
                    return $"Internet: UDP port {Port} not opened on the router ({Problem}); forward it by hand to play outside your network";
                }

                if (PublicAddress == null) return $"Internet: UDP port {Port} opened on the router";

                return IsReachable
                    ? $"Internet: players can join at {PublicAddress} (UDP port {Port} opened on the router)"
                    : $"Internet: UDP port {Port} opened on the router, but the router has no public address ({PublicAddress}): players outside your network cannot join";
            }
        }

        // Looks for the router and asks it to forward the port. Never throws: a failure is described in Problem.
        public static PortForwarding Open(int port, string description)
        {
            try
            {
                return Open(UpnpGateway.Discover(), port, description);
            }
            catch (Exception e)
            {
                return new PortForwarding(port, description, null, null, e.Message);
            }
        }

        // Same, through a router already found, or null if none was.
        public static PortForwarding Open(UpnpGateway gateway, int port, string description)
        {
            if (gateway == null)
            {
                return new PortForwarding(port, description, null, null, "no router answered, UPnP may be disabled on it");
            }

            if (!gateway.TryAddPortMapping(port, description, out string error))
            {
                return new PortForwarding(port, description, null, null, error);
            }

            gateway.TryGetExternalAddress(out IPAddress publicAddress);
            return new PortForwarding(port, description, gateway, publicAddress, null);
        }

        // Routers forget a forwarding after a while: to call from time to time while the server runs.
        public bool Renew()
        {
            return IsOpen && gateway.TryAddPortMapping(Port, description, out _);
        }

        // Removes the forwarding. No effect if the port was not opened.
        public void Close()
        {
            if (IsOpen) gateway.TryDeletePortMapping(Port);
        }
    }
}

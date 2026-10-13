namespace Voxel.Core
{
    // Reasons for which the server refuses a connection. The client only displays these: any
    // other disconnection text comes from the network layer and is not meant for the player.
    public static class ConnectionRefusal
    {
        public const string InvalidRequest = "Invalid connection request.";
        public const string WrongPassword = "Wrong password.";
        public const string AlreadyConnected = "This player is already connected.";

        public static bool IsKnown(string reason) =>
            reason == InvalidRequest || reason == WrongPassword || reason == AlreadyConnected;
    }
}

using System;
using System.IO;
using System.Text;

namespace Voxel.Core
{
    // What a client sends to the server when asking to connect: who it is, and the password.
    public sealed class ConnectionRequest
    {
        public const int MaxLength = 64;

        private const byte FormatVersion = 1;

        public ConnectionRequest(string playerId, string password)
        {
            PlayerId = playerId;
            Password = password;
        }

        // Stable id of the player, under which the server keeps their data from one session to the next.
        public string PlayerId { get; }

        public string Password { get; }

        public static bool IsValidPlayerId(string playerId)
        {
            if (string.IsNullOrEmpty(playerId) || playerId.Length > MaxLength) return false;

            foreach (char character in playerId)
            {
                bool allowed = (character >= 'a' && character <= 'z')
                    || (character >= 'A' && character <= 'Z')
                    || (character >= '0' && character <= '9')
                    || character == '-'
                    || character == '_';
                if (!allowed) return false;
            }

            return true;
        }

        public byte[] ToBytes()
        {
            var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(FormatVersion);
                writer.Write(PlayerId);
                writer.Write(Password);
            }

            return stream.ToArray();
        }

        // The data comes from the network: anything malformed is refused, without an exception.
        public static bool TryParse(byte[] data, out ConnectionRequest request)
        {
            request = null;
            if (data == null) return false;

            try
            {
                using (var reader = new BinaryReader(new MemoryStream(data), Encoding.UTF8))
                {
                    if (reader.ReadByte() != FormatVersion) return false;

                    string playerId = reader.ReadString();
                    string password = reader.ReadString();
                    if (!IsValidPlayerId(playerId) || password.Length > MaxLength) return false;

                    request = new ConnectionRequest(playerId, password);
                    return true;
                }
            }
            catch (Exception e) when (e is IOException || e is FormatException || e is ArgumentException)
            {
                return false;
            }
        }
    }
}

using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text;

namespace Voxel.Core
{
    public struct PlayerState
    {
        public Vector3 Position;

        // Direction du regard autour de l'axe vertical, en degrés.
        public float Yaw;
    }

    // Ce que le serveur retient de chaque joueur d'une partie à l'autre, par identifiant de joueur.
    public sealed class PlayerStore
    {
        private const uint Magic = 0x504F4345; // « ECOP »
        private const int FormatVersion = 1;

        private readonly Dictionary<string, PlayerState> players = new Dictionary<string, PlayerState>();

        public int Count => players.Count;

        public bool TryGet(string playerId, out PlayerState state) => players.TryGetValue(playerId, out state);

        public void Set(string playerId, PlayerState state) => players[playerId] = state;

        public void Write(Stream stream)
        {
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                writer.Write(Magic);
                writer.Write(FormatVersion);
                writer.Write(players.Count);
                foreach (KeyValuePair<string, PlayerState> player in players)
                {
                    writer.Write(player.Key);
                    writer.Write(player.Value.Position.X);
                    writer.Write(player.Value.Position.Y);
                    writer.Write(player.Value.Position.Z);
                    writer.Write(player.Value.Yaw);
                }
            }
        }

        // Lève InvalidDataException si le fichier n'est pas valide, EndOfStreamException s'il est tronqué.
        public static PlayerStore Read(Stream stream)
        {
            using (var reader = new BinaryReader(stream, Encoding.UTF8, true))
            {
                if (reader.ReadUInt32() != Magic)
                {
                    throw new InvalidDataException("Ce fichier n'est pas une sauvegarde de joueurs.");
                }

                int formatVersion = reader.ReadInt32();
                if (formatVersion != FormatVersion)
                {
                    throw new InvalidDataException(
                        $"Format de sauvegarde des joueurs {formatVersion} non pris en charge (attendu : {FormatVersion}).");
                }

                var store = new PlayerStore();
                int count = reader.ReadInt32();
                for (int i = 0; i < count; i++)
                {
                    string playerId = reader.ReadString();
                    var state = new PlayerState
                    {
                        Position = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
                        Yaw = reader.ReadSingle()
                    };
                    store.Set(playerId, state);
                }

                return store;
            }
        }

        public void SaveToFile(string path)
        {
            AtomicFile.Write(path, Write);
        }

        public static PlayerStore LoadFromFile(string path)
        {
            using (FileStream stream = File.OpenRead(path))
            {
                return Read(stream);
            }
        }
    }
}

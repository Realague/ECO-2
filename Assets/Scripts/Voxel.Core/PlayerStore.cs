using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text;

namespace Voxel.Core
{
    // Ce que le serveur retient d'un joueur d'une partie à l'autre.
    public sealed class PlayerState
    {
        public Vector3 Position;

        // Direction du regard autour de l'axe vertical, en degrés.
        public float Yaw;

        public Speciality Speciality;

        public Inventory Inventory { get; } = new Inventory();
    }

    // L'état de chaque joueur connu du serveur, par identifiant de joueur.
    public sealed class PlayerStore
    {
        private const uint Magic = 0x504F4345; // « ECOP »
        private const int FormatVersion = 2;

        // Dernier format sans spécialité ni inventaire. Il se lit encore : le joueur garde sa position.
        private const int FormatWithoutInventory = 1;

        private readonly Dictionary<string, PlayerState> players = new Dictionary<string, PlayerState>();

        public int Count => players.Count;

        public bool TryGet(string playerId, out PlayerState state) => players.TryGetValue(playerId, out state);

        public void Set(string playerId, PlayerState state) => players[playerId] = state;

        // L'état du joueur, créé vide s'il n'était pas encore connu.
        public PlayerState GetOrAdd(string playerId)
        {
            if (!players.TryGetValue(playerId, out PlayerState state))
            {
                state = new PlayerState();
                players.Add(playerId, state);
            }

            return state;
        }

        public void Write(Stream stream)
        {
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                writer.Write(Magic);
                writer.Write(FormatVersion);
                writer.Write(players.Count);
                foreach (KeyValuePair<string, PlayerState> player in players)
                {
                    PlayerState state = player.Value;
                    writer.Write(player.Key);
                    writer.Write(state.Position.X);
                    writer.Write(state.Position.Y);
                    writer.Write(state.Position.Z);
                    writer.Write(state.Yaw);
                    writer.Write((byte)state.Speciality);
                    state.Inventory.Write(writer);
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
                if (formatVersion != FormatVersion && formatVersion != FormatWithoutInventory)
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

                    if (formatVersion != FormatWithoutInventory)
                    {
                        var speciality = (Speciality)reader.ReadByte();
                        if (speciality != Speciality.None && !Specialities.IsChoice(speciality))
                        {
                            throw new InvalidDataException($"Spécialité inconnue pour le joueur {playerId}.");
                        }

                        state.Speciality = speciality;
                        state.Inventory.Read(reader);
                    }

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

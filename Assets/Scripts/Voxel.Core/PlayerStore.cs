using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text;

namespace Voxel.Core
{
    // What the server remembers of a player from one session to the next.
    public sealed class PlayerState
    {
        public Vector3 Position;

        // Direction of the gaze around the vertical axis, in degrees.
        public float Yaw;

        public Speciality Speciality;

        // Null until the player has chosen it.
        public Appearance? Appearance;

        public Inventory Inventory { get; } = new Inventory();
    }

    // The state of every player known to the server, by player id.
    public sealed class PlayerStore
    {
        private const uint Magic = 0x504F4345; // "ECOP"
        private const int FormatVersion = 3;

        // Older formats still load. Without inventory, the player keeps their position; without
        // appearance, they choose one at their next connection.
        private const int FormatWithoutInventory = 1;
        private const int FormatWithoutAppearance = 2;

        private readonly Dictionary<string, PlayerState> players = new Dictionary<string, PlayerState>();

        public int Count => players.Count;

        public bool TryGet(string playerId, out PlayerState state) => players.TryGetValue(playerId, out state);

        public void Set(string playerId, PlayerState state) => players[playerId] = state;

        // The player's state, created empty if they were not known yet.
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
                    writer.Write(state.Appearance?.Encode() ?? Appearance.NotChosen);
                }
            }
        }

        // Throws InvalidDataException if the file is not valid, EndOfStreamException if it is truncated.
        public static PlayerStore Read(Stream stream)
        {
            using (var reader = new BinaryReader(stream, Encoding.UTF8, true))
            {
                if (reader.ReadUInt32() != Magic)
                {
                    throw new InvalidDataException("This file is not a player save.");
                }

                int formatVersion = reader.ReadInt32();
                if (formatVersion != FormatVersion && formatVersion != FormatWithoutInventory && formatVersion != FormatWithoutAppearance)
                {
                    throw new InvalidDataException(
                        $"Unsupported player save format {formatVersion} (expected: {FormatVersion}).");
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
                            throw new InvalidDataException($"Unknown speciality for player {playerId}.");
                        }

                        state.Speciality = speciality;
                        state.Inventory.Read(reader);
                    }

                    if (formatVersion == FormatVersion)
                    {
                        ushort code = reader.ReadUInt16();
                        if (code != Appearance.NotChosen)
                        {
                            if (!Appearance.TryDecode(code, out Appearance appearance))
                            {
                                throw new InvalidDataException($"Unknown appearance for player {playerId}.");
                            }

                            state.Appearance = appearance;
                        }
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

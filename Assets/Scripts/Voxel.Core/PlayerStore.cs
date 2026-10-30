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

        // The ten quick slots, kept with the player (format 4).
        public Hotbar Hotbar { get; } = new Hotbar();

        // What they have eaten (format 5).
        public Stomach Stomach { get; } = new Stomach();

        // Whether they were given their starting tools (format 6): once only, even if they lose them.
        public bool ReceivedTools;

        // The name they chose (format 7), shown to the others; empty until they have chosen one.
        public string Name = "";

        // Whether they were given their starting chest and stockpile (format 7): once only.
        public bool ReceivedStorage;

        // Whether they were given their starting torches (format 8): once only.
        public bool ReceivedTorches;

        // Whether they were given a knife (format 9): once only, for players from before knives (new
        // players get it with their tools).
        public bool ReceivedKnife;

        // Whether they were given a hammer (format 10): once only, for players from before hammers.
        public bool ReceivedHammer;

        // Whether they play in creative mode (format 11): items for free, flight, one hit breaks
        // anything, no hunger nor carrying limits. Anyone may switch it on or off.
        public bool Creative;
    }

    // The state of every player known to the server, by player id.
    public sealed class PlayerStore
    {
        private const uint Magic = 0x504F4345; // "ECOP"
        private const int FormatVersion = 11;

        // Older formats still load. Without inventory, the player keeps their position; without
        // appearance, they choose one at their next connection; without a hotbar, theirs starts empty
        // (the server fills it with what they own); without a stomach, theirs starts half full;
        // without tools, they get them at their next connection; without a name or storage, they choose
        // a name and get a chest and a stockpile at their next connection; without torches, a knife or a hammer,
        // they get them at their next connection; without creative mode, they play in survival.
        private const int FormatWithoutInventory = 1;
        private const int FormatWithoutAppearance = 2;
        private const int FormatWithoutHotbar = 3;
        private const int FormatWithoutStomach = 4;
        private const int FormatWithoutTools = 5;
        private const int FormatWithoutName = 6;
        private const int FormatWithoutTorches = 7;
        private const int FormatWithoutKnife = 8;
        private const int FormatWithoutHammer = 9;
        private const int FormatWithoutCreative = 10;

        private readonly Dictionary<string, PlayerState> players = new Dictionary<string, PlayerState>();

        public int Count => players.Count;

        public bool TryGet(string playerId, out PlayerState state) => players.TryGetValue(playerId, out state);

        public void Set(string playerId, PlayerState state) => players[playerId] = state;

        // The player who chose this name (ignoring case).
        public bool TryFindByName(string name, out string playerId)
        {
            foreach (KeyValuePair<string, PlayerState> player in players)
            {
                if (PlayerNames.SameName(player.Value.Name, name))
                {
                    playerId = player.Key;
                    return true;
                }
            }

            playerId = null;
            return false;
        }

        // Whether a player other than playerId already has this name (ignoring case).
        public bool IsNameTaken(string name, string playerId)
        {
            foreach (KeyValuePair<string, PlayerState> player in players)
            {
                if (player.Key != playerId && PlayerNames.SameName(player.Value.Name, name)) return true;
            }

            return false;
        }

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
                    state.Hotbar.Write(writer);
                    state.Stomach.Write(writer);
                    writer.Write(state.ReceivedTools);
                    writer.Write(state.Name ?? "");
                    writer.Write(state.ReceivedStorage);
                    writer.Write(state.ReceivedTorches);
                    writer.Write(state.ReceivedKnife);
                    writer.Write(state.ReceivedHammer);
                    writer.Write(state.Creative);
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
                if (formatVersion < FormatWithoutInventory || formatVersion > FormatVersion)
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
                        // A speciality that no longer exists is dropped: the player chooses again.
                        var speciality = (Speciality)reader.ReadByte();
                        if (Specialities.IsRetired(speciality)) speciality = Speciality.None;
                        if (speciality != Speciality.None && !Specialities.IsChoice(speciality))
                        {
                            throw new InvalidDataException($"Unknown speciality for player {playerId}.");
                        }

                        state.Speciality = speciality;
                        state.Inventory.Read(reader);
                    }

                    if (formatVersion >= FormatWithoutHotbar)
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

                    if (formatVersion >= FormatWithoutStomach) state.Hotbar.Read(reader);
                    if (formatVersion >= FormatWithoutTools) state.Stomach.Read(reader);
                    if (formatVersion >= FormatWithoutName) state.ReceivedTools = reader.ReadBoolean();
                    if (formatVersion >= FormatWithoutTorches)
                    {
                        string name = reader.ReadString();
                        if (name.Length > 0 && (!PlayerNames.TryNormalize(name, out string normal) || normal != name))
                        {
                            throw new InvalidDataException($"Invalid name for player {playerId}.");
                        }

                        state.Name = name;
                        state.ReceivedStorage = reader.ReadBoolean();
                    }

                    if (formatVersion >= FormatWithoutKnife) state.ReceivedTorches = reader.ReadBoolean();
                    if (formatVersion >= FormatWithoutHammer) state.ReceivedKnife = reader.ReadBoolean();
                    if (formatVersion >= FormatWithoutCreative) state.ReceivedHammer = reader.ReadBoolean();
                    if (formatVersion >= FormatVersion) state.Creative = reader.ReadBoolean();

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

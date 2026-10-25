using System.Collections.Generic;
using Unity.Netcode;

namespace Voxel.Client
{
    // A player on an access list an owner manages (a storage's, their land's): their name, whether
    // they are connected, and whether the owner allows them.
    public readonly struct AccessEntry
    {
        public readonly string Name;
        public readonly bool Online;
        public readonly bool Allowed;

        public AccessEntry(string name, bool online, bool allowed)
        {
            Name = name;
            Online = online;
            Allowed = allowed;
        }

        public static void WriteList(FastBufferWriter writer, List<AccessEntry> entries)
        {
            writer.WriteValueSafe(entries.Count);
            foreach (AccessEntry entry in entries)
            {
                writer.WriteValueSafe(entry.Name);
                writer.WriteValueSafe(entry.Online);
                writer.WriteValueSafe(entry.Allowed);
            }
        }

        public static List<AccessEntry> ReadList(FastBufferReader reader)
        {
            reader.ReadValueSafe(out int count);
            var entries = new List<AccessEntry>();
            for (int i = 0; i < count; i++)
            {
                reader.ReadValueSafe(out string name);
                reader.ReadValueSafe(out bool online);
                reader.ReadValueSafe(out bool allowed);
                entries.Add(new AccessEntry(name, online, allowed));
            }

            return entries;
        }
    }
}

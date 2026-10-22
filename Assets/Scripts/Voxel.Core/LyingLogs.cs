using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text;

namespace Voxel.Core
{
    // A log lying on the ground, left by a felled tree: where its middle rests, which way it lies
    // (yaw in radians about the vertical) and the plant species it came from (its index, so that it
    // is drawn in that tree's bark). Picked up, it gives one wood.
    public readonly struct LyingLog
    {
        public readonly int Id;
        public readonly Vector3 Position;
        public readonly float Yaw;
        public readonly int Species;

        public LyingLog(int id, Vector3 position, float yaw, int species)
        {
            Id = id;
            Position = position;
            Yaw = yaw;
            Species = species;
        }

        public LyingLog MovedTo(Vector3 position) => new LyingLog(Id, position, Yaw, Species);
    }

    // The logs lying in a world, kept by the server and saved with it (logs.dat), with the logs of
    // each block column at hand so that a block changing under or into them is cheap to follow.
    public sealed class LyingLogs
    {
        private const uint Magic = 0x53474F4C; // "LOGS"
        private const int FormatVersion = 1;

        private readonly Dictionary<int, LyingLog> logs = new Dictionary<int, LyingLog>();
        private readonly Dictionary<long, List<int>> byColumn = new Dictionary<long, List<int>>();
        private int nextId = 1;

        public int Count => logs.Count;

        public IEnumerable<LyingLog> All => logs.Values;

        public LyingLog Add(Vector3 position, float yaw, int species)
        {
            var log = new LyingLog(nextId++, position, yaw, species);
            Insert(log);
            return log;
        }

        public bool TryGet(int id, out LyingLog log) => logs.TryGetValue(id, out log);

        public bool Remove(int id)
        {
            if (!logs.TryGetValue(id, out LyingLog log)) return false;

            logs.Remove(id);
            Unindex(log);
            return true;
        }

        public bool Move(int id, Vector3 position)
        {
            if (!logs.TryGetValue(id, out LyingLog log)) return false;

            Unindex(log);
            Insert(log.MovedTo(position));
            return true;
        }

        // The ids of the logs lying in a column (into is cleared first).
        public void InColumn(int x, int z, List<int> into)
        {
            into.Clear();
            if (byColumn.TryGetValue(ColumnKey(x, z), out List<int> ids)) into.AddRange(ids);
        }

        public void Write(Stream stream)
        {
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                writer.Write(Magic);
                writer.Write(FormatVersion);
                writer.Write(nextId);
                writer.Write(logs.Count);
                foreach (LyingLog log in logs.Values)
                {
                    writer.Write(log.Id);
                    writer.Write(log.Position.X);
                    writer.Write(log.Position.Y);
                    writer.Write(log.Position.Z);
                    writer.Write(log.Yaw);
                    writer.Write(log.Species);
                }
            }
        }

        // Throws InvalidDataException if the file is not valid, EndOfStreamException if it is truncated.
        public static LyingLogs Read(Stream stream)
        {
            using (var reader = new BinaryReader(stream, Encoding.UTF8, true))
            {
                if (reader.ReadUInt32() != Magic) throw new InvalidDataException("This file is not a log save.");

                int format = reader.ReadInt32();
                if (format != FormatVersion) throw new InvalidDataException($"Unsupported log save format {format} (expected: {FormatVersion}).");

                var store = new LyingLogs { nextId = reader.ReadInt32() };
                int count = reader.ReadInt32();
                if (count < 0) throw new InvalidDataException("Invalid log count.");

                for (int i = 0; i < count; i++)
                {
                    int id = reader.ReadInt32();
                    var position = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                    float yaw = reader.ReadSingle();
                    int species = reader.ReadInt32();
                    if (id <= 0 || id >= store.nextId || store.logs.ContainsKey(id)) throw new InvalidDataException($"Invalid log id {id}.");
                    if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z) || !float.IsFinite(yaw)) throw new InvalidDataException("Invalid log position.");
                    if (species < 0) throw new InvalidDataException($"Invalid log species {species}.");

                    store.Insert(new LyingLog(id, position, yaw, species));
                }

                return store;
            }
        }

        public void SaveToFile(string path)
        {
            AtomicFile.Write(path, Write);
        }

        public static LyingLogs LoadFromFile(string path)
        {
            using (FileStream stream = File.OpenRead(path))
            {
                return Read(stream);
            }
        }

        public static long ColumnKey(int x, int z) => ((long)x << 32) | (uint)z;

        private static long ColumnOf(Vector3 position) => ColumnKey((int)System.MathF.Floor(position.X), (int)System.MathF.Floor(position.Z));

        private void Insert(LyingLog log)
        {
            logs[log.Id] = log;
            long key = ColumnOf(log.Position);
            if (!byColumn.TryGetValue(key, out List<int> ids))
            {
                ids = new List<int>();
                byColumn.Add(key, ids);
            }

            ids.Add(log.Id);
        }

        private void Unindex(LyingLog log)
        {
            long key = ColumnOf(log.Position);
            if (!byColumn.TryGetValue(key, out List<int> ids)) return;

            ids.Remove(log.Id);
            if (ids.Count == 0) byColumn.Remove(key);
        }
    }
}

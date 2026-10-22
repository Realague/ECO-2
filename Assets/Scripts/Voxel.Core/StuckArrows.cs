using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text;

namespace Voxel.Core
{
    // An arrow that has landed: where its point is and which way it flew, so it is drawn stuck in
    // what it hit. It stays until a player picks it up.
    public readonly struct StuckArrow
    {
        public readonly int Id;
        public readonly Vector3 Position;
        public readonly Vector3 Direction;

        public StuckArrow(int id, Vector3 position, Vector3 direction)
        {
            Id = id;
            Position = position;
            Direction = direction;
        }
    }

    // The arrows lying in a world, kept by the server and saved with it (arrows.dat).
    public sealed class StuckArrows
    {
        private const uint Magic = 0x4F525241; // "ARRO"
        private const int FormatVersion = 1;

        private readonly Dictionary<int, StuckArrow> arrows = new Dictionary<int, StuckArrow>();
        private int nextId = 1;

        public int Count => arrows.Count;

        public IEnumerable<StuckArrow> All => arrows.Values;

        public StuckArrow Add(Vector3 position, Vector3 direction)
        {
            var arrow = new StuckArrow(nextId++, position, direction);
            arrows.Add(arrow.Id, arrow);
            return arrow;
        }

        public bool TryGet(int id, out StuckArrow arrow) => arrows.TryGetValue(id, out arrow);

        public bool Remove(int id) => arrows.Remove(id);

        public void Write(Stream stream)
        {
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                writer.Write(Magic);
                writer.Write(FormatVersion);
                writer.Write(nextId);
                writer.Write(arrows.Count);
                foreach (StuckArrow arrow in arrows.Values)
                {
                    writer.Write(arrow.Id);
                    WriteVector(writer, arrow.Position);
                    WriteVector(writer, arrow.Direction);
                }
            }
        }

        // Throws InvalidDataException if the file is not valid, EndOfStreamException if it is truncated.
        public static StuckArrows Read(Stream stream)
        {
            using (var reader = new BinaryReader(stream, Encoding.UTF8, true))
            {
                if (reader.ReadUInt32() != Magic) throw new InvalidDataException("This file is not an arrow save.");

                int format = reader.ReadInt32();
                if (format != FormatVersion) throw new InvalidDataException($"Unsupported arrow save format {format} (expected: {FormatVersion}).");

                var store = new StuckArrows { nextId = reader.ReadInt32() };
                int count = reader.ReadInt32();
                if (count < 0) throw new InvalidDataException("Invalid arrow count.");

                for (int i = 0; i < count; i++)
                {
                    int id = reader.ReadInt32();
                    Vector3 position = ReadVector(reader);
                    Vector3 direction = ReadVector(reader);
                    if (id <= 0 || id >= store.nextId || store.arrows.ContainsKey(id)) throw new InvalidDataException($"Invalid arrow id {id}.");

                    store.arrows.Add(id, new StuckArrow(id, position, direction));
                }

                return store;
            }
        }

        public void SaveToFile(string path)
        {
            AtomicFile.Write(path, Write);
        }

        public static StuckArrows LoadFromFile(string path)
        {
            using (FileStream stream = File.OpenRead(path))
            {
                return Read(stream);
            }
        }

        private static void WriteVector(BinaryWriter writer, Vector3 v)
        {
            writer.Write(v.X);
            writer.Write(v.Y);
            writer.Write(v.Z);
        }

        private static Vector3 ReadVector(BinaryReader reader)
        {
            var v = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            if (!float.IsFinite(v.X) || !float.IsFinite(v.Y) || !float.IsFinite(v.Z)) throw new InvalidDataException("Invalid arrow position.");

            return v;
        }
    }
}

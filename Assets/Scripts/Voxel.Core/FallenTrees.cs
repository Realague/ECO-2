using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text;

namespace Voxel.Core
{
    // How a felled tree is worked (FallenTrees): its branches pruned with the axe first, then its
    // trunk bucked into logs, one at a time from its top.
    public static class FallenTreeRules
    {
        // Axe hits per branch, and per log bucked off the trunk.
        public const int HitsPerBranch = 2;
        public const int HitsPerLog = 2;

        // A tree's branches by the wood in it: one for every two, at least one.
        public static int BranchesOf(int wood) => wood <= 0 ? 0 : Math.Max(1, (wood + 1) / 2);

        // The hits the next part of a fallen tree takes: a branch while any is left, else a log.
        public static int HitsFor(FallenTree tree) => tree.Branches > 0 ? HitsPerBranch : HitsPerLog;
    }

    // One log still part of a fallen tree's trunk: where it will lie once bucked off.
    public readonly struct FallenSegment
    {
        public readonly Vector3 Position;
        public readonly float Yaw;

        public FallenSegment(Vector3 position, float yaw)
        {
            Position = position;
            Yaw = yaw;
        }
    }

    // A felled tree lying where it fell: its species, the column and size it grew at (to draw it lying
    // as it fell), the way it fell, the branches still on it and the logs its trunk still holds (from
    // the foot to the top: bucking takes the last one first).
    public sealed class FallenTree
    {
        public int Id;
        public int Species;
        public int Stage;
        public int X;
        public int Y;
        public int Z;
        public float Yaw;
        public int Branches;
        public readonly List<FallenSegment> Segments = new List<FallenSegment>();

        public bool IsPruned => Branches <= 0;
    }

    // The trees lying felled in a world, kept by the server and saved with it (fallen.dat).
    public sealed class FallenTrees
    {
        private const uint Magic = 0x4C4C4146; // "FALL"
        private const int FormatVersion = 1;

        private readonly Dictionary<int, FallenTree> trees = new Dictionary<int, FallenTree>();
        private int nextId = 1;

        public int Count => trees.Count;

        public IEnumerable<FallenTree> All => trees.Values;

        public FallenTree Add(int species, int stage, int x, int y, int z, float yaw, int branches, IEnumerable<FallenSegment> segments)
        {
            var tree = new FallenTree { Id = nextId++, Species = species, Stage = stage, X = x, Y = y, Z = z, Yaw = yaw, Branches = Math.Max(0, branches) };
            tree.Segments.AddRange(segments);
            trees[tree.Id] = tree;
            return tree;
        }

        // Puts a tree known by its id (a client's copy of what the server sent).
        public void Put(FallenTree tree)
        {
            trees[tree.Id] = tree;
            if (tree.Id >= nextId) nextId = tree.Id + 1;
        }

        public bool TryGet(int id, out FallenTree tree) => trees.TryGetValue(id, out tree);

        public bool Remove(int id) => trees.Remove(id);

        public void Clear() => trees.Clear();

        // Cuts one branch off; false if it has none left.
        public bool Prune(int id)
        {
            if (!trees.TryGetValue(id, out FallenTree tree) || tree.Branches <= 0) return false;

            tree.Branches--;
            return true;
        }

        // Bucks the top log off a pruned tree: false while it still has branches. The tree goes once
        // its last log is off.
        public bool Buck(int id, out FallenSegment log)
        {
            log = default;
            if (!trees.TryGetValue(id, out FallenTree tree) || tree.Branches > 0 || tree.Segments.Count == 0) return false;

            log = tree.Segments[tree.Segments.Count - 1];
            tree.Segments.RemoveAt(tree.Segments.Count - 1);
            if (tree.Segments.Count == 0) trees.Remove(id);
            return true;
        }

        // The trees lying over a column (a log of their trunk in it, or their foot).
        public void InColumn(int x, int z, List<int> into)
        {
            into.Clear();
            foreach (FallenTree tree in trees.Values)
            {
                if (tree.X == x && tree.Z == z)
                {
                    into.Add(tree.Id);
                    continue;
                }

                foreach (FallenSegment segment in tree.Segments)
                {
                    if ((int)MathF.Floor(segment.Position.X) == x && (int)MathF.Floor(segment.Position.Z) == z)
                    {
                        into.Add(tree.Id);
                        break;
                    }
                }
            }
        }

        public static void WriteTree(BinaryWriter writer, FallenTree tree)
        {
            writer.Write(tree.Id);
            writer.Write(tree.Species);
            writer.Write(tree.Stage);
            writer.Write(tree.X);
            writer.Write(tree.Y);
            writer.Write(tree.Z);
            writer.Write(tree.Yaw);
            writer.Write(tree.Branches);
            writer.Write(tree.Segments.Count);
            foreach (FallenSegment segment in tree.Segments)
            {
                writer.Write(segment.Position.X);
                writer.Write(segment.Position.Y);
                writer.Write(segment.Position.Z);
                writer.Write(segment.Yaw);
            }
        }

        public static FallenTree ReadTree(BinaryReader reader)
        {
            var tree = new FallenTree
            {
                Id = reader.ReadInt32(),
                Species = reader.ReadInt32(),
                Stage = reader.ReadInt32(),
                X = reader.ReadInt32(),
                Y = reader.ReadInt32(),
                Z = reader.ReadInt32(),
                Yaw = reader.ReadSingle(),
                Branches = reader.ReadInt32()
            };

            int count = reader.ReadInt32();
            if (tree.Id <= 0 || tree.Species < 0 || tree.Branches < 0 || count < 0 || count > 1000 || !float.IsFinite(tree.Yaw)) throw new InvalidDataException("Invalid fallen tree.");

            for (int i = 0; i < count; i++)
            {
                var position = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                float yaw = reader.ReadSingle();
                if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z) || !float.IsFinite(yaw)) throw new InvalidDataException("Invalid fallen tree log.");
                tree.Segments.Add(new FallenSegment(position, yaw));
            }

            return tree;
        }

        public void Write(Stream stream)
        {
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                writer.Write(Magic);
                writer.Write(FormatVersion);
                writer.Write(nextId);
                writer.Write(trees.Count);
                foreach (FallenTree tree in trees.Values) WriteTree(writer, tree);
            }
        }

        // Throws InvalidDataException if the file is not valid, EndOfStreamException if it is truncated.
        public static FallenTrees Read(Stream stream)
        {
            using (var reader = new BinaryReader(stream, Encoding.UTF8, true))
            {
                if (reader.ReadUInt32() != Magic) throw new InvalidDataException("This file is not a fallen trees save.");

                int format = reader.ReadInt32();
                if (format != FormatVersion) throw new InvalidDataException($"Unsupported fallen trees save format {format} (expected: {FormatVersion}).");

                var store = new FallenTrees { nextId = reader.ReadInt32() };
                int count = reader.ReadInt32();
                if (count < 0) throw new InvalidDataException("Invalid fallen tree count.");

                for (int i = 0; i < count; i++)
                {
                    FallenTree tree = ReadTree(reader);
                    if (tree.Id >= store.nextId || store.trees.ContainsKey(tree.Id)) throw new InvalidDataException($"Invalid fallen tree id {tree.Id}.");
                    store.trees.Add(tree.Id, tree);
                }

                return store;
            }
        }

        public void SaveToFile(string path)
        {
            AtomicFile.Write(path, Write);
        }

        public static FallenTrees LoadFromFile(string path)
        {
            using (FileStream stream = File.OpenRead(path))
            {
                return Read(stream);
            }
        }
    }
}

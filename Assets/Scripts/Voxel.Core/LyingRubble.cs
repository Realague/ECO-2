using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text;

namespace Voxel.Core
{
    // The rubble a block broken with a pickaxe leaves on the ground (MiningRules.LeavesRubble): where
    // it rests (the middle of its underside), which way it lies (yaw in radians about the vertical)
    // and the block it came from, whole, so that it is drawn in that rock and gives back the rock and
    // the ore it held (Items.DropOf, Ores.OreOf) when picked up.
    public readonly struct Rubble
    {
        public readonly int Id;
        public readonly Vector3 Position;
        public readonly float Yaw;
        public readonly ushort Block;

        public Rubble(int id, Vector3 position, float yaw, ushort block)
        {
            Id = id;
            Position = position;
            Yaw = yaw;
            Block = block;
        }

        public Rubble MovedTo(Vector3 position) => new Rubble(Id, position, Yaw, Block);
    }

    // The rubble lying in a world, kept by the server and saved with it (rubble.dat), with that of
    // each block column at hand so that a block changing under or into it is cheap to follow.
    public sealed class RubblePiles
    {
        private const uint Magic = 0x4C425552; // "RUBL"
        // Format 2 adds the cells where players placed a block that leaves rubble (format 1 still loads).
        private const int FormatVersion = 2;

        private readonly Dictionary<int, Rubble> piles = new Dictionary<int, Rubble>();
        private readonly Dictionary<long, List<int>> byColumn = new Dictionary<long, List<int>>();
        private readonly HashSet<BlockPos> placed = new HashSet<BlockPos>();
        private int nextId = 1;

        public int Count => piles.Count;

        public IEnumerable<Rubble> All => piles.Values;

        public Rubble Add(Vector3 position, float yaw, ushort block)
        {
            var rubble = new Rubble(nextId++, position, yaw, block);
            Insert(rubble);
            return rubble;
        }

        public bool TryGet(int id, out Rubble rubble) => piles.TryGetValue(id, out rubble);

        public bool Remove(int id)
        {
            if (!piles.TryGetValue(id, out Rubble rubble)) return false;

            piles.Remove(id);
            Unindex(rubble);
            return true;
        }

        public bool Move(int id, Vector3 position)
        {
            if (!piles.TryGetValue(id, out Rubble rubble)) return false;

            Unindex(rubble);
            Insert(rubble.MovedTo(position));
            return true;
        }

        // A player placed a block that leaves rubble here: broken again, it leaves one piece, not
        // MiningRules.RubblePieces, or placing and breaking stone would make more of it.
        public void MarkPlaced(BlockPos pos) => placed.Add(pos);

        // Whether a player placed the block here, forgotten once asked (the block is being broken).
        public bool TakePlaced(BlockPos pos) => placed.Remove(pos);

        public int PlacedCount => placed.Count;

        // The ids of the rubble lying in a column (into is cleared first).
        public void InColumn(int x, int z, List<int> into)
        {
            into.Clear();
            if (byColumn.TryGetValue(LyingLogs.ColumnKey(x, z), out List<int> ids)) into.AddRange(ids);
        }

        public void Write(Stream stream)
        {
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                writer.Write(Magic);
                writer.Write(FormatVersion);
                writer.Write(nextId);
                writer.Write(piles.Count);
                foreach (Rubble rubble in piles.Values)
                {
                    writer.Write(rubble.Id);
                    writer.Write(rubble.Position.X);
                    writer.Write(rubble.Position.Y);
                    writer.Write(rubble.Position.Z);
                    writer.Write(rubble.Yaw);
                    writer.Write(rubble.Block);
                }

                writer.Write(placed.Count);
                foreach (BlockPos pos in placed)
                {
                    writer.Write(pos.X);
                    writer.Write(pos.Y);
                    writer.Write(pos.Z);
                }
            }
        }

        // Throws InvalidDataException if the file is not valid, EndOfStreamException if it is truncated.
        public static RubblePiles Read(Stream stream)
        {
            using (var reader = new BinaryReader(stream, Encoding.UTF8, true))
            {
                if (reader.ReadUInt32() != Magic) throw new InvalidDataException("This file is not a rubble save.");

                int format = reader.ReadInt32();
                if (format != 1 && format != FormatVersion) throw new InvalidDataException($"Unsupported rubble save format {format} (expected: {FormatVersion}).");

                var store = new RubblePiles { nextId = reader.ReadInt32() };
                int count = reader.ReadInt32();
                if (count < 0) throw new InvalidDataException("Invalid rubble count.");

                for (int i = 0; i < count; i++)
                {
                    int id = reader.ReadInt32();
                    var position = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                    float yaw = reader.ReadSingle();
                    ushort block = reader.ReadUInt16();
                    if (id <= 0 || id >= store.nextId || store.piles.ContainsKey(id)) throw new InvalidDataException($"Invalid rubble id {id}.");
                    if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z) || !float.IsFinite(yaw)) throw new InvalidDataException("Invalid rubble position.");
                    if (!Blocks.IsValid(block) || Blocks.TypeOf(block) == Blocks.Air) throw new InvalidDataException($"Invalid rubble block {block}.");

                    store.Insert(new Rubble(id, position, yaw, block));
                }

                if (format >= 2)
                {
                    int placedCount = reader.ReadInt32();
                    if (placedCount < 0) throw new InvalidDataException("Invalid placed count.");

                    for (int i = 0; i < placedCount; i++)
                    {
                        store.placed.Add(new BlockPos(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32()));
                    }
                }

                return store;
            }
        }

        public void SaveToFile(string path)
        {
            AtomicFile.Write(path, Write);
        }

        public static RubblePiles LoadFromFile(string path)
        {
            using (FileStream stream = File.OpenRead(path))
            {
                return Read(stream);
            }
        }

        private static long ColumnOf(Vector3 position) => LyingLogs.ColumnKey((int)System.MathF.Floor(position.X), (int)System.MathF.Floor(position.Z));

        private void Insert(Rubble rubble)
        {
            piles[rubble.Id] = rubble;
            long key = ColumnOf(rubble.Position);
            if (!byColumn.TryGetValue(key, out List<int> ids))
            {
                ids = new List<int>();
                byColumn.Add(key, ids);
            }

            ids.Add(rubble.Id);
        }

        private void Unindex(Rubble rubble)
        {
            long key = ColumnOf(rubble.Position);
            if (!byColumn.TryGetValue(key, out List<int> ids)) return;

            ids.Remove(rubble.Id);
            if (ids.Count == 0) byColumn.Remove(key);
        }
    }

    // Where the pieces of a broken block come to rest: spread evenly around the middle of its cell
    // (from turn, radians), about half a block out, so that some land in the columns beside it, then
    // fallen to the ground there (sinking through water). A piece whose way out is solid goes onto
    // it, out of the hole, when there is room above both; else (a wall, the world's edge) it stays
    // in the block's own cell, nearer its middle.
    public static class RubbleScatter
    {
        public const float Reach = 0.5f;
        public const float NearReach = 0.2f;

        public static void Spots(World world, BlockPos pos, int count, float turn, List<Vector3> into)
        {
            into.Clear();
            for (int i = 0; i < count; i++)
            {
                float angle = turn + i * 2f * System.MathF.PI / count;
                var way = new Vector3(System.MathF.Cos(angle), 0f, System.MathF.Sin(angle));
                Vector3 middle = new Vector3(pos.X + 0.5f, pos.Y, pos.Z + 0.5f);
                Vector3 spot = count == 1 ? middle : middle + way * Reach;
                int x = (int)System.MathF.Floor(spot.X);
                int z = (int)System.MathF.Floor(spot.Z);
                if (!world.InBounds(x, pos.Y, z))
                {
                    spot = middle + way * NearReach;
                }
                else if (world.IsSolid(x, pos.Y, z))
                {
                    // Out of a hole, onto its rim if there is room over both; else it stays in the cell.
                    bool rim = world.InBounds(x, pos.Y + 1, z) && !world.IsSolid(x, pos.Y + 1, z) && !world.IsSolid(pos.X, pos.Y + 1, pos.Z);
                    spot = rim ? spot + new Vector3(0f, 1f, 0f) : middle + way * NearReach;
                }

                if (LogPlacement.Settle(world, spot, out Vector3 settled, false)) spot = settled;
                into.Add(spot);
            }
        }
    }
}

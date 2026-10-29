using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Voxel.Core
{
    // A chest, a stockpile or a station placed in the world: where (a chest's or station's cell, a
    // stockpile's corner, at the height of the cells above the ground), facing which way, whose (the
    // player id of who placed it), who else may open it, and what it holds.
    public sealed class Storage
    {
        public readonly int Id;
        public readonly StorageKind Kind;
        public readonly BlockPos Position;
        public readonly int Facing;
        public readonly string OwnerId;
        public readonly HashSet<string> Allowed = new HashSet<string>();

        // A chest's items (null for the others).
        public readonly Inventory Contents;

        // A stockpile's piles (null for the others).
        public readonly StockpilePiles Piles;

        // A station's queue, and a campfire's wood (null for the others).
        public readonly Station Station;

        public Storage(int id, StorageKind kind, BlockPos position, int facing, string ownerId)
        {
            Id = id;
            Kind = kind;
            Position = position;
            Facing = facing;
            OwnerId = ownerId;
            if (kind == StorageKind.Chest) Contents = new Inventory();
            else if (kind == StorageKind.Campfire) Station = Station.Campfire();
            else if (kind == StorageKind.ButcherTable) Station = Station.ButcherTable();
            else Piles = new StockpilePiles(kind);
        }

        public bool IsEmpty
        {
            get
            {
                switch (Kind)
                {
                    case StorageKind.Chest: return Contents.DistinctCount == 0;
                    case StorageKind.Campfire:
                    case StorageKind.ButcherTable: return Station.IsEmpty;
                    default: return Piles.IsEmpty;
                }
            }
        }

        public bool CanOpen(string playerId) => !string.IsNullOrEmpty(playerId) && (playerId == OwnerId || Allowed.Contains(playerId));

        public int CellCount => StorageRules.IsSingleCell(Kind) ? 1 : StorageRules.PileCountOf(Kind);

        // A cell it covers (0 to CellCount - 1): a chest's or station's own, or a stockpile's piles in order.
        public BlockPos Cell(int index) => StorageRules.IsSingleCell(Kind) ? Position : StorageRules.PileCell(Position, index, StorageRules.SideOf(Kind));

        // Whether a cell it covers is solid: a chest or a station always, a stockpile's pile when it holds something.
        public bool IsSolidAt(int index) => StorageRules.IsSingleCell(Kind) || Piles.CountAt(index) > 0;
    }

    // The storage placed in a world, kept by the server and saved with it (storage.dat), with each
    // covered cell at hand: what stands there, and whether it stops players and blocks.
    public sealed class Storages
    {
        private const uint Magic = 0x524F5453; // "STOR"
        // Format 2 adds campfires (their wood and queue after them), format 3 orders of several
        // dishes (Station.Write), format 4 butcher's tables (the same as a campfire after them);
        // formats 1 to 3 still load.
        private const int FormatVersion = 4;

        private readonly Dictionary<int, Storage> storages = new Dictionary<int, Storage>();

        // Each covered cell: its storage and its index among that storage's cells.
        private readonly Dictionary<BlockPos, (int id, int index)> byCell = new Dictionary<BlockPos, (int id, int index)>();
        private int nextId = 1;

        public int Count => storages.Count;

        public IEnumerable<Storage> All => storages.Values;

        public bool TryGet(int id, out Storage storage) => storages.TryGetValue(id, out storage);

        // Whether any of these cells is covered already.
        public bool AnyCovered(IEnumerable<BlockPos> cells)
        {
            foreach (BlockPos cell in cells)
            {
                if (byCell.ContainsKey(cell)) return true;
            }

            return false;
        }

        public Storage Add(StorageKind kind, BlockPos position, int facing, string ownerId)
        {
            var storage = new Storage(nextId++, kind, position, facing, ownerId);
            Insert(storage);
            return storage;
        }

        // Puts a storage known by its id (a client's copy of what the server sent), replacing any
        // storage with that id.
        public void Put(Storage storage)
        {
            Remove(storage.Id);
            Insert(storage);
            if (storage.Id >= nextId) nextId = storage.Id + 1;
        }

        public bool Remove(int id)
        {
            if (!storages.TryGetValue(id, out Storage storage)) return false;

            storages.Remove(id);
            for (int i = 0; i < storage.CellCount; i++)
            {
                byCell.Remove(storage.Cell(i));
            }

            return true;
        }

        // The storage covering a cell, and the cell's index in it.
        public bool TryAt(BlockPos cell, out Storage storage, out int index)
        {
            storage = null;
            index = -1;
            if (!byCell.TryGetValue(cell, out (int id, int index) entry)) return false;

            index = entry.index;
            return storages.TryGetValue(entry.id, out storage);
        }

        // Whether a cell is solid because of storage (StorageRules: a chest, or a pile holding something).
        public bool IsSolid(int x, int y, int z)
        {
            return TryAt(new BlockPos(x, y, z), out Storage storage, out int index) && storage.IsSolidAt(index);
        }

        public void Write(Stream stream)
        {
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                writer.Write(Magic);
                writer.Write(FormatVersion);
                writer.Write(nextId);
                writer.Write(storages.Count);
                foreach (Storage storage in storages.Values)
                {
                    writer.Write(storage.Id);
                    writer.Write((byte)storage.Kind);
                    writer.Write(storage.Position.X);
                    writer.Write(storage.Position.Y);
                    writer.Write(storage.Position.Z);
                    writer.Write((byte)storage.Facing);
                    writer.Write(storage.OwnerId);
                    writer.Write(storage.Allowed.Count);
                    foreach (string playerId in storage.Allowed)
                    {
                        writer.Write(playerId);
                    }

                    if (storage.Kind == StorageKind.Chest)
                    {
                        writer.Write(storage.Contents.DistinctCount);
                        foreach (KeyValuePair<int, int> entry in storage.Contents.Entries)
                        {
                            writer.Write(entry.Key);
                            writer.Write(entry.Value);
                        }
                    }
                    else if (StorageRules.IsStation(storage.Kind))
                    {
                        storage.Station.Write(writer);
                    }
                    else
                    {
                        storage.Piles.Write(writer);
                    }
                }
            }
        }

        // Throws InvalidDataException if the file is not valid, EndOfStreamException if it is truncated.
        // Items that no longer exist are dropped from what storage holds.
        public static Storages Read(Stream stream)
        {
            using (var reader = new BinaryReader(stream, Encoding.UTF8, true))
            {
                if (reader.ReadUInt32() != Magic) throw new InvalidDataException("This file is not a storage save.");

                int format = reader.ReadInt32();
                if (format < 1 || format > FormatVersion) throw new InvalidDataException($"Unsupported storage save format {format} (expected: {FormatVersion}).");

                var store = new Storages { nextId = reader.ReadInt32() };
                int count = reader.ReadInt32();
                if (count < 0) throw new InvalidDataException("Invalid storage count.");

                for (int i = 0; i < count; i++)
                {
                    int id = reader.ReadInt32();
                    byte kind = reader.ReadByte();
                    var position = new BlockPos(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32());
                    int facing = reader.ReadByte();
                    string ownerId = reader.ReadString();
                    if (id <= 0 || id >= store.nextId || store.storages.ContainsKey(id)) throw new InvalidDataException($"Invalid storage id {id}.");
                    if (!StorageRules.IsKind(kind) || facing > 3 || ownerId.Length == 0) throw new InvalidDataException($"Invalid storage {id}.");
                    if (format < 2 && kind == (byte)StorageKind.Campfire) throw new InvalidDataException($"Campfire {id} in a save from before campfires.");
                    if (format < 4 && kind == (byte)StorageKind.ButcherTable) throw new InvalidDataException($"Butcher's table {id} in a save from before them.");

                    var storage = new Storage(id, (StorageKind)kind, position, facing, ownerId);
                    int allowed = reader.ReadInt32();
                    if (allowed < 0) throw new InvalidDataException($"Invalid access list of storage {id}.");

                    for (int a = 0; a < allowed; a++)
                    {
                        storage.Allowed.Add(reader.ReadString());
                    }

                    if (storage.Kind == StorageKind.Chest)
                    {
                        int entries = reader.ReadInt32();
                        if (entries < 0) throw new InvalidDataException($"Invalid contents of storage {id}.");

                        for (int e = 0; e < entries; e++)
                        {
                            int item = Items.Upgrade(reader.ReadInt32());
                            int amount = reader.ReadInt32();
                            if (amount > 0 && StorageRules.AcceptsInChest(item)) storage.Contents.Add(item, amount);
                        }
                    }
                    else if (StorageRules.IsStation(storage.Kind))
                    {
                        storage.Station.Read(reader, format);
                    }
                    else
                    {
                        storage.Piles.Read(reader);
                    }

                    var cells = new List<BlockPos>();
                    for (int c = 0; c < storage.CellCount; c++)
                    {
                        cells.Add(storage.Cell(c));
                    }

                    if (store.AnyCovered(cells)) throw new InvalidDataException($"Storage {id} overlaps another.");

                    store.Insert(storage);
                }

                return store;
            }
        }

        public void SaveToFile(string path)
        {
            AtomicFile.Write(path, Write);
        }

        public static Storages LoadFromFile(string path)
        {
            using (FileStream stream = File.OpenRead(path))
            {
                return Read(stream);
            }
        }

        private void Insert(Storage storage)
        {
            storages[storage.Id] = storage;
            for (int i = 0; i < storage.CellCount; i++)
            {
                byCell[storage.Cell(i)] = (storage.Id, i);
            }
        }
    }
}

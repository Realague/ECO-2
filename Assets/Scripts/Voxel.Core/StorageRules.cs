using System;
using System.Collections.Generic;

namespace Voxel.Core
{
    public enum StorageKind : byte
    {
        Chest = 1,
        Stockpile = 2,
        SmallStockpile = 3,

        // Not storage but placed like it (one cell, like a chest): a station that cooks (Campfire).
        Campfire = 4
    }

    // What storage holds and where it may stand. A chest stands in one cell on top of a solid block
    // and holds any item that is not a block, up to ChestMaxWeight kg in all; a stockpile covers
    // StockpileSide x StockpileSide cells of flat ground and holds blocks (and wood), one pile per
    // cell of one block type each, up to PileSize per pile; a small stockpile the same on
    // SmallStockpileSide x SmallStockpileSide cells, up to SmallPileSize per pile. Facing is a quarter turn (0 toward +z,
    // 1 toward +x, 2 toward -z, 3 toward -x), the way the player looked when placing it.
    public static class StorageRules
    {
        public const float ChestMaxWeight = 150f;
        public const int StockpileSide = 3;
        public const int PileCount = StockpileSide * StockpileSide;

        // A full pile is drawn as 3 x 3 x 3 small cubes: exactly one block's volume.
        public const int PileSize = 27;
        public const int StockpileCapacity = PileCount * PileSize;

        // The small stockpile every player is given: 2 x 2 piles of 20, 80 blocks.
        public const int SmallStockpileSide = 2;
        public const int SmallPileSize = 20;

        public static bool IsKind(byte kind) => kind >= (byte)StorageKind.Chest && kind <= (byte)StorageKind.Campfire;

        // Whether it stands in one cell like a chest (a chest or a campfire), rather than on a patch.
        public static bool IsSingleCell(StorageKind kind) => !IsStockpile(kind);

        public static bool IsStockpile(StorageKind kind) => kind == StorageKind.Stockpile || kind == StorageKind.SmallStockpile;

        // A stockpile's side in cells, its number of piles, the blocks a pile holds and in all.
        public static int SideOf(StorageKind kind) => kind == StorageKind.SmallStockpile ? SmallStockpileSide : StockpileSide;

        public static int PileCountOf(StorageKind kind) => SideOf(kind) * SideOf(kind);

        public static int PileSizeOf(StorageKind kind) => kind == StorageKind.SmallStockpile ? SmallPileSize : PileSize;

        public static int CapacityOf(StorageKind kind) => PileCountOf(kind) * PileSizeOf(kind);

        public static int ItemOf(StorageKind kind)
        {
            switch (kind)
            {
                case StorageKind.Chest: return Items.Chest;
                case StorageKind.SmallStockpile: return Items.SmallStockpile;
                case StorageKind.Campfire: return Items.Campfire;
                default: return Items.Stockpile;
            }
        }

        public static bool TryKindOf(int item, out StorageKind kind)
        {
            switch (item)
            {
                case Items.Chest: kind = StorageKind.Chest; break;
                case Items.SmallStockpile: kind = StorageKind.SmallStockpile; break;
                case Items.Campfire: kind = StorageKind.Campfire; break;
                default: kind = StorageKind.Stockpile; break;
            }

            return Items.IsStorage(item);
        }

        public static bool AcceptsInChest(int item) => Items.IsValid(item) && !CarryLimits.CountsAsBlock(item);

        public static bool AcceptsInStockpile(int item) => Items.IsValid(item) && CarryLimits.CountsAsBlock(item);

        public static float ChestWeight(Inventory contents, ItemWeights weights)
        {
            float weight = 0f;
            foreach (KeyValuePair<int, int> entry in contents.Entries)
            {
                weight += weights.Of(entry.Key) * entry.Value;
            }

            return weight;
        }

        // How many more of an item a chest can take (0 for what it does not hold).
        public static int ChestRoom(Inventory contents, int item, ItemWeights weights)
        {
            if (!AcceptsInChest(item)) return 0;

            float each = weights.Of(item);
            if (each <= 0f) return int.MaxValue;

            // A hair of slack, as for the carrying limit, so that float sums do not refuse what exactly fits.
            float free = ChestMaxWeight - ChestWeight(contents, weights) + 1e-4f;
            return free <= 0f ? 0 : (int)Math.Min(int.MaxValue, Math.Floor(free / each));
        }

        // The quarter turn a player looking along yaw (degrees, 0 toward +z, 90 toward +x) faces.
        public static int FacingOf(float yawDegrees)
        {
            int quarter = (int)Math.Round(yawDegrees / 90.0);
            return ((quarter % 4) + 4) % 4;
        }

        // The cell a chest goes into: on top of the solid block, free of blocks (air, not water).
        public static bool CanPlaceChest(World world, BlockPos cell)
        {
            return world.InBounds(cell.X, cell.Y, cell.Z) && world.GetBlock(cell) == Blocks.Air && world.IsSolid(cell.X, cell.Y - 1, cell.Z);
        }

        // The corner (lowest x and z) of a stockpile of that side placed by a player facing that way
        // at target: target is the middle of the area's near edge (of an even side, the cell left of
        // the middle), the area stretching away from the player.
        public static BlockPos StockpileCorner(BlockPos target, int facing, int side = StockpileSide)
        {
            int half = (side - 1) / 2;
            switch (facing)
            {
                case 1: return new BlockPos(target.X, target.Y, target.Z - half);
                case 2: return new BlockPos(target.X - half, target.Y, target.Z - (side - 1));
                case 3: return new BlockPos(target.X - (side - 1), target.Y, target.Z - half);
                default: return new BlockPos(target.X - half, target.Y, target.Z);
            }
        }

        // A stockpile's cells, from its corner: flat solid ground under each, air in each.
        public static bool CanPlaceStockpile(World world, BlockPos corner, int side = StockpileSide)
        {
            for (int pile = 0; pile < side * side; pile++)
            {
                BlockPos cell = PileCell(corner, pile, side);
                if (!world.InBounds(cell.X, cell.Y, cell.Z) || world.GetBlock(cell) != Blocks.Air) return false;
                if (!world.IsSolid(cell.X, cell.Y - 1, cell.Z) || world.IsWater(cell.X, cell.Y - 1, cell.Z)) return false;
            }

            return true;
        }

        // The cell of a pile (0 to side² - 1) of a stockpile of that side, from its corner.
        public static BlockPos PileCell(BlockPos corner, int pile, int side = StockpileSide) => new BlockPos(corner.X + pile % side, corner.Y, corner.Z + pile / side);
    }
}

using System.Numerics;

namespace Voxel.Core
{
    // A wooden door (Blocks.Door): two cells, one above the other, each a door block whose orientation
    // holds its facing (bits 0-1, the way the player who placed it looked: 0 toward +z, 1 +x, 2 -z,
    // 3 -x, StorageRules.FacingOf), whether it is open (bit 2) and whether it is the upper half (bit 3).
    // Closed, its panel stands across the far edge of its cells, hinged on the placer's left; open, it
    // swings back against that side. Both halves always change together (WorldSync), and it falls with
    // the block under it.
    public static class Doors
    {
        public const int LastOrientation = 15;

        // How thick the panel is, in blocks.
        public const float Thickness = 0.1875f;

        private const int OpenBit = 4;
        private const int UpperBit = 8;

        public static ushort Make(int facing, bool open, bool upper) =>
            Blocks.WithOrientation(Blocks.Door, (facing & 3) | (open ? OpenBit : 0) | (upper ? UpperBit : 0));

        public static int FacingOf(ushort door) => Blocks.OrientationOf(door) & 3;

        public static bool IsOpen(ushort door) => (Blocks.OrientationOf(door) & OpenBit) != 0;

        public static bool IsUpper(ushort door) => (Blocks.OrientationOf(door) & UpperBit) != 0;

        // The same half, opened if it was closed and closed if it was open.
        public static ushort Toggled(ushort door) => Make(FacingOf(door), !IsOpen(door), IsUpper(door));

        // Where its other half is.
        public static BlockPos OtherHalf(BlockPos pos, ushort door) => new BlockPos(pos.X, IsUpper(door) ? pos.Y - 1 : pos.Y + 1, pos.Z);

        // Its lower half's cell.
        public static BlockPos LowerHalf(BlockPos pos, ushort door) => IsUpper(door) ? new BlockPos(pos.X, pos.Y - 1, pos.Z) : pos;

        // Whether a door may stand with its lower half at pos: both cells empty air in the world, on a
        // solid full block that is not a door.
        public static bool CanStand(World world, BlockPos pos)
        {
            if (!world.InBounds(pos.X, pos.Y - 1, pos.Z) || !world.InBounds(pos.X, pos.Y + 1, pos.Z)) return false;

            ushort below = world.GetBlock(pos.X, pos.Y - 1, pos.Z);
            return Blocks.IsSolid(below) && !Blocks.IsDoor(below) && Shapes.IsFullCube(below)
                && Blocks.TypeOf(world.GetBlock(pos)) == Blocks.Air
                && Blocks.TypeOf(world.GetBlock(pos.X, pos.Y + 1, pos.Z)) == Blocks.Air;
        }

        // The door standing on the block at support (to drop when it goes): its lower half's cell.
        public static bool TryFindHeldBy(World world, BlockPos support, out BlockPos lower)
        {
            lower = new BlockPos(support.X, support.Y + 1, support.Z);
            if (!world.InBounds(lower.X, lower.Y, lower.Z)) return false;

            ushort block = world.GetBlock(lower);
            return Blocks.IsDoor(block) && !IsUpper(block);
        }

        // The panel's box within its cell (pos is the cell's corner), the whole height of the cell.
        public static void GetBox(BlockPos pos, ushort door, out Vector3 min, out Vector3 max)
        {
            // Facing +z, closed: across the far edge (z near 1); open: along the hinge side (x near 0).
            float minX = 0f, maxX = 1f, minZ = 1f - Thickness, maxZ = 1f;
            if (IsOpen(door))
            {
                maxX = Thickness;
                minZ = 0f;
            }

            // Turned by the facing about the cell's middle, a quarter turn at a time: (x, z) -> (z, -x).
            for (int turn = 0; turn < FacingOf(door); turn++)
            {
                float x0 = minX - 0.5f, x1 = maxX - 0.5f, z0 = minZ - 0.5f, z1 = maxZ - 0.5f;
                minX = z0 + 0.5f;
                maxX = z1 + 0.5f;
                minZ = -x1 + 0.5f;
                maxZ = -x0 + 0.5f;
            }

            var corner = new Vector3(pos.X, pos.Y, pos.Z);
            min = corner + new Vector3(minX, 0f, minZ);
            max = corner + new Vector3(maxX, 1f, maxZ);
        }
    }
}

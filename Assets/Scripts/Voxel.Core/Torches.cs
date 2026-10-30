using System.Collections.Generic;
using System.Numerics;

namespace Voxel.Core
{
    // Where a torch (Blocks.Torch) holds: standing on the top of the block under it, or hanging on
    // the side of a block beside it, leaning away from it. Its orientation (the block's high bits)
    // says which: Standing, or the side its support is on. A torch whose support is gone falls
    // (WorldSync gives it back to whoever broke the support).
    public static class Torches
    {
        public const int Standing = 0;
        public const int OnEast = 1;   // support at +x
        public const int OnWest = 2;   // support at -x
        public const int OnSouth = 3;  // support at +z
        public const int OnNorth = 4;  // support at -z
        public const int LastOrientation = OnNorth;

        private static readonly BlockPos[] Supports =
        {
            new BlockPos(0, -1, 0),
            new BlockPos(1, 0, 0),
            new BlockPos(-1, 0, 0),
            new BlockPos(0, 0, 1),
            new BlockPos(0, 0, -1)
        };

        // The torch placed against the face of a block whose outward normal is given (the face a
        // player aims at): standing on a top face, hanging on a side face; none under a block (false).
        public static bool TryOrient(BlockPos faceNormal, out ushort torch)
        {
            torch = Blocks.Air;
            for (int orientation = 0; orientation <= LastOrientation; orientation++)
            {
                BlockPos support = Supports[orientation];
                if (support.X == -faceNormal.X && support.Y == -faceNormal.Y && support.Z == -faceNormal.Z)
                {
                    torch = Blocks.WithOrientation(Blocks.Torch, orientation);
                    return true;
                }
            }

            return false;
        }

        // The block a torch at pos holds on to.
        public static BlockPos SupportOf(BlockPos pos, ushort torch)
        {
            int orientation = Blocks.OrientationOf(torch);
            return pos + Supports[orientation <= LastOrientation ? orientation : Standing];
        }

        public static bool HasSupport(World world, BlockPos pos, ushort torch)
        {
            BlockPos support = SupportOf(pos, torch);
            if (!world.InBounds(support.X, support.Y, support.Z)) return false;

            // Not on a door (opening it would leave the torch in the air) nor on a shaped block.
            ushort block = world.GetBlock(support);
            return Blocks.IsSolid(block) && !Blocks.IsDoor(block) && Shapes.IsFullCube(block);
        }

        // The torches held by the block at support (to drop when it goes), added to into.
        public static void FindHeldBy(World world, BlockPos support, List<BlockPos> into)
        {
            for (int orientation = 0; orientation <= LastOrientation; orientation++)
            {
                // A torch holding on this side of it sits on the other side.
                BlockPos pos = support - Supports[orientation];
                if (!world.InBounds(pos.X, pos.Y, pos.Z)) continue;

                ushort block = world.GetBlock(pos);
                if (Blocks.IsTorch(block) && Blocks.OrientationOf(block) == orientation) into.Add(pos);
            }
        }

        // The box a torch is aimed at by, within its cell (pos is the cell's corner): a thin stick
        // standing in the middle, or leaning out from its support.
        public static void GetBox(BlockPos pos, ushort torch, out Vector3 min, out Vector3 max)
        {
            var corner = new Vector3(pos.X, pos.Y, pos.Z);
            int orientation = Blocks.OrientationOf(torch);
            if (orientation == Standing || orientation > LastOrientation)
            {
                min = corner + new Vector3(0.38f, 0f, 0.38f);
                max = corner + new Vector3(0.62f, 0.72f, 0.62f);
                return;
            }

            BlockPos side = Supports[orientation];
            var toward = new Vector3(side.X, 0f, side.Z);

            // From the wall to about the middle of the cell, a stick's width across.
            var centre = new Vector3(0.5f, 0.55f, 0.5f) + toward * 0.25f;
            var half = new Vector3(side.X != 0 ? 0.25f : 0.12f, 0.33f, side.Z != 0 ? 0.25f : 0.12f);
            min = corner + centre - half;
            max = corner + centre + half;
        }
    }
}

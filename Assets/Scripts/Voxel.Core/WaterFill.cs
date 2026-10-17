using System.Collections.Generic;

namespace Voxel.Core
{
    // How water fills a hole, on the server: when a block becomes empty with water touching it from
    // the side or from above, the water pours into it and into the empty space joined to it
    // sideways or below, but never above: a hole fills up to the level of the water next to it, no
    // higher. A space larger than MaxBlocks is open land rather than a hole (a breach in a bank
    // above a valley, say): nothing is filled then, and the water stays where it is.
    public static class WaterFill
    {
        public const int MaxBlocks = 4096;

        private static readonly Queue<BlockPos> Pending = new Queue<BlockPos>();
        private static readonly HashSet<BlockPos> Seen = new HashSet<BlockPos>();

        // Fills result with the blocks water should take, start first and then outwards. Returns
        // false, with result empty, if nothing is to be filled.
        public static bool Find(World world, BlockPos start, List<BlockPos> result)
        {
            result.Clear();
            if (!IsEmpty(world, start) || !TouchesWater(world, start)) return false;

            Pending.Clear();
            Seen.Clear();
            Pending.Enqueue(start);
            Seen.Add(start);

            while (Pending.Count > 0)
            {
                BlockPos pos = Pending.Dequeue();
                result.Add(pos);
                if (result.Count > MaxBlocks)
                {
                    result.Clear();
                    return false;
                }

                Spread(world, new BlockPos(pos.X + 1, pos.Y, pos.Z));
                Spread(world, new BlockPos(pos.X - 1, pos.Y, pos.Z));
                Spread(world, new BlockPos(pos.X, pos.Y, pos.Z + 1));
                Spread(world, new BlockPos(pos.X, pos.Y, pos.Z - 1));
                Spread(world, new BlockPos(pos.X, pos.Y - 1, pos.Z));
            }

            return true;
        }

        private static void Spread(World world, BlockPos pos)
        {
            if (!IsEmpty(world, pos) || !Seen.Add(pos)) return;

            Pending.Enqueue(pos);
        }

        private static bool IsEmpty(World world, BlockPos pos)
        {
            return world.InBounds(pos.X, pos.Y, pos.Z) && Blocks.TypeOf(world.GetBlock(pos)) == Blocks.Air;
        }

        private static bool TouchesWater(World world, BlockPos pos)
        {
            return world.IsWater(pos.X + 1, pos.Y, pos.Z) || world.IsWater(pos.X - 1, pos.Y, pos.Z)
                || world.IsWater(pos.X, pos.Y, pos.Z + 1) || world.IsWater(pos.X, pos.Y, pos.Z - 1)
                || world.IsWater(pos.X, pos.Y + 1, pos.Z);
        }
    }
}

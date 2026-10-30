using System;
using System.Collections.Generic;

namespace Voxel.Core
{
    // A line of cells a hammer places or takes in one go: from the first cell toward the second, along
    // the axis on which they are furthest apart (x first, then z, then y when even), MaxLength cells
    // at most, the first included. The same on the server and the client, which only send both ends.
    public static class BuildLines
    {
        public const int MaxLength = 16;

        public static void Cells(BlockPos start, BlockPos end, List<BlockPos> into)
        {
            into.Clear();
            int dx = end.X - start.X;
            int dy = end.Y - start.Y;
            int dz = end.Z - start.Z;
            int ax = Math.Abs(dx), ay = Math.Abs(dy), az = Math.Abs(dz);

            BlockPos step;
            int length;
            if (ax >= az && ax >= ay)
            {
                step = new BlockPos(Math.Sign(dx), 0, 0);
                length = ax;
            }
            else if (az >= ay)
            {
                step = new BlockPos(0, 0, Math.Sign(dz));
                length = az;
            }
            else
            {
                step = new BlockPos(0, Math.Sign(dy), 0);
                length = ay;
            }

            int count = Math.Min(length + 1, MaxLength);
            BlockPos cell = start;
            for (int i = 0; i < count; i++)
            {
                into.Add(cell);
                cell += step;
            }
        }
    }
}

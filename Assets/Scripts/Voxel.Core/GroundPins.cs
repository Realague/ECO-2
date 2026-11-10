using System;
using System.Collections.Generic;

namespace Voxel.Core
{
    // Ground cells that something not made of blocks stands on (storage: chests, stockpiles, campfires,
    // work tables), which the smoothed ground keeps in blocks (SmoothTerrain.IsPinned), display only:
    // nothing reads them but drawing. A cell may be pinned more than once; it is free again once
    // unpinned as many times. Changed is raised for each cell that becomes pinned or free.
    public sealed class GroundPins
    {
        private readonly Dictionary<BlockPos, int> counts = new Dictionary<BlockPos, int>();

        public event Action<BlockPos> Changed;

        public int Count => counts.Count;

        public bool Contains(int x, int y, int z) => counts.Count > 0 && counts.ContainsKey(new BlockPos(x, y, z));

        public void Pin(BlockPos cell)
        {
            counts.TryGetValue(cell, out int count);
            counts[cell] = count + 1;
            if (count == 0) Changed?.Invoke(cell);
        }

        public void Unpin(BlockPos cell)
        {
            if (!counts.TryGetValue(cell, out int count)) return;

            if (count > 1)
            {
                counts[cell] = count - 1;
                return;
            }

            counts.Remove(cell);
            Changed?.Invoke(cell);
        }
    }
}

using System.Numerics;

namespace Voxel.Core
{
    // The smoothed look of natural ground, display only (collisions, rays, the server and the
    // simulation keep the blocks): a binary Surface Nets. Natural solid blocks (IsSmooth: rocks,
    // soils, sand, snow, ice, mud…, not construction blocks, shapes, doors or torches) are sampled at
    // their centres; every corner of the block grid gets one vertex, the mean of the middles of the
    // edges between the 8 blocks around it where smooth ground meets anything else. A visible face of
    // a smooth block is then drawn as a quad through the vertices of its four corners instead of the
    // cube's corners (ChunkMesher). Flat ground keeps its exact height, a vertical cliff stays
    // vertical, a step of one block becomes a ramp and an edge a rounded chamfer. A vertex depends on
    // the world alone, so neighbouring chunks work out the same ones and never leave a seam.
    //
    // Beyond the world's sides, the nearest column inside is repeated (so the ground does not curl
    // up or down at the edge), below it is solid and above it air.
    public static class SmoothTerrain
    {
        // A corner's material is a block type: the rock, not the ore it may hold. An ore shows on the faces
        // of its own block only (ChunkMesher), so that a single ore block is never lost among the rock around.
        public const int MaterialCount = Blocks.LastType + 1;

        public static bool IsSmooth(ushort block) => Blocks.IsOpaque(block) && !Blocks.IsConstruction(block);

        public static int MaterialOf(ushort block) => Blocks.TypeOf(block);

        // Whether the cell counts as smooth ground.
        public static bool IsSmoothAt(World world, int x, int y, int z)
        {
            if (y >= world.SizeY) return false;
            if (y < 0) return true;
            return IsSmooth(world.GetBlock(Clamp(x, world.SizeX), y, Clamp(z, world.SizeZ)));
        }

        // The block a cell shows as smooth ground (beyond the sides, the nearest column's; bedrock below the world).
        private static ushort BlockAt(World world, int x, int y, int z)
        {
            if (y < 0) return Blocks.Bedrock;
            return world.GetBlock(Clamp(x, world.SizeX), y, Clamp(z, world.SizeZ));
        }

        private static int Clamp(int value, int size) => value < 0 ? 0 : value >= size ? size - 1 : value;

        // The 8 cells around the corner (cx, cy, cz), bit i + 2j + 4k for the cell (cx - 1 + i,
        // cy - 1 + j, cz - 1 + k), set where it is smooth ground.
        public static int CornerMask(World world, int cx, int cy, int cz)
        {
            int mask = 0;
            for (int bit = 0; bit < 8; bit++)
            {
                if (IsSmoothAt(world, cx - 1 + (bit & 1), cy - 1 + ((bit >> 1) & 1), cz - 1 + ((bit >> 2) & 1))) mask |= 1 << bit;
            }

            return mask;
        }

        // The vertex of the corner (cx, cy, cz) in world coordinates, and its normal (the gradient of
        // the 8 cells, from ground toward what is not; zero where it cancels out, as between two cells
        // that only touch by an edge). Where no edge around the corner crosses the surface, the corner itself.
        // At the water's surface (a corner of a water block with no water above it), the vertex stays on
        // the corner itself: water is drawn in blocks, so a bank smoothed there would dip under the
        // water's top and bare its sides, or reach over it.
        // So too beside ground kept in blocks under what players built (IsPinned), which the smoothed
        // ground around then meets edge to edge.
        public static void CornerVertex(World world, int cx, int cy, int cz, out Vector3 position, out Vector3 normal)
        {
            CornerVertex(CornerMask(world, cx, cy, cz), cx, cy, cz, out position, out normal);
            if (TouchesWaterSurface(world, cx, cy, cz) || TouchesPinned(world, cx, cy, cz)) position = new Vector3(cx, cy, cz);
        }

        // What players build on: construction blocks (planks, logs, their shapes, doors) and torches.
        public static bool IsPlayerBlock(ushort block) => Blocks.IsConstruction(block) || Blocks.IsTorch(block);

        // Ground under a player's block (IsPlayerBlock) or under what players placed that is not a block
        // (World.Pins: storage, stations) is not smoothed: it stays a cube, drawn as a block with its own
        // tiles (ChunkMesher), so what stands on it stands on a flat, square top. It still counts as
        // ground for the vertices around it.
        public static bool IsPinned(World world, int x, int y, int z)
        {
            if (!world.InBounds(x, y, z) || !IsSmooth(world.GetBlock(x, y, z))) return false;
            return (y + 1 < world.SizeY && IsPlayerBlock(world.GetBlock(x, y + 1, z))) || world.Pins.Contains(x, y, z);
        }

        public static bool TouchesPinned(World world, int cx, int cy, int cz)
        {
            for (int bit = 0; bit < 8; bit++)
            {
                if (IsPinned(world, cx - 1 + (bit & 1), cy - 1 + ((bit >> 1) & 1), cz - 1 + ((bit >> 2) & 1))) return true;
            }

            return false;
        }

        public static bool TouchesWaterSurface(World world, int cx, int cy, int cz)
        {
            for (int bit = 0; bit < 8; bit++)
            {
                int x = cx - 1 + (bit & 1), y = cy - 1 + ((bit >> 1) & 1), z = cz - 1 + ((bit >> 2) & 1);
                if (world.IsWater(x, y, z) && !world.IsWater(x, y + 1, z)) return true;
            }

            return false;
        }

        // How far the vertices go from the block corners toward the Surface Nets position, 0 to 1: at 1
        // the smoothest, lower values keep steeper slopes and sharper edges (flat ground and vertical
        // cliffs are the same at any value). Display only, set by the client (TerrainSettings) on the
        // main thread while nothing is meshed; 1 unless set.
        public static float Strength = 1f;

        public static void CornerVertex(int mask, int cx, int cy, int cz, out Vector3 position, out Vector3 normal)
        {
            float sumX = 0f, sumY = 0f, sumZ = 0f;
            int count = 0;
            for (int a = 0; a < 2; a++)
            {
                for (int b = 0; b < 2; b++)
                {
                    // Along x: the cells (0, a, b) and (1, a, b).
                    if (Solid(mask, 0, a, b) != Solid(mask, 1, a, b))
                    {
                        sumX += cx;
                        sumY += cy - 0.5f + a;
                        sumZ += cz - 0.5f + b;
                        count++;
                    }

                    // Along y: (a, 0, b) and (a, 1, b).
                    if (Solid(mask, a, 0, b) != Solid(mask, a, 1, b))
                    {
                        sumX += cx - 0.5f + a;
                        sumY += cy;
                        sumZ += cz - 0.5f + b;
                        count++;
                    }

                    // Along z: (a, b, 0) and (a, b, 1).
                    if (Solid(mask, a, b, 0) != Solid(mask, a, b, 1))
                    {
                        sumX += cx - 0.5f + a;
                        sumY += cy - 0.5f + b;
                        sumZ += cz;
                        count++;
                    }
                }
            }

            var corner = new Vector3(cx, cy, cz);
            position = count > 0 ? corner + (new Vector3(sumX / count, sumY / count, sumZ / count) - corner) * Strength : corner;

            float gx = 0f, gy = 0f, gz = 0f;
            for (int bit = 0; bit < 8; bit++)
            {
                float sign = (mask & (1 << bit)) != 0 ? -1f : 1f;
                gx += sign * ((bit & 1) - 0.5f);
                gy += sign * (((bit >> 1) & 1) - 0.5f);
                gz += sign * (((bit >> 2) & 1) - 0.5f);
            }

            var gradient = new Vector3(gx, gy, gz);
            float length = gradient.Length();
            normal = length > 1e-4f ? gradient / length : Vector3.Zero;
        }

        private static bool Solid(int mask, int i, int j, int k) => (mask & (1 << (i + 2 * j + 4 * k))) != 0;

        // The material a corner shows (MaterialOf) among the smooth cells around it: one with something
        // that is not ground above it first (the grass at a cliff's top edge, the rock down its side),
        // the upper layer before the lower, then the one most of them hold, then the lowest. -1 when no
        // cell around it is ground.
        public static int MaterialAt(World world, int cx, int cy, int cz)
        {
            int best = -1, bestScore = -1, bestCount = 0;
            for (int bit = 0; bit < 8; bit++)
            {
                int x = cx - 1 + (bit & 1), y = cy - 1 + ((bit >> 1) & 1), z = cz - 1 + ((bit >> 2) & 1);
                if (!IsSmoothAt(world, x, y, z)) continue;

                int material = MaterialOf(BlockAt(world, x, y, z));
                int score = (IsSmoothAt(world, x, y + 1, z) ? 0 : 2) + ((bit >> 1) & 1);
                int count = 0;
                for (int other = 0; other < 8; other++)
                {
                    int ox = cx - 1 + (other & 1), oy = cy - 1 + ((other >> 1) & 1), oz = cz - 1 + ((other >> 2) & 1);
                    if (IsSmoothAt(world, ox, oy, oz) && MaterialOf(BlockAt(world, ox, oy, oz)) == material) count++;
                }

                bool better = score > bestScore
                    || (score == bestScore && count > bestCount)
                    || (score == bestScore && count == bestCount && material < best);
                if (!better) continue;

                best = material;
                bestScore = score;
                bestCount = count;
            }

            return best;
        }

        // How high the smoothed ground shows over the column (x, z) whose top is groundY (the cell just
        // above its topmost solid block), at (fx, fz) within the column (0 to 1, 0.5 its middle): the
        // four vertices of its top face blended. groundY itself when that block is not smooth ground
        // (a construction block, nothing at all).
        public static float HeightAt(World world, int x, int z, int groundY, float fx, float fz)
        {
            if (groundY <= 0 || !IsSmooth(world.GetBlock(x, groundY - 1, z))) return groundY;

            float h00 = CornerHeight(world, x, groundY, z);
            float h10 = CornerHeight(world, x + 1, groundY, z);
            float h01 = CornerHeight(world, x, groundY, z + 1);
            float h11 = CornerHeight(world, x + 1, groundY, z + 1);
            float near = h00 + (h10 - h00) * fx;
            float far = h01 + (h11 - h01) * fx;
            return near + (far - near) * fz;
        }

        private static float CornerHeight(World world, int cx, int cy, int cz)
        {
            CornerVertex(world, cx, cy, cz, out Vector3 position, out _);
            return position.Y;
        }
    }
}

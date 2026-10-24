using System;
using System.Numerics;

namespace Voxel.Core
{
    public struct VoxelHit
    {
        public BlockPos Block;

        // Face hit, pointing out of the block. Zero if the ray starts inside a block.
        public BlockPos Normal;
        public float Distance;
    }

    public static class VoxelRaycast
    {
        // Block-by-block grid traversal (Amanatides and Woo).
        public static bool Cast(World world, Vector3 origin, Vector3 direction, float maxDistance, out VoxelHit hit)
        {
            return Cast(world, origin, direction, maxDistance, false, out hit);
        }

        // The same; with torches, a torch the ray passes through stops it too, when the ray enters its
        // box (Torches.GetBox) within its cell: what a player strikes.
        public static bool Cast(World world, Vector3 origin, Vector3 direction, float maxDistance, bool torches, out VoxelHit hit)
        {
            hit = default;

            float length = direction.Length();
            if (length < 1e-6f) return false;
            direction /= length;

            int x = (int)MathF.Floor(origin.X);
            int y = (int)MathF.Floor(origin.Y);
            int z = (int)MathF.Floor(origin.Z);

            int stepX = direction.X > 0f ? 1 : -1;
            int stepY = direction.Y > 0f ? 1 : -1;
            int stepZ = direction.Z > 0f ? 1 : -1;

            float deltaX = DistancePerBlock(direction.X);
            float deltaY = DistancePerBlock(direction.Y);
            float deltaZ = DistancePerBlock(direction.Z);

            float nextX = FirstBoundary(origin.X, x, stepX, deltaX);
            float nextY = FirstBoundary(origin.Y, y, stepY, deltaY);
            float nextZ = FirstBoundary(origin.Z, z, stepZ, deltaZ);

            var normal = new BlockPos(0, 0, 0);
            float distance = 0f;

            while (distance <= maxDistance)
            {
                ushort block = world.GetBlock(x, y, z);
                if (Blocks.IsSolid(block))
                {
                    hit = new VoxelHit { Block = new BlockPos(x, y, z), Normal = normal, Distance = distance };
                    return true;
                }

                if (torches && Blocks.IsTorch(block))
                {
                    var cell = new BlockPos(x, y, z);
                    Torches.GetBox(cell, block, out Vector3 min, out Vector3 max);
                    float entry = ArrowFlight.SegmentEntry(origin, origin + direction * maxDistance, min, max);
                    if (entry >= 0f && entry <= maxDistance)
                    {
                        hit = new VoxelHit { Block = cell, Normal = normal, Distance = entry };
                        return true;
                    }
                }

                if (nextX <= nextY && nextX <= nextZ)
                {
                    distance = nextX;
                    nextX += deltaX;
                    x += stepX;
                    normal = new BlockPos(-stepX, 0, 0);
                }
                else if (nextY <= nextZ)
                {
                    distance = nextY;
                    nextY += deltaY;
                    y += stepY;
                    normal = new BlockPos(0, -stepY, 0);
                }
                else
                {
                    distance = nextZ;
                    nextZ += deltaZ;
                    z += stepZ;
                    normal = new BlockPos(0, 0, -stepZ);
                }
            }

            return false;
        }

        private static float DistancePerBlock(float direction) =>
            direction != 0f ? Math.Abs(1f / direction) : float.PositiveInfinity;

        private static float FirstBoundary(float origin, int block, int step, float distancePerBlock)
        {
            if (float.IsPositiveInfinity(distancePerBlock)) return float.PositiveInfinity;
            return (step > 0 ? block + 1 - origin : origin - block) * distancePerBlock;
        }
    }
}

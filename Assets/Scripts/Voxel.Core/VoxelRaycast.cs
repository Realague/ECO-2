using System;
using System.Collections.Generic;
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

        // The same; with thin, a thin block the ray passes through (a torch, a door open or closed,
        // Blocks.IsThin) stops it only where the ray enters its box (Torches.GetBox, Doors.GetBox) within
        // its cell: what a player strikes or opens. Without it a closed door stops it at its cell.
        public static bool Cast(World world, Vector3 origin, Vector3 direction, float maxDistance, bool thin, out VoxelHit hit)
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
                if (thin && Blocks.IsThin(block))
                {
                    var cell = new BlockPos(x, y, z);
                    Vector3 min, max;
                    if (Blocks.IsDoor(block)) Doors.GetBox(cell, block, out min, out max);
                    else Torches.GetBox(cell, block, out min, out max);

                    float entry = ArrowFlight.SegmentEntry(origin, origin + direction * maxDistance, min, max);
                    if (entry >= 0f && entry <= maxDistance)
                    {
                        hit = new VoxelHit { Block = cell, Normal = normal, Distance = entry };
                        return true;
                    }
                }
                else if (Blocks.IsSolid(block) && Shapes.IsShaped(block))
                {
                    // A shaped block stops the ray where it enters one of its boxes, on that box's face.
                    var cell = new BlockPos(x, y, z);
                    if (TryHitShape(world, cell, block, origin, direction, maxDistance, out float entry, out BlockPos face))
                    {
                        hit = new VoxelHit { Block = cell, Normal = face, Distance = entry };
                        return true;
                    }
                }
                else if (Blocks.IsSolid(block))
                {
                    hit = new VoxelHit { Block = new BlockPos(x, y, z), Normal = normal, Distance = distance };
                    return true;
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

        [ThreadStatic] private static List<(Vector3 min, Vector3 max)> shapeBoxes;

        // The nearest of a shaped block's boxes the ray enters within reach, and the face it enters by
        // (none, zero, if it starts inside one).
        private static bool TryHitShape(World world, BlockPos cell, ushort block, Vector3 origin, Vector3 direction, float maxDistance, out float entry, out BlockPos face)
        {
            List<(Vector3 min, Vector3 max)> boxes = shapeBoxes ?? (shapeBoxes = new List<(Vector3, Vector3)>());
            boxes.Clear();
            Shapes.GetBoxes(world, cell, block, boxes);
            entry = float.MaxValue;
            face = new BlockPos(0, 0, 0);
            bool found = false;
            foreach ((Vector3 min, Vector3 max) in boxes)
            {
                if (!RayBox(origin, direction, min, max, out float t, out BlockPos normal) || t > maxDistance || t >= entry) continue;

                entry = t;
                face = normal;
                found = true;
            }

            return found;
        }

        // Where a ray (direction of length 1) enters a box, and through which face: the slab method,
        // keeping the axis whose entry is the latest. False if it misses or the box is behind.
        public static bool RayBox(Vector3 origin, Vector3 direction, Vector3 min, Vector3 max, out float entry, out BlockPos face)
        {
            entry = 0f;
            float exit = float.MaxValue;
            face = new BlockPos(0, 0, 0);
            for (int axis = 0; axis < 3; axis++)
            {
                float o = axis == 0 ? origin.X : axis == 1 ? origin.Y : origin.Z;
                float d = axis == 0 ? direction.X : axis == 1 ? direction.Y : direction.Z;
                float lo = axis == 0 ? min.X : axis == 1 ? min.Y : min.Z;
                float hi = axis == 0 ? max.X : axis == 1 ? max.Y : max.Z;
                if (Math.Abs(d) < 1e-8f)
                {
                    if (o < lo || o > hi) return false;
                    continue;
                }

                float t0 = (lo - o) / d;
                float t1 = (hi - o) / d;
                int sign = -1;
                if (t0 > t1)
                {
                    (t0, t1) = (t1, t0);
                    sign = 1;
                }

                if (t0 > entry)
                {
                    entry = t0;
                    face = axis == 0 ? new BlockPos(sign, 0, 0) : axis == 1 ? new BlockPos(0, sign, 0) : new BlockPos(0, 0, sign);
                }

                exit = Math.Min(exit, t1);
                if (entry > exit) return false;
            }

            return true;
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

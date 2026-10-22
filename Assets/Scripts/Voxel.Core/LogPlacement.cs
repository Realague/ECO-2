using System;
using System.Collections.Generic;
using System.Numerics;

namespace Voxel.Core
{
    // Where the logs of a felled tree come to rest: spread along the line its trunk fell on, each on
    // the top of its column (afloat on water), stopping at a wall more than a block high or at the
    // world's edge, where the rest are piled; and where a log goes when the ground under it changes.
    public static class LogPlacement
    {
        // A log afloat lies this far below the top of the water block.
        public const float FloatDraft = 0.125f;

        // Each log piled on another is this much higher.
        public const float PileStep = 0.3f;

        // The furthest a log is pushed up out of a block placed into it, before it goes on top of
        // its column instead.
        private const int MostPush = 4;

        // The line of a fall is followed this finely, column after column.
        private const float WalkStep = 0.25f;

        // Up to this many radians either way from the fall's direction.
        private const float YawJitter = 0.2f;

        // count logs from start to length along dir (horizontal, unit) from foot (the middle of the
        // foot of the tree, on its ground); each a position and a yaw.
        public static void AlongFall(World world, Vector3 foot, Vector3 dir, float start, float length, int count, List<(Vector3 position, float yaw)> into)
        {
            into.Clear();
            if (count <= 0) return;

            float yaw = MathF.Atan2(dir.X, dir.Z);
            int previousTop = (int)MathF.Floor(foot.Y);

            // Blocked at once, the logs pile at the tree's foot.
            int footX = (int)MathF.Floor(foot.X);
            int footZ = (int)MathF.Floor(foot.Z);
            Vector3 lastGood = new Vector3(foot.X, RestHeight(world, footX, world.GetTopHeight(footX, footZ), footZ), foot.Z);
            bool blocked = false;
            int piled = 0;
            float walked = 0f;
            for (int i = 0; i < count; i++)
            {
                float jitter = YawJitter * (Hash(i) * 2f - 1f);
                if (!blocked)
                {
                    // Every column on the way counts, so that no log lands beyond a wall.
                    float distance = count == 1 ? start : start + (length - start) * i / (count - 1);
                    for (; walked < distance && !blocked; walked = MathF.Min(distance, walked + WalkStep))
                    {
                        blocked = !Passes(world, foot + dir * MathF.Min(distance, walked + WalkStep), ref previousTop);
                    }

                    if (!blocked && Passes(world, foot + dir * distance, ref previousTop))
                    {
                        Vector3 point = foot + dir * distance;
                        int x = (int)MathF.Floor(point.X);
                        int z = (int)MathF.Floor(point.Z);
                        lastGood = new Vector3(point.X, RestHeight(world, x, previousTop, z), point.Z);
                        into.Add((lastGood, yaw + jitter));
                        continue;
                    }

                    blocked = true;
                }

                piled++;
                into.Add((lastGood + new Vector3(0f, PileStep * piled, 0f), yaw + jitter));
            }
        }

        // Whether the fall goes on over this point's column: inside the world and at most a block
        // higher than the last (previousTop then becomes its top).
        private static bool Passes(World world, Vector3 point, ref int previousTop)
        {
            int x = (int)MathF.Floor(point.X);
            int z = (int)MathF.Floor(point.Z);
            if (x < 0 || z < 0 || x >= world.SizeX || z >= world.SizeZ) return false;

            int top = world.GetTopHeight(x, z);
            if (top > previousTop + 1) return false;

            previousTop = top;
            return true;
        }

        // Where a log at position goes once the blocks around it changed: pushed up out of a solid
        // block, or dropped onto the ground or water under it. Returns false if it stays where it is.
        public static bool Settle(World world, Vector3 position, out Vector3 settled)
        {
            int x = (int)MathF.Floor(position.X);
            int z = (int)MathF.Floor(position.Z);
            int y = (int)MathF.Floor(position.Y + 0.01f);
            settled = position;

            if (world.IsSolid(x, y, z))
            {
                int pushed = 0;
                while (world.IsSolid(x, y, z) && pushed < MostPush)
                {
                    y++;
                    pushed++;
                }

                if (world.IsSolid(x, y, z)) y = world.GetTopHeight(x, z);
                settled = new Vector3(position.X, RestHeight(world, x, y, z), position.Z);
                return true;
            }

            int ground = y;
            while (ground > 0 && !world.IsSolid(x, ground - 1, z) && !world.IsWater(x, ground - 1, z)) ground--;
            float rest = RestHeight(world, x, ground, z);
            if (rest >= position.Y - 0.01f) return false;

            settled = new Vector3(position.X, rest, position.Z);
            return true;
        }

        // The height a log rests at in a column whose first free cell is y: on the block below, or
        // afloat if that is water.
        private static float RestHeight(World world, int x, int y, int z)
        {
            return world.IsWater(x, y - 1, z) ? y - FloatDraft : y;
        }

        // A number from 0 to 1 for a log's place in the fall, so the same tree's logs lie the same on
        // every machine and every time.
        private static float Hash(int i)
        {
            uint h = (uint)i * 0x9E3779B1u;
            h ^= h >> 15;
            h *= 0x85EBCA77u;
            h ^= h >> 13;
            return (h & 0xFFFF) / 65535f;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Numerics;

namespace Voxel.Core
{
    // Boxes besides the blocks that stop a moving box (tree trunks): those standing in a range of
    // columns, as (min, max) in world coordinates, each within its own column.
    public interface ICollisionObstacles
    {
        void Collect(int minX, int maxX, int minZ, int maxZ, List<(Vector3 min, Vector3 max)> into);
    }

    public struct MoveResult
    {
        public Vector3 Position;
        public bool Grounded;
        public bool HitCeiling;
        public bool HitWall;
    }

    // Collision of an axis-aligned box against the block grid.
    // The position is the centre of the bottom of the box.
    public static class VoxelCollision
    {
        private const float Skin = 0.001f;
        private const float GroundProbe = 0.005f;

        // Each sub-step stays under one block, so as never to go through a wall.
        private const float MaxStep = 0.4f;

        // Obstacle boxes, gathered for each move along an axis (moves happen on one thread at a time).
        [ThreadStatic] private static List<(Vector3 min, Vector3 max)> obstacleBoxes;

        // extraSolid, when given, names cells that are solid besides the blocks (placed storage);
        // obstacles, boxes thinner than a block that stop the box too (tree trunks).
        public static MoveResult Move(World world, Vector3 position, float halfWidth, float height, Vector3 delta,
            Func<int, int, int, bool> extraSolid = null, ICollisionObstacles obstacles = null)
        {
            var result = new MoveResult { Position = position };

            float longest = Math.Max(Math.Abs(delta.X), Math.Max(Math.Abs(delta.Y), Math.Abs(delta.Z)));
            int steps = Math.Max(1, (int)Math.Ceiling(longest / MaxStep));
            Vector3 step = delta / steps;

            for (int i = 0; i < steps; i++)
            {
                Vector3 before = result.Position;
                MoveY(world, extraSolid, ref result, halfWidth, height, step.Y);
                StopAtObstacles(obstacles, ref result, before, halfWidth, height, 1);
                before = result.Position;
                MoveX(world, extraSolid, ref result, halfWidth, height, step.X);
                StopAtObstacles(obstacles, ref result, before, halfWidth, height, 0);
                before = result.Position;
                MoveZ(world, extraSolid, ref result, halfWidth, height, step.Z);
                StopAtObstacles(obstacles, ref result, before, halfWidth, height, 2);
            }

            result.Grounded = HasGroundBelow(world, extraSolid, result.Position, halfWidth) || StandsOnObstacle(obstacles, result.Position, halfWidth);
            return result;
        }

        // After a move along one axis (0 x, 1 y, 2 z) from before: an obstacle the box has just entered
        // stops it at its face. One the box was already in (a tree grown where it stood) does not
        // hold it, so it can walk out. A step stays under the box's width, so none is passed through.
        private static void StopAtObstacles(ICollisionObstacles obstacles, ref MoveResult result, Vector3 before, float halfWidth, float height, int axis)
        {
            if (obstacles == null) return;

            Vector3 p = result.Position;
            List<(Vector3 min, Vector3 max)> boxes = Gather(obstacles, p, halfWidth);
            foreach ((Vector3 min, Vector3 max) in boxes)
            {
                if (!BoxOverlaps(p, halfWidth, height, min, max) || BoxOverlaps(before, halfWidth, height, min, max)) continue;

                switch (axis)
                {
                    case 0:
                        p.X = p.X > before.X ? Math.Min(p.X, min.X - halfWidth - Skin) : Math.Max(p.X, max.X + halfWidth + Skin);
                        result.HitWall = true;
                        break;
                    case 2:
                        p.Z = p.Z > before.Z ? Math.Min(p.Z, min.Z - halfWidth - Skin) : Math.Max(p.Z, max.Z + halfWidth + Skin);
                        result.HitWall = true;
                        break;
                    default:
                        if (p.Y > before.Y)
                        {
                            p.Y = Math.Min(p.Y, min.Y - height - Skin);
                            result.HitCeiling = true;
                        }
                        else
                        {
                            p.Y = Math.Max(p.Y, max.Y + Skin);
                        }

                        break;
                }
            }

            result.Position = p;
        }

        // Whether the box rests on the top of an obstacle (a stump).
        private static bool StandsOnObstacle(ICollisionObstacles obstacles, Vector3 position, float halfWidth)
        {
            if (obstacles == null) return false;

            foreach ((Vector3 min, Vector3 max) in Gather(obstacles, position, halfWidth))
            {
                bool under = position.X + halfWidth > min.X && position.X - halfWidth < max.X && position.Z + halfWidth > min.Z && position.Z - halfWidth < max.Z;
                if (under && max.Y <= position.Y + Skin * 2f && max.Y >= position.Y - GroundProbe) return true;
            }

            return false;
        }

        private static List<(Vector3 min, Vector3 max)> Gather(ICollisionObstacles obstacles, Vector3 position, float halfWidth)
        {
            List<(Vector3 min, Vector3 max)> boxes = obstacleBoxes ?? (obstacleBoxes = new List<(Vector3, Vector3)>());
            boxes.Clear();
            obstacles.Collect(Floor(position.X - halfWidth), Floor(position.X + halfWidth), Floor(position.Z - halfWidth), Floor(position.Z + halfWidth), boxes);
            return boxes;
        }

        private static bool BoxOverlaps(Vector3 position, float halfWidth, float height, Vector3 min, Vector3 max)
        {
            return position.X + halfWidth > min.X && position.X - halfWidth < max.X
                && position.Y + height > min.Y && position.Y < max.Y
                && position.Z + halfWidth > min.Z && position.Z - halfWidth < max.Z;
        }

        public static bool Overlaps(World world, Vector3 position, float halfWidth, float height)
        {
            return AnySolid(
                world,
                null,
                Floor(position.X - halfWidth), Floor(position.X + halfWidth),
                Floor(position.Y), Floor(position.Y + height),
                Floor(position.Z - halfWidth), Floor(position.Z + halfWidth));
        }

        // Raises the position until the box no longer touches any block: used to put a player back
        // where the world has changed since their last visit.
        public static Vector3 RaiseToFreeSpace(World world, Vector3 position, float halfWidth, float height)
        {
            while (Overlaps(world, position, halfWidth, height))
            {
                position.Y = MathF.Floor(position.Y) + 1f;
            }

            return position;
        }

        public static bool IntersectsBlock(Vector3 position, float halfWidth, float height, BlockPos block)
        {
            return position.X + halfWidth > block.X && position.X - halfWidth < block.X + 1
                && position.Y + height > block.Y && position.Y < block.Y + 1
                && position.Z + halfWidth > block.Z && position.Z - halfWidth < block.Z + 1;
        }

        private static void MoveY(World world, Func<int, int, int, bool> extraSolid, ref MoveResult result, float halfWidth, float height, float delta)
        {
            if (delta == 0f) return;

            Vector3 p = result.Position;
            p.Y += delta;
            int minX = Floor(p.X - halfWidth), maxX = Floor(p.X + halfWidth);
            int minZ = Floor(p.Z - halfWidth), maxZ = Floor(p.Z + halfWidth);

            if (delta > 0f)
            {
                int layer = Floor(p.Y + height);
                if (AnySolid(world, extraSolid, minX, maxX, layer, layer, minZ, maxZ))
                {
                    p.Y = layer - height - Skin;
                    result.HitCeiling = true;
                }
            }
            else
            {
                int layer = Floor(p.Y);
                if (AnySolid(world, extraSolid, minX, maxX, layer, layer, minZ, maxZ))
                {
                    p.Y = layer + 1 + Skin;
                }
            }

            result.Position = p;
        }

        private static void MoveX(World world, Func<int, int, int, bool> extraSolid, ref MoveResult result, float halfWidth, float height, float delta)
        {
            if (delta == 0f) return;

            Vector3 p = result.Position;
            p.X += delta;
            int minY = Floor(p.Y), maxY = Floor(p.Y + height);
            int minZ = Floor(p.Z - halfWidth), maxZ = Floor(p.Z + halfWidth);

            int layer = Floor(delta > 0f ? p.X + halfWidth : p.X - halfWidth);
            if (AnySolid(world, extraSolid, layer, layer, minY, maxY, minZ, maxZ))
            {
                p.X = delta > 0f ? layer - halfWidth - Skin : layer + 1 + halfWidth + Skin;
                result.HitWall = true;
            }

            result.Position = p;
        }

        private static void MoveZ(World world, Func<int, int, int, bool> extraSolid, ref MoveResult result, float halfWidth, float height, float delta)
        {
            if (delta == 0f) return;

            Vector3 p = result.Position;
            p.Z += delta;
            int minX = Floor(p.X - halfWidth), maxX = Floor(p.X + halfWidth);
            int minY = Floor(p.Y), maxY = Floor(p.Y + height);

            int layer = Floor(delta > 0f ? p.Z + halfWidth : p.Z - halfWidth);
            if (AnySolid(world, extraSolid, minX, maxX, minY, maxY, layer, layer))
            {
                p.Z = delta > 0f ? layer - halfWidth - Skin : layer + 1 + halfWidth + Skin;
                result.HitWall = true;
            }

            result.Position = p;
        }

        private static bool HasGroundBelow(World world, Func<int, int, int, bool> extraSolid, Vector3 position, float halfWidth)
        {
            int layer = Floor(position.Y - GroundProbe);
            return AnySolid(
                world,
                extraSolid,
                Floor(position.X - halfWidth), Floor(position.X + halfWidth),
                layer, layer,
                Floor(position.Z - halfWidth), Floor(position.Z + halfWidth));
        }

        private static bool AnySolid(World world, Func<int, int, int, bool> extraSolid, int minX, int maxX, int minY, int maxY, int minZ, int maxZ)
        {
            for (int y = minY; y <= maxY; y++)
            {
                for (int z = minZ; z <= maxZ; z++)
                {
                    for (int x = minX; x <= maxX; x++)
                    {
                        if (world.IsSolid(x, y, z) || (extraSolid != null && extraSolid(x, y, z))) return true;
                    }
                }
            }

            return false;
        }

        private static int Floor(float value) => (int)MathF.Floor(value);
    }
}

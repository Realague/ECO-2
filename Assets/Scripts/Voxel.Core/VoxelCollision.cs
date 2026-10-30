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
    // Full blocks stop it by their cells; shaped blocks (Shapes: walls, slabs, slopes, fences) by
    // their boxes, met like obstacles. A box on the ground that a wall stops climbs StepHeight by
    // itself if there is room (onto a floor, up a slope), never a full block.
    public static class VoxelCollision
    {
        private const float Skin = 0.001f;
        private const float GroundProbe = 0.005f;

        // How high a box on the ground steps up by itself: a slab, a step of a slope.
        public const float StepHeight = 0.5f;

        // Each sub-step stays under one block, so as never to go through a wall.
        private const float MaxStep = 0.4f;

        // Obstacle boxes, gathered for each move along an axis (moves happen on one thread at a time).
        [ThreadStatic] private static List<(Vector3 min, Vector3 max)> obstacleBoxes;

        // extraSolid, when given, names cells that are solid besides the blocks (placed storage);
        // obstacles, boxes thinner than a block that stop the box too (tree trunks).
        public static MoveResult Move(World world, Vector3 position, float halfWidth, float height, Vector3 delta,
            Func<int, int, int, bool> extraSolid = null, ICollisionObstacles obstacles = null)
        {
            bool grounded = HasGroundBelow(world, extraSolid, position, halfWidth) || StandsOnObstacle(world, obstacles, position, halfWidth, height);
            MoveResult result = Sweep(world, position, halfWidth, height, delta, extraSolid, obstacles);

            // Stopped on the ground by something low: from StepHeight up, if the box fits there, it may
            // go further; then it comes back down onto what it climbed.
            var horizontal = new Vector3(delta.X, 0f, delta.Z);
            if (grounded && result.HitWall && horizontal.LengthSquared() > 0f)
            {
                Vector3 raised = position + new Vector3(0f, StepHeight, 0f);
                if (Fits(world, extraSolid, obstacles, raised, halfWidth, height))
                {
                    MoveResult stepped = Sweep(world, raised, halfWidth, height, horizontal, extraSolid, obstacles);
                    MoveResult settled = Sweep(world, stepped.Position, halfWidth, height, new Vector3(0f, -StepHeight, 0f), extraSolid, obstacles);
                    if (Travelled(position, settled.Position) > Travelled(position, result.Position) + Skin)
                    {
                        settled.HitWall = stepped.HitWall;
                        result = settled;
                    }
                }
            }

            result.Grounded = HasGroundBelow(world, extraSolid, result.Position, halfWidth) || StandsOnObstacle(world, obstacles, result.Position, halfWidth, height);
            return result;
        }

        private static float Travelled(Vector3 from, Vector3 to) => new Vector2(to.X - from.X, to.Z - from.Z).LengthSquared();

        private static MoveResult Sweep(World world, Vector3 position, float halfWidth, float height, Vector3 delta,
            Func<int, int, int, bool> extraSolid, ICollisionObstacles obstacles)
        {
            var result = new MoveResult { Position = position };

            float longest = Math.Max(Math.Abs(delta.X), Math.Max(Math.Abs(delta.Y), Math.Abs(delta.Z)));
            int steps = Math.Max(1, (int)Math.Ceiling(longest / MaxStep));
            Vector3 step = delta / steps;

            for (int i = 0; i < steps; i++)
            {
                Vector3 before = result.Position;
                MoveY(world, extraSolid, ref result, halfWidth, height, step.Y);
                StopAtObstacles(world, obstacles, ref result, before, halfWidth, height, 1);
                before = result.Position;
                MoveX(world, extraSolid, ref result, halfWidth, height, step.X);
                StopAtObstacles(world, obstacles, ref result, before, halfWidth, height, 0);
                before = result.Position;
                MoveZ(world, extraSolid, ref result, halfWidth, height, step.Z);
                StopAtObstacles(world, obstacles, ref result, before, halfWidth, height, 2);
            }

            return result;
        }

        // Whether the box at position touches no full block, no extra solid cell and no box.
        private static bool Fits(World world, Func<int, int, int, bool> extraSolid, ICollisionObstacles obstacles, Vector3 position, float halfWidth, float height)
        {
            if (AnySolid(world, extraSolid, Floor(position.X - halfWidth), Floor(position.X + halfWidth), Floor(position.Y), Floor(position.Y + height),
                    Floor(position.Z - halfWidth), Floor(position.Z + halfWidth)))
            {
                return false;
            }

            foreach ((Vector3 min, Vector3 max) in Gather(world, obstacles, position, halfWidth, height))
            {
                if (BoxOverlaps(position, halfWidth, height, min, max)) return false;
            }

            return true;
        }

        // After a move along one axis (0 x, 1 y, 2 z) from before: an obstacle the box has just entered
        // stops it at its face. One the box was already in (a tree grown where it stood) does not
        // hold it, so it can walk out. A step stays under the box's width, so none is passed through.
        private static void StopAtObstacles(World world, ICollisionObstacles obstacles, ref MoveResult result, Vector3 before, float halfWidth, float height, int axis)
        {
            Vector3 p = result.Position;
            List<(Vector3 min, Vector3 max)> boxes = Gather(world, obstacles, p, halfWidth, height);
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

        // Whether the box rests on the top of an obstacle (a stump) or of a shaped block (a floor).
        private static bool StandsOnObstacle(World world, ICollisionObstacles obstacles, Vector3 position, float halfWidth, float height)
        {
            foreach ((Vector3 min, Vector3 max) in Gather(world, obstacles, position, halfWidth, height))
            {
                bool under = position.X + halfWidth > min.X && position.X - halfWidth < max.X && position.Z + halfWidth > min.Z && position.Z - halfWidth < max.Z;
                if (under && max.Y <= position.Y + Skin * 2f && max.Y >= position.Y - GroundProbe) return true;
            }

            return false;
        }

        // The obstacles' boxes in the box's columns, and those of the shaped blocks in the cells it
        // touches, from the layer under its feet (a fence rises past its cell) to its top.
        private static List<(Vector3 min, Vector3 max)> Gather(World world, ICollisionObstacles obstacles, Vector3 position, float halfWidth, float height)
        {
            List<(Vector3 min, Vector3 max)> boxes = obstacleBoxes ?? (obstacleBoxes = new List<(Vector3, Vector3)>());
            boxes.Clear();
            int minX = Floor(position.X - halfWidth), maxX = Floor(position.X + halfWidth);
            int minZ = Floor(position.Z - halfWidth), maxZ = Floor(position.Z + halfWidth);
            obstacles?.Collect(minX, maxX, minZ, maxZ, boxes);
            for (int y = Floor(position.Y - GroundProbe) - 1; y <= Floor(position.Y + height); y++)
            {
                for (int z = minZ; z <= maxZ; z++)
                {
                    for (int x = minX; x <= maxX; x++)
                    {
                        if (!world.InBounds(x, y, z)) continue;

                        ushort block = world.GetBlock(x, y, z);
                        if (Blocks.IsSolid(block) && Shapes.IsShaped(block)) Shapes.GetBoxes(world, new BlockPos(x, y, z), block, boxes, true);
                    }
                }
            }

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

        // Whether the box would touch a block of that shape set at pos: its boxes, for a shaped block
        // (a wall beside a player does not touch them), else its cell.
        public static bool IntersectsBlock(World world, Vector3 position, float halfWidth, float height, BlockPos pos, ushort block)
        {
            if (!Shapes.IsShaped(block)) return IntersectsBlock(position, halfWidth, height, pos);

            var boxes = new List<(Vector3 min, Vector3 max)>();
            Shapes.GetBoxes(world, pos, block, boxes, true);
            foreach ((Vector3 min, Vector3 max) in boxes)
            {
                if (BoxOverlaps(position, halfWidth, height, min, max)) return true;
            }

            return false;
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
                        if (IsSolidCell(world, x, y, z) || (extraSolid != null && extraSolid(x, y, z))) return true;
                    }
                }
            }

            return false;
        }

        // A cell that stops the box whole: a solid full block, or the outside of the world's sides and
        // bottom. A shaped block stops it by its boxes instead (Gather).
        private static bool IsSolidCell(World world, int x, int y, int z)
        {
            if (!world.InBounds(x, y, z)) return world.IsSolid(x, y, z);

            ushort block = world.GetBlock(x, y, z);
            return Blocks.IsSolid(block) && !Shapes.IsShaped(block);
        }

        private static int Floor(float value) => (int)MathF.Floor(value);
    }
}

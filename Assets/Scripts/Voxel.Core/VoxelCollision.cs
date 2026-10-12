using System;
using System.Numerics;

namespace Voxel.Core
{
    public struct MoveResult
    {
        public Vector3 Position;
        public bool Grounded;
        public bool HitCeiling;
        public bool HitWall;
    }

    // Collision d'une boîte alignée sur les axes contre la grille de blocs.
    // La position désigne le centre du bas de la boîte.
    public static class VoxelCollision
    {
        private const float Skin = 0.001f;
        private const float GroundProbe = 0.005f;

        // Chaque sous-pas reste inférieur à un bloc, pour ne jamais traverser un mur.
        private const float MaxStep = 0.4f;

        public static MoveResult Move(World world, Vector3 position, float halfWidth, float height, Vector3 delta)
        {
            var result = new MoveResult { Position = position };

            float longest = Math.Max(Math.Abs(delta.X), Math.Max(Math.Abs(delta.Y), Math.Abs(delta.Z)));
            int steps = Math.Max(1, (int)Math.Ceiling(longest / MaxStep));
            Vector3 step = delta / steps;

            for (int i = 0; i < steps; i++)
            {
                MoveY(world, ref result, halfWidth, height, step.Y);
                MoveX(world, ref result, halfWidth, height, step.X);
                MoveZ(world, ref result, halfWidth, height, step.Z);
            }

            result.Grounded = HasGroundBelow(world, result.Position, halfWidth);
            return result;
        }

        public static bool IntersectsBlock(Vector3 position, float halfWidth, float height, BlockPos block)
        {
            return position.X + halfWidth > block.X && position.X - halfWidth < block.X + 1
                && position.Y + height > block.Y && position.Y < block.Y + 1
                && position.Z + halfWidth > block.Z && position.Z - halfWidth < block.Z + 1;
        }

        private static void MoveY(World world, ref MoveResult result, float halfWidth, float height, float delta)
        {
            if (delta == 0f) return;

            Vector3 p = result.Position;
            p.Y += delta;
            int minX = Floor(p.X - halfWidth), maxX = Floor(p.X + halfWidth);
            int minZ = Floor(p.Z - halfWidth), maxZ = Floor(p.Z + halfWidth);

            if (delta > 0f)
            {
                int layer = Floor(p.Y + height);
                if (AnySolid(world, minX, maxX, layer, layer, minZ, maxZ))
                {
                    p.Y = layer - height - Skin;
                    result.HitCeiling = true;
                }
            }
            else
            {
                int layer = Floor(p.Y);
                if (AnySolid(world, minX, maxX, layer, layer, minZ, maxZ))
                {
                    p.Y = layer + 1 + Skin;
                }
            }

            result.Position = p;
        }

        private static void MoveX(World world, ref MoveResult result, float halfWidth, float height, float delta)
        {
            if (delta == 0f) return;

            Vector3 p = result.Position;
            p.X += delta;
            int minY = Floor(p.Y), maxY = Floor(p.Y + height);
            int minZ = Floor(p.Z - halfWidth), maxZ = Floor(p.Z + halfWidth);

            int layer = Floor(delta > 0f ? p.X + halfWidth : p.X - halfWidth);
            if (AnySolid(world, layer, layer, minY, maxY, minZ, maxZ))
            {
                p.X = delta > 0f ? layer - halfWidth - Skin : layer + 1 + halfWidth + Skin;
                result.HitWall = true;
            }

            result.Position = p;
        }

        private static void MoveZ(World world, ref MoveResult result, float halfWidth, float height, float delta)
        {
            if (delta == 0f) return;

            Vector3 p = result.Position;
            p.Z += delta;
            int minX = Floor(p.X - halfWidth), maxX = Floor(p.X + halfWidth);
            int minY = Floor(p.Y), maxY = Floor(p.Y + height);

            int layer = Floor(delta > 0f ? p.Z + halfWidth : p.Z - halfWidth);
            if (AnySolid(world, minX, maxX, minY, maxY, layer, layer))
            {
                p.Z = delta > 0f ? layer - halfWidth - Skin : layer + 1 + halfWidth + Skin;
                result.HitWall = true;
            }

            result.Position = p;
        }

        private static bool HasGroundBelow(World world, Vector3 position, float halfWidth)
        {
            int layer = Floor(position.Y - GroundProbe);
            return AnySolid(
                world,
                Floor(position.X - halfWidth), Floor(position.X + halfWidth),
                layer, layer,
                Floor(position.Z - halfWidth), Floor(position.Z + halfWidth));
        }

        private static bool AnySolid(World world, int minX, int maxX, int minY, int maxY, int minZ, int maxZ)
        {
            for (int y = minY; y <= maxY; y++)
            {
                for (int z = minZ; z <= maxZ; z++)
                {
                    for (int x = minX; x <= maxX; x++)
                    {
                        if (world.IsSolid(x, y, z)) return true;
                    }
                }
            }

            return false;
        }

        private static int Floor(float value) => (int)MathF.Floor(value);
    }
}

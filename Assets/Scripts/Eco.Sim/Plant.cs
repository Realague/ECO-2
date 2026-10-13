using System;
using System.Numerics;

namespace Eco.Sim
{
    public struct Plant
    {
        // Growth is displayed and sent in stages, not continuously.
        public const int StageCount = 8;

        public int Id;
        public int SpeciesIndex;

        // Air cell occupied by the base of the plant, just above the ground.
        public int X;
        public int Y;
        public int Z;

        public int Age;

        // From 0 (seed) to 1 (adult).
        public float Growth;

        public int Stage => StageOf(Growth);

        public static int StageOf(float growth) => Math.Min(StageCount - 1, (int)(growth * StageCount));
    }

    // Volume occupied by a plant, to aim at it and to check that it is within reach.
    public static class PlantShape
    {
        private const float MinHalfWidth = 0.15f;
        private const float MinHeight = 0.3f;

        public static float Scale(int stage) => (stage + 1) / (float)Plant.StageCount;

        public static void GetBounds(Species species, int x, int y, int z, int stage, out Vector3 min, out Vector3 max)
        {
            float scale = Scale(stage);
            float halfWidth = Math.Max(MinHalfWidth, species.Width * scale * 0.5f);
            float height = Math.Max(MinHeight, species.Height * scale);
            min = new Vector3(x + 0.5f - halfWidth, y, z + 0.5f - halfWidth);
            max = new Vector3(x + 0.5f + halfWidth, y + height, z + 0.5f + halfWidth);
        }

        // Distance along the ray to the box, or a negative value if the ray misses it.
        // The direction must be normalized.
        public static float RayDistance(Vector3 origin, Vector3 direction, Vector3 min, Vector3 max)
        {
            float near = 0f;
            float far = float.PositiveInfinity;
            if (!ClipAxis(origin.X, direction.X, min.X, max.X, ref near, ref far)) return -1f;
            if (!ClipAxis(origin.Y, direction.Y, min.Y, max.Y, ref near, ref far)) return -1f;
            if (!ClipAxis(origin.Z, direction.Z, min.Z, max.Z, ref near, ref far)) return -1f;
            return near;
        }

        public static float DistanceSquared(Vector3 point, Vector3 min, Vector3 max)
        {
            Vector3 offset = point - Vector3.Clamp(point, min, max);
            return offset.LengthSquared();
        }

        private static bool ClipAxis(float origin, float direction, float min, float max, ref float near, ref float far)
        {
            if (Math.Abs(direction) < 1e-8f) return origin >= min && origin <= max;

            float first = (min - origin) / direction;
            float second = (max - origin) / direction;
            if (first > second)
            {
                float swap = first;
                first = second;
                second = swap;
            }

            near = Math.Max(near, first);
            far = Math.Min(far, second);
            return near <= far;
        }
    }
}

using System;
using System.Numerics;

namespace Eco.Sim
{
    public struct Plant
    {
        // La croissance est affichée et transmise par paliers, pas en continu.
        public const int StageCount = 8;

        public int Id;
        public int SpeciesIndex;

        // Case d'air occupée par le pied de la plante, juste au-dessus du sol.
        public int X;
        public int Y;
        public int Z;

        public int Age;

        // De 0 (graine) à 1 (adulte).
        public float Growth;

        public int Stage => StageOf(Growth);

        public static int StageOf(float growth) => Math.Min(StageCount - 1, (int)(growth * StageCount));
    }

    // Volume occupé par une plante, pour la viser et pour vérifier qu'elle est à portée.
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

        // Distance le long du rayon jusqu'à la boîte, ou une valeur négative si le rayon la manque.
        // La direction doit être normalisée.
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

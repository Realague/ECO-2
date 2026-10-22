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

        // Steps lived. Negative for a stump left by a felled tree (Ecosystem.CutToStump): it counts up
        // to 0, when the stump sprouts again; int.MinValue marks a plant removed (Ecosystem.RemovedAge).
        public int Age;

        // From 0 (seed) to 1 (adult).
        public float Growth;

        // How well the climate where it stands suits its species (Species.Suitability), 0 to 1: it
        // grows that fast. Worked out when the plant appears or is loaded, never saved or sent.
        public float Vigour;

        // A crop's ripeness, from 0 to 1, once the plant is adult (Species.Harvest): sent as one of
        // RipeStageCount stages (RipeStage), the harvest growing with the stage.
        public float Fruit;

        // Stages of ripeness: 0, nothing to harvest; 1 (unripe), 2 (ripe) and 3 (fully ripe) give a
        // third, two thirds and all of the crop.
        public const int RipeStageCount = 3;

        public int Stage => StageOf(Growth);

        public int RipeStage => RipeStageOf(Fruit);

        // A stump waiting to sprout again: it keeps its column, but neither feeds, seeds, shades nor
        // is eaten.
        public bool IsStump => Age < 0 && Age != int.MinValue;

        public static int StageOf(float growth) => Math.Min(StageCount - 1, (int)(growth * StageCount));

        public static int RipeStageOf(float fruit) => Math.Min(RipeStageCount, (int)(fruit * RipeStageCount));
    }

    // How much a harvest gives at each stage of ripeness (Plant.RipeStage).
    public static class CropYield
    {
        // A third, two thirds or all of a full harvest, rounded up: a ripe crop always gives something.
        public static int Share(int full, int ripeStage)
        {
            if (full <= 0 || ripeStage <= 0) return 0;

            int stage = Math.Min(ripeStage, Plant.RipeStageCount);
            return (full * stage + Plant.RipeStageCount - 1) / Plant.RipeStageCount;
        }

        // An annual's seeds: none if picked unripe, then a share of the full number, at least one.
        public static int Seeds(int full, int ripeStage) => ripeStage < 2 ? 0 : Math.Max(1, Share(full, ripeStage));
    }

    // Volume occupied by a plant, to aim at it and to check that it is within reach.
    public static class PlantShape
    {
        private const float MinHalfWidth = 0.15f;
        private const float MinHeight = 0.3f;

        // How high a stump stands, whatever the tree.
        public const float StumpHeight = 0.6f;

        public static float Scale(int stage) => (stage + 1) / (float)Plant.StageCount;

        public static void GetBounds(Species species, int x, int y, int z, int stage, out Vector3 min, out Vector3 max)
        {
            GetBounds(species, x, y, z, stage, false, out min, out max);
        }

        public static void GetBounds(Species species, int x, int y, int z, int stage, bool stump, out Vector3 min, out Vector3 max)
        {
            float scale = Scale(stage);
            float halfWidth = Math.Max(MinHalfWidth, species.Width * scale * 0.5f);
            float height = stump ? StumpHeight : Math.Max(MinHeight, species.Height * scale);
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

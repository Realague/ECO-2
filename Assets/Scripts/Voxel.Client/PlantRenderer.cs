using System.Collections.Generic;
using Eco.Sim;
using UnityEngine;
using UnityEngine.Rendering;

namespace Voxel.Client
{
    // What a machine knows about a plant to display it and aim at it.
    internal struct PlantView
    {
        public int SpeciesIndex;
        public int X;
        public int Y;
        public int Z;
        public int Stage;
    }

    // Draws plants in batches (GPU instancing), without creating one object per plant. Plants are
    // grouped by world region: only the regions near the camera are prepared and drawn,
    // which keeps the cost constant whatever the size of the world.
    internal sealed class PlantRenderer
    {
        private const int RegionSize = 32;
        private const int MaxInstancesPerCall = 1023;

        // Margin around a region for foliage that sticks out, and height covered above the lowest ground.
        private const float RegionMargin = 4f;
        private const float RegionHeight = 512f;

        private readonly SpeciesAsset[] species;
        private readonly RenderParams[][] renderParams;
        private readonly Dictionary<long, Region> regions = new Dictionary<long, Region>();

        public PlantRenderer(SpeciesAsset[] species)
        {
            this.species = species;
            renderParams = new RenderParams[species.Length][];
            for (int s = 0; s < species.Length; s++)
            {
                IReadOnlyList<SpeciesAsset.Part> parts = species[s].Parts;
                renderParams[s] = new RenderParams[parts.Count];
                for (int p = 0; p < parts.Count; p++)
                {
                    renderParams[s][p] = new RenderParams(parts[p].material)
                    {
                        shadowCastingMode = ShadowCastingMode.On,
                        receiveShadows = true
                    };
                }
            }
        }

        // Adds a plant or updates its growth stage. A plant never changes place.
        public void Set(int plantId, PlantView plant)
        {
            long key = KeyOf(plant.X / RegionSize, plant.Z / RegionSize);
            if (!regions.TryGetValue(key, out Region region))
            {
                region = new Region(plant.X / RegionSize, plant.Z / RegionSize, species);
                regions.Add(key, region);
            }

            region.Plants[plantId] = plant;
            region.Dirty = true;
        }

        public void Remove(int plantId, PlantView plant)
        {
            if (!regions.TryGetValue(KeyOf(plant.X / RegionSize, plant.Z / RegionSize), out Region region)) return;

            if (region.Plants.Remove(plantId)) region.Dirty = true;
        }

        public void Clear()
        {
            regions.Clear();
        }

        // To call every frame. Plants beyond viewDistance are not drawn.
        public void Render(Vector3 cameraPosition, float viewDistance)
        {
            int minX = Mathf.FloorToInt((cameraPosition.x - viewDistance) / RegionSize);
            int maxX = Mathf.FloorToInt((cameraPosition.x + viewDistance) / RegionSize);
            int minZ = Mathf.FloorToInt((cameraPosition.z - viewDistance) / RegionSize);
            int maxZ = Mathf.FloorToInt((cameraPosition.z + viewDistance) / RegionSize);

            for (int rz = minZ; rz <= maxZ; rz++)
            {
                for (int rx = minX; rx <= maxX; rx++)
                {
                    if (!regions.TryGetValue(KeyOf(rx, rz), out Region region) || region.Plants.Count == 0) continue;
                    if (region.DistanceSquaredTo(cameraPosition) > viewDistance * viewDistance) continue;

                    if (region.Dirty) region.Rebuild(species);
                    Draw(region);
                }
            }
        }

        private void Draw(Region region)
        {
            for (int s = 0; s < species.Length; s++)
            {
                IReadOnlyList<SpeciesAsset.Part> parts = species[s].Parts;
                for (int p = 0; p < parts.Count; p++)
                {
                    // Each batch carries the box of its region: Unity itself culls those out of view.
                    RenderParams parameters = renderParams[s][p];
                    parameters.worldBounds = region.Bounds;

                    for (int start = 0; start < region.Counts[s]; start += MaxInstancesPerCall)
                    {
                        int count = Mathf.Min(MaxInstancesPerCall, region.Counts[s] - start);
                        Graphics.RenderMeshInstanced(parameters, parts[p].mesh, 0, region.Matrices[s][p], count, start);
                    }
                }
            }
        }

        private static long KeyOf(int regionX, int regionZ) => ((long)regionX << 32) | (uint)regionZ;

        private sealed class Region
        {
            public readonly Dictionary<int, PlantView> Plants = new Dictionary<int, PlantView>();

            // By species then by part: the matrices of the region's plants, recomputed when Dirty is true.
            public readonly Matrix4x4[][][] Matrices;
            public readonly int[] Counts;
            public readonly Bounds Bounds;
            public bool Dirty = true;

            private readonly float minX;
            private readonly float minZ;

            public Region(int regionX, int regionZ, SpeciesAsset[] species)
            {
                minX = regionX * RegionSize;
                minZ = regionZ * RegionSize;

                var centre = new Vector3(minX + RegionSize * 0.5f, RegionHeight * 0.5f, minZ + RegionSize * 0.5f);
                Bounds = new Bounds(centre, new Vector3(RegionSize + 2f * RegionMargin, RegionHeight, RegionSize + 2f * RegionMargin));

                Counts = new int[species.Length];
                Matrices = new Matrix4x4[species.Length][][];
                for (int s = 0; s < species.Length; s++)
                {
                    Matrices[s] = new Matrix4x4[species[s].Parts.Count][];
                    for (int p = 0; p < Matrices[s].Length; p++)
                    {
                        Matrices[s][p] = new Matrix4x4[0];
                    }
                }
            }

            // Horizontal distance between a point and the region's rectangle.
            public float DistanceSquaredTo(Vector3 point)
            {
                float dx = Mathf.Max(0f, Mathf.Max(minX - point.x, point.x - (minX + RegionSize)));
                float dz = Mathf.Max(0f, Mathf.Max(minZ - point.z, point.z - (minZ + RegionSize)));
                return dx * dx + dz * dz;
            }

            public void Rebuild(SpeciesAsset[] species)
            {
                for (int s = 0; s < Counts.Length; s++)
                {
                    Counts[s] = 0;
                }

                foreach (PlantView plant in Plants.Values)
                {
                    Counts[plant.SpeciesIndex]++;
                }

                for (int s = 0; s < Counts.Length; s++)
                {
                    for (int p = 0; p < Matrices[s].Length; p++)
                    {
                        if (Matrices[s][p].Length < Counts[s]) Matrices[s][p] = new Matrix4x4[Mathf.NextPowerOfTwo(Counts[s])];
                    }

                    Counts[s] = 0;
                }

                foreach (PlantView plant in Plants.Values)
                {
                    int s = plant.SpeciesIndex;
                    float size = PlantShape.Scale(plant.Stage);
                    var foot = new Vector3(plant.X + 0.5f, plant.Y, plant.Z + 0.5f);
                    IReadOnlyList<SpeciesAsset.Part> parts = species[s].Parts;
                    for (int p = 0; p < parts.Count; p++)
                    {
                        Matrices[s][p][Counts[s]] = Matrix4x4.TRS(foot + parts[p].offset * size, Quaternion.identity, parts[p].scale * size);
                    }

                    Counts[s]++;
                }

                Dirty = false;
            }
        }
    }
}

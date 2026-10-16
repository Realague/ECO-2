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
    //
    // A species may have several models; each plant shows the one its column picks
    // (SpeciesAsset.ModelAt), turned by a quarter turn also picked from its column. Batches are
    // made per look, that is per model of a species, then per part.
    internal sealed class PlantRenderer
    {
        private const int RegionSize = 32;
        private const int MaxInstancesPerCall = 1023;

        // Margin around a region for foliage that sticks out, and height covered above the lowest ground.
        private const float RegionMargin = 4f;
        private const float RegionHeight = 512f;

        // Each plant is turned by a quarter turn chosen from its column, so that neighbours of the
        // same species do not look like copies. Display only: the same on every machine, never sent.
        private static readonly Quaternion[] Turns =
        {
            Quaternion.identity, Quaternion.Euler(0f, 90f, 0f), Quaternion.Euler(0f, 180f, 0f), Quaternion.Euler(0f, 270f, 0f)
        };

        private readonly SpeciesAsset[] species;

        // By species: the index of its first look; its models follow.
        private readonly int[] firstLook;

        // By look: its parts and how to draw each.
        private readonly IReadOnlyList<SpeciesAsset.Part>[] lookParts;
        private readonly RenderParams[][] renderParams;
        private readonly Dictionary<long, Region> regions = new Dictionary<long, Region>();

        public PlantRenderer(SpeciesAsset[] species)
        {
            this.species = species;
            firstLook = new int[species.Length];
            var parts = new List<IReadOnlyList<SpeciesAsset.Part>>();
            for (int s = 0; s < species.Length; s++)
            {
                firstLook[s] = parts.Count;
                for (int m = 0; m < species[s].ModelCount; m++)
                {
                    parts.Add(species[s].PartsOf(m));
                }
            }

            lookParts = parts.ToArray();
            renderParams = new RenderParams[lookParts.Length][];
            for (int look = 0; look < lookParts.Length; look++)
            {
                renderParams[look] = new RenderParams[lookParts[look].Count];
                for (int p = 0; p < lookParts[look].Count; p++)
                {
                    renderParams[look][p] = new RenderParams(lookParts[look][p].material)
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
                region = new Region(plant.X / RegionSize, plant.Z / RegionSize, lookParts);
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

                    if (region.Dirty) region.Rebuild(this);
                    Draw(region);
                }
            }
        }

        private void Draw(Region region)
        {
            for (int look = 0; look < lookParts.Length; look++)
            {
                IReadOnlyList<SpeciesAsset.Part> parts = lookParts[look];
                for (int p = 0; p < parts.Count; p++)
                {
                    // Each batch carries the box of its region: Unity itself culls those out of view.
                    RenderParams parameters = renderParams[look][p];
                    parameters.worldBounds = region.Bounds;

                    for (int start = 0; start < region.Counts[look]; start += MaxInstancesPerCall)
                    {
                        int count = Mathf.Min(MaxInstancesPerCall, region.Counts[look] - start);
                        Graphics.RenderMeshInstanced(parameters, parts[p].mesh, 0, region.Matrices[look][p], count, start);
                    }
                }
            }
        }

        // The look a plant shows, or -1 if its species has no model.
        private int LookOf(PlantView plant)
        {
            SpeciesAsset asset = species[plant.SpeciesIndex];
            return asset.ModelCount == 0 ? -1 : firstLook[plant.SpeciesIndex] + asset.ModelAt(plant.X, plant.Z);
        }

        private static long KeyOf(int regionX, int regionZ) => ((long)regionX << 32) | (uint)regionZ;

        private static Quaternion TurnOf(int x, int z)
        {
            int hash = (x * 73856093) ^ (z * 19349663);
            return Turns[(hash >> 7) & 3];
        }

        private sealed class Region
        {
            public readonly Dictionary<int, PlantView> Plants = new Dictionary<int, PlantView>();

            // By look then by part: the matrices of the region's plants, recomputed when Dirty is true.
            public readonly Matrix4x4[][][] Matrices;
            public readonly int[] Counts;
            public readonly Bounds Bounds;
            public bool Dirty = true;

            private readonly float minX;
            private readonly float minZ;

            public Region(int regionX, int regionZ, IReadOnlyList<SpeciesAsset.Part>[] lookParts)
            {
                minX = regionX * RegionSize;
                minZ = regionZ * RegionSize;

                var centre = new Vector3(minX + RegionSize * 0.5f, RegionHeight * 0.5f, minZ + RegionSize * 0.5f);
                Bounds = new Bounds(centre, new Vector3(RegionSize + 2f * RegionMargin, RegionHeight, RegionSize + 2f * RegionMargin));

                Counts = new int[lookParts.Length];
                Matrices = new Matrix4x4[lookParts.Length][][];
                for (int look = 0; look < lookParts.Length; look++)
                {
                    Matrices[look] = new Matrix4x4[lookParts[look].Count][];
                    for (int p = 0; p < Matrices[look].Length; p++)
                    {
                        Matrices[look][p] = new Matrix4x4[0];
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

            public void Rebuild(PlantRenderer renderer)
            {
                for (int look = 0; look < Counts.Length; look++)
                {
                    Counts[look] = 0;
                }

                foreach (PlantView plant in Plants.Values)
                {
                    int look = renderer.LookOf(plant);
                    if (look >= 0) Counts[look]++;
                }

                for (int look = 0; look < Counts.Length; look++)
                {
                    for (int p = 0; p < Matrices[look].Length; p++)
                    {
                        if (Matrices[look][p].Length < Counts[look]) Matrices[look][p] = new Matrix4x4[Mathf.NextPowerOfTwo(Counts[look])];
                    }

                    Counts[look] = 0;
                }

                foreach (PlantView plant in Plants.Values)
                {
                    int look = renderer.LookOf(plant);
                    if (look < 0) continue;

                    float size = PlantShape.Scale(plant.Stage);
                    var foot = new Vector3(plant.X + 0.5f, plant.Y, plant.Z + 0.5f);
                    Quaternion turn = TurnOf(plant.X, plant.Z);
                    IReadOnlyList<SpeciesAsset.Part> parts = renderer.lookParts[look];
                    for (int p = 0; p < parts.Count; p++)
                    {
                        Matrices[look][p][Counts[look]] = Matrix4x4.TRS(foot + turn * parts[p].offset * size, turn, parts[p].scale * size);
                    }

                    Counts[look]++;
                }

                Dirty = false;
            }
        }
    }
}

using System.Collections.Generic;
using Eco.Sim;
using UnityEngine;
using UnityEngine.Rendering;

namespace Voxel.Client
{
    // Animals near the local player, as the server describes them, and their display. From time to
    // time the server sends the full list of nearby animals: between two lists, each animal
    // glides from its old position to the new one.
    internal sealed class AnimalViews
    {
        private const int MaxInstancesPerCall = 1023;

        private readonly AnimalSpeciesAsset[] assets;
        private readonly IReadOnlyList<AnimalSpecies> species;
        private readonly Dictionary<int, View> views = new Dictionary<int, View>();
        private readonly List<int> gone = new List<int>();
        // By species then by part: the matrices of the displayed animals, recomputed every frame.
        private readonly Matrix4x4[][][] matrices;
        private readonly int[] counts;
        private readonly RenderParams[][] renderParams;
        private int stamp;

        // Animals are not drawn without a display: assets is then null.
        public AnimalViews(AnimalSpeciesAsset[] assets, IReadOnlyList<AnimalSpecies> species, bool rendering)
        {
            this.species = species;
            if (!rendering) return;

            this.assets = assets;
            var everywhere = new Bounds(Vector3.zero, Vector3.one * 100000f);
            matrices = new Matrix4x4[assets.Length][][];
            counts = new int[assets.Length];
            renderParams = new RenderParams[assets.Length][];
            for (int s = 0; s < assets.Length; s++)
            {
                IReadOnlyList<SpeciesAsset.Part> parts = assets[s].Parts;
                matrices[s] = new Matrix4x4[parts.Count][];
                renderParams[s] = new RenderParams[parts.Count];
                for (int p = 0; p < parts.Count; p++)
                {
                    matrices[s][p] = new Matrix4x4[0];
                    renderParams[s][p] = new RenderParams(parts[p].material)
                    {
                        shadowCastingMode = ShadowCastingMode.On,
                        receiveShadows = true,
                        worldBounds = everywhere
                    };
                }
            }
        }

        public int Count => views.Count;

        // Start of a new list: animals that do not appear in it will be removed by EndList.
        public void BeginList()
        {
            stamp++;
        }

        public void Add(int animalId, int speciesIndex, Vector3 position, float travelSeconds)
        {
            if (speciesIndex >= species.Count) return;

            if (views.TryGetValue(animalId, out View view))
            {
                Vector3 current = view.PositionAt(Time.time);
                if (position != view.To)
                {
                    Vector3 direction = position - current;
                    direction.y = 0f;
                    if (direction.sqrMagnitude > 0.001f) view.Yaw = Quaternion.LookRotation(direction).eulerAngles.y;

                    view.From = current;
                    view.To = position;
                    view.Start = Time.time;
                    view.Duration = travelSeconds;
                }
            }
            else
            {
                view = new View { SpeciesIndex = speciesIndex, From = position, To = position, Start = Time.time, Duration = travelSeconds };
            }

            view.Stamp = stamp;
            views[animalId] = view;
        }

        public void EndList()
        {
            gone.Clear();
            foreach (KeyValuePair<int, View> entry in views)
            {
                if (entry.Value.Stamp != stamp) gone.Add(entry.Key);
            }

            foreach (int animalId in gone)
            {
                views.Remove(animalId);
            }
        }

        public void Remove(int animalId)
        {
            views.Remove(animalId);
        }

        public void Clear()
        {
            views.Clear();
        }

        // Finds the nearest animal along the ray, at most maxDistance away. The direction must be normalized.
        public bool TryPick(System.Numerics.Vector3 origin, System.Numerics.Vector3 direction, float maxDistance, out int animalId, out float distance)
        {
            animalId = 0;
            distance = maxDistance;
            bool found = false;

            foreach (KeyValuePair<int, View> entry in views)
            {
                AnimalSpecies kind = species[entry.Value.SpeciesIndex];
                Vector3 position = entry.Value.PositionAt(Time.time);
                float half = kind.Width * 0.5f;
                var min = new System.Numerics.Vector3(position.x - half, position.y, position.z - half);
                var max = new System.Numerics.Vector3(position.x + half, position.y + kind.Height, position.z + half);

                float hit = PlantShape.RayDistance(origin, direction, min, max);
                if (hit >= 0f && hit <= distance)
                {
                    distance = hit;
                    animalId = entry.Key;
                    found = true;
                }
            }

            return found;
        }

        // To call every frame.
        public void Render()
        {
            if (assets == null || views.Count == 0) return;

            for (int s = 0; s < counts.Length; s++)
            {
                counts[s] = 0;
            }

            foreach (View view in views.Values)
            {
                counts[view.SpeciesIndex]++;
            }

            for (int s = 0; s < counts.Length; s++)
            {
                for (int p = 0; p < matrices[s].Length; p++)
                {
                    if (matrices[s][p].Length < counts[s]) matrices[s][p] = new Matrix4x4[Mathf.NextPowerOfTwo(counts[s])];
                }

                counts[s] = 0;
            }

            float now = Time.time;
            foreach (View view in views.Values)
            {
                int s = view.SpeciesIndex;
                Vector3 position = view.PositionAt(now);
                Quaternion rotation = Quaternion.Euler(0f, view.Yaw, 0f);
                IReadOnlyList<SpeciesAsset.Part> parts = assets[s].Parts;
                for (int p = 0; p < parts.Count; p++)
                {
                    matrices[s][p][counts[s]] = Matrix4x4.TRS(position + rotation * parts[p].offset, rotation, parts[p].scale);
                }

                counts[s]++;
            }

            for (int s = 0; s < assets.Length; s++)
            {
                IReadOnlyList<SpeciesAsset.Part> parts = assets[s].Parts;
                for (int p = 0; p < parts.Count; p++)
                {
                    for (int start = 0; start < counts[s]; start += MaxInstancesPerCall)
                    {
                        int count = Mathf.Min(MaxInstancesPerCall, counts[s] - start);
                        Graphics.RenderMeshInstanced(renderParams[s][p], parts[p].mesh, 0, matrices[s][p], count, start);
                    }
                }
            }
        }

        private struct View
        {
            public int SpeciesIndex;
            public Vector3 From;
            public Vector3 To;
            public float Start;
            public float Duration;
            public float Yaw;
            public int Stamp;

            public Vector3 PositionAt(float time)
            {
                float progress = Duration > 0f ? Mathf.Clamp01((time - Start) / Duration) : 1f;
                return Vector3.Lerp(From, To, progress);
            }
        }
    }
}

using System;
using System.Collections.Generic;
using Eco.Sim;
using UnityEngine;
using Voxel.Core;

namespace Voxel.Client
{
    // A plant species described by data: its simulation rules and its appearance.
    // Adding a species means creating one of these files (Eco > Rebuild plant models creates it
    // with its models, from PlantModelBuilder's table) and adding it at the end of EcosystemSync's list.
    [CreateAssetMenu(menuName = "Eco/Plant species", fileName = "Species")]
    public class SpeciesAsset : ScriptableObject
    {
        // Shape displayed at adult size; it is scaled down according to the growth stage.
        [Serializable]
        public struct Part
        {
            public Mesh mesh;
            public Material material;
            public Vector3 offset;
            public Vector3 scale;

            // Animals only, optional: the same shape with smooth normals, which casts the shadow
            // instead of the mesh. A flat-shaded mesh's facets each move along their own normal in
            // the shadow pass (the light's normal bias) and the light leaks between them.
            public Mesh shadowMesh;
        }

        [Header("Simulation (durations in game days)")]
        [SerializeField] private float maturityDays = 3f;
        [SerializeField] private float lifespanDays = 30f;
        [SerializeField] private float seedAttemptsPerDay = 1.5f;
        [SerializeField] private int seedRange = 6;
        [SerializeField] private int spacing = 2;
        [SerializeField, Range(0f, 1f)] private float minFertility = 0.2f;
        [SerializeField] private float fertilityUsePerDay = 0.2f;

        // The blocks a plant can stand on.
        [Flags]
        public enum Ground
        {
            Grass = 1,
            Dirt = 2,
            Sand = 4,
            Snow = 8,
            Clay = 16,
            Gravel = 32
        }

        [Header("Climate and ground")]
        [Tooltip("Warmth it thrives in, from 0 (the coldest) to 1 (the hottest): the climate's temperature.")]
        [SerializeField] private Vector2 idealWarmth = new Vector2(0f, 1f);
        [Tooltip("Wetness it thrives in, from 0 (the driest) to 1 (the wettest): the climate's humidity.")]
        [SerializeField] private Vector2 idealWetness = new Vector2(0f, 1f);
        [Tooltip("How far past those ranges it still grows, less and less well.")]
        [SerializeField] private float climateTolerance = 0.1f;
        [SerializeField] private Ground grounds = Ground.Grass | Ground.Dirt;
        [Tooltip("Only grows at the water's edge.")]
        [SerializeField] private bool needsWater;

        [Header("Harvest")]
        [Tooltip("Wood obtained by felling an adult plant; a younger plant gives less.")]
        [SerializeField] private int woodYield;

        // One look of the species. Each plant shows one of the models, picked from its column.
        [Serializable]
        public struct Model
        {
            public Part[] parts;
        }

        [Header("Appearance and aiming")]
        [Tooltip("Aiming box at adult size: as high as the tallest model, as wide as the trunk.")]
        [SerializeField] private float height = 5.5f;
        [SerializeField] private float width = 1f;
        [SerializeField] private Model[] models = Array.Empty<Model>();

        public int ModelCount => models.Length;

        public IReadOnlyList<Part> PartsOf(int model) => models[model].parts ?? Array.Empty<Part>();

        // The model shown for a plant standing in this column: the same on every machine, and
        // never sent, since it only changes the look.
        public int ModelAt(int x, int z)
        {
            if (models.Length <= 1) return 0;

            uint hash = (uint)(x * 1640531513) ^ (uint)(z * 2654435761u);
            hash ^= hash >> 15;
            hash *= 2246822519u;
            hash ^= hash >> 13;
            return (int)(hash % (uint)models.Length);
        }

        public int WoodYield => woodYield;

        public Species ToSpecies()
        {
            return new Species
            {
                Name = name,
                MaturitySteps = Mathf.Max(1, Mathf.RoundToInt(maturityDays * SimClock.StepsPerDay)),
                LifespanSteps = Mathf.Max(1, Mathf.RoundToInt(lifespanDays * SimClock.StepsPerDay)),
                SeedChance = seedAttemptsPerDay / SimClock.StepsPerDay,
                SeedRange = Mathf.Max(1, seedRange),
                Spacing = Mathf.Max(1, spacing),
                MinFertility = minFertility,
                FertilityUse = fertilityUsePerDay / SimClock.StepsPerDay,
                Height = height,
                Width = width,
                WarmthMin = idealWarmth.x,
                WarmthMax = idealWarmth.y,
                WetnessMin = idealWetness.x,
                WetnessMax = idealWetness.y,
                ClimateTolerance = climateTolerance,
                Grounds = GroundBlocks(),
                NeedsWater = needsWater
            };
        }

        private ushort[] GroundBlocks()
        {
            var blocks = new List<ushort>();
            if ((grounds & Ground.Grass) != 0) blocks.Add(Blocks.Grass);
            if ((grounds & Ground.Dirt) != 0) blocks.Add(Blocks.Dirt);
            if ((grounds & Ground.Sand) != 0) blocks.Add(Blocks.Sand);
            if ((grounds & Ground.Snow) != 0) blocks.Add(Blocks.Snow);
            if ((grounds & Ground.Clay) != 0) blocks.Add(Blocks.Clay);
            if ((grounds & Ground.Gravel) != 0) blocks.Add(Blocks.Gravel);
            return blocks.ToArray();
        }
    }
}

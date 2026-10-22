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

        [Header("Soil (NPK)")]
        [Tooltip("How much it needs nitrogen, from 0 (it does without) to 1. A plant is limited by the nutrient it lacks most and draws on each as much as it needs it.")]
        [SerializeField, Range(0f, 1f)] private float nitrogenNeed = 1f;
        [Tooltip("How much it needs phosphorus, from 0 to 1.")]
        [SerializeField, Range(0f, 1f)] private float phosphorusNeed = 1f;
        [Tooltip("How much it needs potassium, from 0 to 1.")]
        [SerializeField, Range(0f, 1f)] private float potassiumNeed = 1f;
        [Tooltip("Nitrogen an adult gives back to the soil each day: legumes.")]
        [SerializeField] private float nitrogenFixPerDay;

        // The blocks a plant can stand on.
        [Flags]
        public enum Ground
        {
            Grass = 1,
            Dirt = 2,
            Sand = 4,
            Snow = 8,
            Clay = 16,
            Gravel = 32,
            Mud = 64
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
        [Tooltip("Where its base stands: on dry ground, in water one block deep, or either. Grounds are then those of the bed.")]
        [SerializeField] private Rooting rooting = Rooting.Dry;
        [Tooltip("Drawn at the water's surface rather than on the bed (water lilies).")]
        [SerializeField] private bool floatsOnWater;

        [Tooltip("Only sprouts in the shade of a grown plant at least 3 blocks high, within 2 columns (mushrooms).")]
        [SerializeField] private bool needsShade;

        [Header("Harvest")]
        [Tooltip("Wood obtained by felling an adult plant; a younger plant gives less.")]
        [SerializeField] private int woodYield;
        [Tooltip("A crop: none, an annual (pulled up when harvested) or a perennial (picked again and again). " +
                 "Once adult it ripens through 3 stages, giving a third, two thirds and all of its harvest.")]
        [SerializeField] private HarvestKind harvest;
        [Tooltip("What the harvest gives, its item named so: \"Apple\", \"Wheat grain\".")]
        [SerializeField] private string produceName;
        [Tooltip("Crops given by a fully ripe harvest.")]
        [SerializeField] private int produceYield = 3;
        [Tooltip("An annual's seeds given by a fully ripe harvest (none when picked unripe).")]
        [SerializeField] private int seedYield = 2;
        [Tooltip("Straw given by a fully ripe harvest (wheat).")]
        [SerializeField] private int strawYield;
        [Tooltip("Weight of one crop item in kg, for the 30 kg a player can carry besides blocks.")]
        [SerializeField] private float produceWeight = 0.2f;
        [Tooltip("Days for an adult plant to go from unripe to fully ripe, on rich soil in a climate that suits it (and to ripen again once picked).")]
        [SerializeField] private float fruitDays = 3f;

        [Header("Food")]
        [Tooltip("Calories in one crop item; none means the crop cannot be eaten (cotton, coffee).")]
        [SerializeField] private float calories;
        [Tooltip("Nutrients of the crop, in points from 0 to about 15: they make up the balance of the eater's stomach, weighted by calories.")]
        [SerializeField] private float carbs;
        [SerializeField] private float protein;
        [SerializeField] private float fat;
        [SerializeField] private float vitamins;

        // One look of the species. Each plant shows one of the models, picked from its column. A
        // crop's fruit are drawn over it once ripe: fruits[0] at the first stage of ripeness, then
        // the next, each fruit larger (PlantModelBuilder makes them).
        [Serializable]
        public struct Model
        {
            public Part[] parts;
            public FruitLook[] fruits;
        }

        [Serializable]
        public struct FruitLook
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

        // The fruit drawn over a model at a stage of ripeness (1 to Plant.RipeStageCount); none at 0.
        public IReadOnlyList<Part> FruitPartsOf(int model, int ripeStage)
        {
            FruitLook[] fruits = models[model].fruits;
            if (fruits == null || ripeStage < 1 || ripeStage > fruits.Length) return Array.Empty<Part>();

            return fruits[ripeStage - 1].parts ?? Array.Empty<Part>();
        }

        public int WoodYield => woodYield;

        public HarvestKind Harvest => harvest;

        public string ProduceName => string.IsNullOrEmpty(produceName) ? name : produceName;

        public int ProduceYield => produceYield;

        public int SeedYield => seedYield;

        public int StrawYield => strawYield;

        public float ProduceWeight => produceWeight;

        // What eating one crop item gives (nothing for a plant that is not a crop).
        public Nutrition Food => harvest == HarvestKind.None ? default : new Nutrition(calories, carbs, protein, fat, vitamins);

        public bool FloatsOnWater => floatsOnWater;

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
                NeedNitrogen = nitrogenNeed,
                NeedPhosphorus = phosphorusNeed,
                NeedPotassium = potassiumNeed,
                FixesNitrogen = nitrogenFixPerDay / SimClock.StepsPerDay,
                Height = height,
                Width = width,
                WarmthMin = idealWarmth.x,
                WarmthMax = idealWarmth.y,
                WetnessMin = idealWetness.x,
                WetnessMax = idealWetness.y,
                ClimateTolerance = climateTolerance,
                Grounds = GroundBlocks(),
                NeedsWater = needsWater,
                Rooting = rooting,
                NeedsShade = needsShade,
                Harvest = harvest,
                FruitSteps = Mathf.Max(1, Mathf.RoundToInt(fruitDays * SimClock.StepsPerDay))
            };
        }

        private ushort[] GroundBlocks()
        {
            var blocks = new List<ushort>();
            if ((grounds & Ground.Grass) != 0) blocks.Add(Blocks.Grass);
            if ((grounds & Ground.Dirt) != 0)
            {
                // What grows on dirt grows on soil a player has tilled.
                blocks.Add(Blocks.Dirt);
                blocks.Add(Blocks.TilledSoil);
            }

            if ((grounds & Ground.Sand) != 0) blocks.Add(Blocks.Sand);
            if ((grounds & Ground.Snow) != 0) blocks.Add(Blocks.Snow);
            if ((grounds & Ground.Clay) != 0) blocks.Add(Blocks.Clay);
            if ((grounds & Ground.Gravel) != 0) blocks.Add(Blocks.Gravel);
            if ((grounds & Ground.Mud) != 0) blocks.Add(Blocks.Mud);
            return blocks.ToArray();
        }
    }
}

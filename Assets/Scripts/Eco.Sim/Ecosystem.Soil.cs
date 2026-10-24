using System;
using Voxel.Core;

namespace Eco.Sim
{
    // The three nutrients of a soil: nitrogen, phosphorus, potassium.
    public enum Nutrient
    {
        Nitrogen,
        Phosphorus,
        Potassium
    }

    // A soil's nutrients, each from 0 up to its capacity plus what carcasses and legumes add.
    public struct SoilSample
    {
        public float Nitrogen;
        public float Phosphorus;
        public float Potassium;

        public float this[Nutrient nutrient] => nutrient == Nutrient.Nitrogen ? Nitrogen : nutrient == Nutrient.Phosphorus ? Phosphorus : Potassium;
    }

    // The soil (NPK): each fertility cell (CellSize × CellSize columns) holds nitrogen, phosphorus
    // and potassium, each regenerating toward its own capacity at its own pace. The capacities come
    // from the land itself, read from the world at construction (not saved): phosphorus and
    // potassium from the rock under the soil and from the soil itself (sand, snow, clay, mud),
    // nitrogen from the climate (wet, mild land is rich in it) and from marsh mud. Plants draw on
    // each nutrient as their species needs it (Species.NeedNitrogen…), grow by the one they lack
    // most (Available), legumes give nitrogen back (Species.FixesNitrogen), plants dying where they
    // stand and rotting carcasses return nutrients to their cell.
    public sealed partial class Ecosystem
    {
        public const int NutrientCount = 3;

        // Capacities range from MinCapacity to 1, like the single fertility they replace.
        private const float MinCapacity = 0.4f;

        // How fast each nutrient comes back toward its capacity, per step: nitrogen fast (and faster
        // where the climate is rich), potassium less, phosphorus slowly (the rock weathers).
        private const float NitrogenRegenPerStep = 0.005f;
        private const float PhosphorusRegenPerStep = 0.0035f;
        private const float PotassiumRegenPerStep = 0.004f;

        // How far above its capacity a carcass or a legume may raise a nutrient; beyond its capacity
        // it comes back down this much slower than it regenerates.
        private const float MaxEnrichment = 0.6f;
        private const int EnrichmentFadeSlowness = 4;

        // A plant that dies where it stands gives back what it drew in this many days.
        private const float LitterDays = 2f;

        // How deep under the ground the rock of a column is looked for.
        private const int RockDepth = 12;

        // Levels and capacities, NutrientCount per cell (cell * NutrientCount + nutrient).
        private readonly float[] soil;
        private readonly float[] soilCapacity;

        // Per cell: how much faster than its base pace nitrogen comes back (0.5 to 1.5, from the climate).
        private readonly float[] nitrogenPace;

        // Per species, what Available and Feed read: its need of each nutrient, its use and the nitrogen
        // it fixes (SoilFieldsPerSpecies values, in that order), copied once from the species.
        private const int SoilFieldsPerSpecies = 5;
        private readonly float[] speciesSoil;

        // The nutrients of the cell holding this column.
        public SoilSample GetSoil(int x, int z)
        {
            int at = CellIndex(x, z) * NutrientCount;
            return new SoilSample { Nitrogen = soil[at], Phosphorus = soil[at + 1], Potassium = soil[at + 2] };
        }

        // The capacities of the cell holding this column: what its soil comes back to.
        public SoilSample GetSoilCapacity(int x, int z)
        {
            int at = CellIndex(x, z) * NutrientCount;
            return new SoilSample { Nitrogen = soilCapacity[at], Phosphorus = soilCapacity[at + 1], Potassium = soilCapacity[at + 2] };
        }

        // How much a species finds to live on in a cell, the law of the minimum weighted by its needs:
        // each nutrient counts 1 − need × (1 − level), and the scarcest one decides. A nutrient the
        // species does not need never limits it; with every need at 1, it is the lowest level. Above
        // capacity (an enriched soil) it can pass 1, so the plant grows faster.
        private float Available(int cell, int speciesIndex)
        {
            // Doubles, read from a plain array: Mono works float arithmetic out through doubles
            // anyway, and this runs for every plant every step.
            int at = cell * NutrientCount;
            int need = speciesIndex * SoilFieldsPerSpecies;
            double available = 1.0;
            bool limited = false;
            double nitrogen = speciesSoil[need];
            if (nitrogen > 0.0)
            {
                available = 1.0 - nitrogen * (1.0 - soil[at]);
                limited = true;
            }

            double phosphorus = speciesSoil[need + 1];
            if (phosphorus > 0.0)
            {
                double left = 1.0 - phosphorus * (1.0 - soil[at + 1]);
                if (!limited || left < available) available = left;
                limited = true;
            }

            double potassium = speciesSoil[need + 2];
            if (potassium > 0.0)
            {
                double left = 1.0 - potassium * (1.0 - soil[at + 2]);
                if (!limited || left < available) available = left;
            }

            return available > 0.0 ? (float)available : 0f;
        }

        // What a plant draws in a step, each nutrient as its species needs it; a legume then gives
        // nitrogen back.
        private void Feed(int cell, int speciesIndex, float growth)
        {
            int at = cell * NutrientCount;
            int need = speciesIndex * SoilFieldsPerSpecies;
            double use = speciesSoil[need + 3] * growth;
            double nitrogen = soil[at] - use * speciesSoil[need];
            double phosphorus = soil[at + 1] - use * speciesSoil[need + 1];
            double potassium = soil[at + 2] - use * speciesSoil[need + 2];
            soil[at] = nitrogen > 0.0 ? (float)nitrogen : 0f;
            soil[at + 1] = phosphorus > 0.0 ? (float)phosphorus : 0f;
            soil[at + 2] = potassium > 0.0 ? (float)potassium : 0f;

            float fixes = speciesSoil[need + 4];
            if (fixes > 0f) Enrich(at, fixes * growth);
        }

        // A plant dead where it stood (of age, of hunger, grazed to death) rots into its cell and
        // gives back what it drew over LitterDays. A plant felled or harvested is carried away.
        private void ReturnToSoil(Plant plant)
        {
            Species kind = species[plant.SpeciesIndex];
            int at = CellIndex(plant.X, plant.Z) * NutrientCount;
            float litter = kind.SoilUse * plant.Growth * LitterDays * SimClock.StepsPerDay;
            Enrich(at, litter * kind.NeedNitrogen);
            Enrich(at + 1, litter * kind.NeedPhosphorus);
            Enrich(at + 2, litter * kind.NeedPotassium);
        }

        // Adds to one nutrient of a cell, up to MaxEnrichment above its capacity.
        private void Enrich(int index, float amount)
        {
            if (amount <= 0f) return;

            soil[index] = Math.Min(soilCapacity[index] + MaxEnrichment, soil[index] + amount);
        }

        // Each nutrient below its capacity regenerates at its pace; above it (enriched), it comes back
        // down slowly.
        private void RegenerateSoil()
        {
            int cells = soil.Length / NutrientCount;
            for (int cell = 0; cell < cells; cell++)
            {
                int at = cell * NutrientCount;
                Regenerate(at, NitrogenRegenPerStep * nitrogenPace[cell]);
                Regenerate(at + 1, PhosphorusRegenPerStep);
                Regenerate(at + 2, PotassiumRegenPerStep);
            }
        }

        private void Regenerate(int index, float pace)
        {
            float level = soil[index];
            float capacity = soilCapacity[index];
            if (level == capacity) return;

            if (level < capacity)
            {
                level += pace;
                soil[index] = level < capacity ? level : capacity;
            }
            else
            {
                level -= pace / EnrichmentFadeSlowness;
                soil[index] = level > capacity ? level : capacity;
            }
        }

        // Works out each cell's capacities from the land, and starts its soil full. Four columns of
        // each cell are read (the 2 × 2 at the middle of its quarters), which is enough for its rock
        // and soil and costs a quarter of reading every column.
        private void FillSoil()
        {
            Geography geography = world.Geography;
            for (int cz = 0; cz < cellsZ; cz++)
            {
                for (int cx = 0; cx < cellsX; cx++)
                {
                    float n = 0f;
                    float p = 0f;
                    float k = 0f;
                    int samples = 0;
                    for (int sz = 0; sz < 2; sz++)
                    {
                        for (int sx = 0; sx < 2; sx++)
                        {
                            int x = Math.Min(world.SizeX - 1, cx * CellSize + CellSize / 4 + sx * CellSize / 2);
                            int z = Math.Min(world.SizeZ - 1, cz * CellSize + CellSize / 4 + sz * CellSize / 2);
                            SoilSample column = ColumnSoil(x, z, geography);
                            n += column.Nitrogen;
                            p += column.Phosphorus;
                            k += column.Potassium;
                            samples++;
                        }
                    }

                    int cell = cx + cellsX * cz;
                    int at = cell * NutrientCount;
                    soilCapacity[at] = MinCapacity + (1f - MinCapacity) * Clamp01(n / samples);
                    soilCapacity[at + 1] = MinCapacity + (1f - MinCapacity) * Clamp01(p / samples);
                    soilCapacity[at + 2] = MinCapacity + (1f - MinCapacity) * Clamp01(k / samples);
                    soil[at] = soilCapacity[at];
                    soil[at + 1] = soilCapacity[at + 1];
                    soil[at + 2] = soilCapacity[at + 2];

                    int midX = Math.Min(world.SizeX - 1, cx * CellSize + CellSize / 2);
                    int midZ = Math.Min(world.SizeZ - 1, cz * CellSize + CellSize / 2);
                    nitrogenPace[cell] = 0.5f + ClimateRichness(geography.TemperatureAt(midX, midZ), geography.HumidityAt(midX, midZ));
                }
            }
        }

        // The raw richness (0 to 1) of one column's soil in each nutrient: phosphorus and potassium
        // from the rock under it, nitrogen from the climate, then what lies on top changes them.
        private SoilSample ColumnSoil(int x, int z, Geography geography)
        {
            int height = world.GetSurfaceHeight(x, z);
            float warmth = (float)geography.TemperatureAt(x, z) / IntNoise.One;
            float richness = ClimateRichness(geography.TemperatureAt(x, z), geography.HumidityAt(x, z));
            if (height <= 0) return new SoilSample();

            ushort top = Blocks.TypeOf(world.GetBlock(x, height - 1, z));
            ushort rock = Blocks.Granite;
            for (int y = height - 1; y >= Math.Max(0, height - RockDepth); y--)
            {
                ushort type = Blocks.TypeOf(world.GetBlock(x, y, z));
                if (IsRock(type))
                {
                    rock = type;
                    break;
                }
            }

            var sample = new SoilSample { Nitrogen = richness };
            RockMinerals(rock, out sample.Phosphorus, out sample.Potassium);

            switch (top)
            {
                case Blocks.Sand:
                case Blocks.Gravel:
                    // Loose and quickly drained: poor in everything.
                    sample.Nitrogen *= 0.3f;
                    sample.Phosphorus *= 0.4f;
                    sample.Potassium *= 0.4f;
                    break;
                case Blocks.Snow:
                    sample.Nitrogen *= 0.3f;
                    sample.Phosphorus *= 0.6f;
                    sample.Potassium *= 0.6f;
                    break;
                case Blocks.Clay:
                    // Clay holds potassium.
                    sample.Nitrogen *= 0.8f;
                    sample.Potassium = Math.Max(sample.Potassium, 0.95f);
                    break;
                case Blocks.Mud:
                    // Marsh mud is full of rotting matter: rich in nitrogen.
                    sample.Nitrogen = Math.Max(sample.Nitrogen, 1f);
                    sample.Potassium = Math.Max(sample.Potassium, 0.7f);
                    break;
                default:
                    // Bare rock has hardly any soil to hold nitrogen.
                    if (IsRock(top)) sample.Nitrogen *= 0.3f;
                    break;
            }

            // Cold slows the weathering of rock.
            if (warmth < 0.25f)
            {
                float cold = 0.8f + 0.8f * warmth;
                sample.Phosphorus *= cold;
                sample.Potassium *= cold;
            }

            return sample;
        }

        // What a rock gives the soil above it, from 0 to 1: limestone and marble phosphorus, basalt
        // both, granite potassium, sandstone little of either.
        private static void RockMinerals(ushort rock, out float phosphorus, out float potassium)
        {
            switch (rock)
            {
                case Blocks.Limestone:
                case Blocks.Marble:
                    phosphorus = 0.9f;
                    potassium = 0.55f;
                    break;
                case Blocks.Basalt:
                    phosphorus = 0.8f;
                    potassium = 0.85f;
                    break;
                case Blocks.Granite:
                    phosphorus = 0.35f;
                    potassium = 0.9f;
                    break;
                case Blocks.Sandstone:
                    phosphorus = 0.35f;
                    potassium = 0.35f;
                    break;
                case Blocks.Slate:
                    phosphorus = 0.6f;
                    potassium = 0.6f;
                    break;
                default:
                    phosphorus = 0.5f;
                    potassium = 0.5f;
                    break;
            }
        }

        private static float[] SpeciesSoil(Species[] species)
        {
            var values = new float[species.Length * SoilFieldsPerSpecies];
            for (int s = 0; s < species.Length; s++)
            {
                int at = s * SoilFieldsPerSpecies;
                values[at] = species[s].NeedNitrogen;
                values[at + 1] = species[s].NeedPhosphorus;
                values[at + 2] = species[s].NeedPotassium;
                values[at + 3] = species[s].SoilUse;
                values[at + 4] = species[s].FixesNitrogen;
            }

            return values;
        }

        private static bool IsRock(ushort type)
        {
            switch (type)
            {
                case Blocks.Granite:
                case Blocks.Basalt:
                case Blocks.Limestone:
                case Blocks.Marble:
                case Blocks.Sandstone:
                case Blocks.Slate:
                case Blocks.Bedrock:
                    return true;
                default:
                    return type >= Blocks.CoalOre && type <= Blocks.Flint;
            }
        }

        private static float Clamp01(float value) => value < 0f ? 0f : value > 1f ? 1f : value;
    }
}

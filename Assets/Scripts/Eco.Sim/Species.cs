using System;
using Voxel.Core;

namespace Eco.Sim
{
    // Where a plant's base may stand (Species.Rooting). The value is part of the species' data, not saved.
    public enum Rooting
    {
        Dry,
        Flooded,
        Either
    }

    // Rules of a plant species. Durations are in simulation steps.
    public sealed class Species
    {
        private static readonly ushort[] SoilGrounds = { Blocks.Grass, Blocks.Dirt };

        public string Name { get; set; }

        // Steps needed to reach adult size on soil of fertility 1, in a climate that suits it.
        public int MaturitySteps { get; set; }

        public int LifespanSteps { get; set; }

        // Chance, at each step, that an adult plant tries to sow.
        public float SeedChance { get; set; }

        // Maximum distance, in blocks, at which a seed lands.
        public int SeedRange { get; set; }

        // A seed does not sprout within this distance of a plant of the same species (1 = no constraint).
        // In any case, a block column holds a single plant.
        public int Spacing { get; set; }

        // Below this fertility, the seed does not sprout and the plant withers.
        public float MinFertility { get; set; }

        // Fertility drawn at each step by an adult plant.
        public float FertilityUse { get; set; }

        // Adult size, in blocks: used to aim at the plant.
        public float Height { get; set; }
        public float Width { get; set; }

        // The climate the species thrives in: warmth and wetness from 0 to 1, as Geography's
        // temperature and humidity (IntNoise.One is 1). Within both ranges it grows at full speed;
        // beyond them it does less and less well, and not at all ClimateTolerance past them.
        public float WarmthMin { get; set; }
        public float WarmthMax { get; set; } = 1f;
        public float WetnessMin { get; set; }
        public float WetnessMax { get; set; } = 1f;
        public float ClimateTolerance { get; set; } = 0.1f;

        // The blocks it can grow on: grass and dirt unless told otherwise.
        public ushort[] Grounds { get; set; } = SoilGrounds;

        // It only grows at the water's edge: water beside its ground or its base.
        public bool NeedsWater { get; set; }

        // Where its base stands: in the air on dry ground (most plants), in water one block deep
        // (water lilies), or either (swamp cypresses). Grounds are then those of the bed.
        public Rooting Rooting { get; set; } = Rooting.Dry;

        // How well the species does in a climate, from 0 (it cannot live there) to 1: its seeds take
        // that often, and it grows that fast.
        public float Suitability(float warmth, float wetness)
        {
            return Fit(warmth, WarmthMin, WarmthMax) * Fit(wetness, WetnessMin, WetnessMax);
        }

        public bool GrowsOn(ushort block)
        {
            ushort type = Blocks.TypeOf(block);
            foreach (ushort ground in Grounds)
            {
                if (ground == type) return true;
            }

            return false;
        }

        private float Fit(float value, float min, float max)
        {
            float tolerance = Math.Max(ClimateTolerance, 1e-4f);
            if (value < min) return Math.Max(0f, 1f - (min - value) / tolerance);
            if (value > max) return Math.Max(0f, 1f - (value - max) / tolerance);
            return 1f;
        }
    }
}

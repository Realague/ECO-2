using System;

namespace Eco.Sim
{
    // Rules of an animal species. Durations are in simulation steps, distances in blocks.
    // Hunger goes from 0 (full) to 1 (starved to death).
    public sealed class AnimalSpecies
    {
        public string Name { get; set; }

        // What the species eats: indices into the plant species list and into the animal species list.
        public int[] EatsPlants { get; set; } = Array.Empty<int>();
        public int[] EatsAnimals { get; set; } = Array.Empty<int>();

        // A scavenger: it also eats carcasses (Ecosystem.Carcasses).
        public bool EatsCarrion { get; set; }

        // When it eats a plant it keeps a seed, which it lets fall later wherever it has gone (birds
        // spreading berries and grass).
        public bool SpreadsSeeds { get; set; }

        public bool EatsSomething => EatsPlants.Length > 0 || EatsAnimals.Length > 0 || EatsCarrion;

        // Where it lives: on land, in water, on the shore (land and water surface) or wading (land and shallow water).
        public AnimalHabitat Habitat { get; set; }

        // Longest distance covered in one step, and distance at which food is spotted.
        public int MoveRange { get; set; }
        public int SenseRange { get; set; }

        public int LifespanSteps { get; set; }
        public int MaturitySteps { get; set; }

        public float HungerPerStep { get; set; }

        // Hunger removed by one meal.
        public float MealValue { get; set; }

        // Chance, at each step, that a well-fed adult has a young.
        public float BirthChance { get; set; }

        // Territory: an animal does not breed if it already counts MaxNeighbours of its kind within
        // TerritoryRange blocks. This is what caps a population when food is plentiful; without it the
        // population grows until everything is eaten, then collapses. A zero range lifts the limit.
        public int TerritoryRange { get; set; }
        public int MaxNeighbours { get; set; }

        // Chance of catching a prey once in contact with it.
        public float CatchChance { get; set; }

        // Minimum population per 10,000 block columns (a 100 by 100 square). Below it, individuals
        // arrive from the map edges, so the species never disappears for good.
        // At zero, nothing comes in from outside.
        public float MinDensity { get; set; }

        // Size in blocks: used to aim at the animal.
        public float Height { get; set; }
        public float Width { get; set; }

        // Plants it can reach, in blocks: taller ones are out of its reach (a deer browses young trees,
        // not grown ones). The height of a plant is its species' times its growth stage.
        public float ReachHeight { get; set; } = float.MaxValue;

        // It keeps near others of its kind: when it has nothing better to do, it walks to the nearest
        // one in sight rather than wandering off.
        public bool Herd { get; set; }

        // The climate it lives in, as for plants (Species.Suitability): it breeds less where the climate
        // suits it less, wanders toward where it suits it, and newcomers only arrive where it suits it.
        public float WarmthMin { get; set; }
        public float WarmthMax { get; set; } = 1f;
        public float WetnessMin { get; set; }
        public float WetnessMax { get; set; } = 1f;
        public float ClimateTolerance { get; set; } = 0.1f;

        // How well the species does in a climate, from 0 to 1.
        public float Suitability(float warmth, float wetness)
        {
            return Fit(warmth, WarmthMin, WarmthMax) * Fit(wetness, WetnessMin, WetnessMax);
        }

        private float Fit(float value, float min, float max)
        {
            float tolerance = Math.Max(ClimateTolerance, 1e-4f);
            if (value < min) return Math.Max(0f, 1f - (min - value) / tolerance);
            if (value > max) return Math.Max(0f, 1f - (value - max) / tolerance);
            return 1f;
        }
    }

    public struct Animal
    {
        public int Id;
        public int SpeciesIndex;

        // Air cell where the animal stands, just above the ground.
        public int X;
        public int Y;
        public int Z;

        public int Age;
        public float Hunger;

        // The seed of a plant it ate, as that plant's species index plus one, or 0 (AnimalSpecies.SpreadsSeeds).
        public int CarriedSeed;
    }
}

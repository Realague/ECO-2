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
    }
}

namespace Eco.Sim
{
    // Rules of a plant species. Durations are in simulation steps.
    public sealed class Species
    {
        public string Name { get; set; }

        // Steps needed to reach adult size on soil of fertility 1.
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
    }
}

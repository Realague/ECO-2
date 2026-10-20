using System.Collections.Generic;
using System.IO;
using Voxel.Core;

namespace Eco.Sim.Tests
{
    // Flat meadow: stone, one layer of dirt, one layer of grass, then air.
    internal sealed class MeadowGenerator : IChunkGenerator
    {
        // Height of the first air cell above the grass.
        public const int Surface = 8;

        public int Version => 1;

        public static World CreateWorld(int chunksPerSide, int seed = 0)
        {
            var world = new World(chunksPerSide, 1, chunksPerSide);
            world.Generate(new MeadowGenerator(), seed);
            return world;
        }

        public void Prepare(int seed, int sizeX, int sizeZ)
        {
        }

        public void Generate(Chunk chunk, int seed)
        {
            for (int y = 0; y < Chunk.Size; y++)
            {
                ushort block = y >= Surface ? Blocks.Air : y == Surface - 1 ? Blocks.Grass : y == Surface - 2 ? Blocks.Dirt : Blocks.Stone;
                for (int z = 0; z < Chunk.Size; z++)
                {
                    for (int x = 0; x < Chunk.Size; x++)
                    {
                        chunk.Set(x, y, z, block);
                    }
                }
            }
        }
    }

    internal static class TestSpecies
    {
        public static Species Tree() => new Species
        {
            Name = "Tree",
            MaturitySteps = 3 * SimClock.StepsPerDay,
            LifespanSteps = 30 * SimClock.StepsPerDay,
            SeedChance = 0.01f,
            SeedRange = 6,
            Spacing = 2,
            MinFertility = 0.2f,
            FertilityUse = 0.0015f,
            Height = 5.5f,
            Width = 1f
        };

        public static Species Bush() => new Species
        {
            Name = "Bush",
            MaturitySteps = SimClock.StepsPerDay,
            LifespanSteps = 10 * SimClock.StepsPerDay,
            SeedChance = 0.02f,
            SeedRange = 3,
            Spacing = 1,
            MinFertility = 0.5f,
            FertilityUse = 0.0008f,
            Height = 0.9f,
            Width = 0.9f
        };

        public static IReadOnlyList<Species> TreeOnly() => new[] { Tree() };

        public static IReadOnlyList<Species> TreeAndBush() => new[] { Tree(), Bush() };

        // Species for the reference run: chosen to go through every branch of a step (shade, water's
        // edge, flooded roots, annual and perennial crops, a legume, herds, a hunter that also
        // scavenges, a bird that spreads seeds, fish, ducks), quickly, not to make a balanced world.
        public static Species[] ReferencePlants()
        {
            int day = SimClock.StepsPerDay;
            var banks = new[] { Blocks.Grass, Blocks.Dirt, Blocks.Sand, Blocks.Mud };
            return new[]
            {
                new Species { Name = "Tree", MaturitySteps = 2 * day, LifespanSteps = 20 * day, SeedChance = 2f / day, SeedRange = 5, Spacing = 3, MinFertility = 0.3f, FertilityUse = 0.0008f, NeedNitrogen = 0.6f, NeedPhosphorus = 0.8f, NeedPotassium = 0.5f, Height = 5.5f, Width = 1f },
                new Species { Name = "Bush", MaturitySteps = day / 2, LifespanSteps = 8 * day, SeedChance = 3f / day, SeedRange = 3, Spacing = 1, MinFertility = 0.4f, FertilityUse = 0.0005f, Height = 0.9f, Width = 0.9f },
                new Species { Name = "Grass", MaturitySteps = day / 3, LifespanSteps = 5 * day, SeedChance = 4f / day, SeedRange = 2, Spacing = 1, MinFertility = 0.2f, FertilityUse = 0.0003f, NeedPhosphorus = 0.4f, NeedPotassium = 0.4f, Height = 0.6f, Width = 0.8f },
                new Species { Name = "Mushroom", MaturitySteps = day / 2, LifespanSteps = 6 * day, SeedChance = 3f / day, SeedRange = 3, Spacing = 2, MinFertility = 0.2f, FertilityUse = 0.0002f, Height = 0.4f, Width = 0.4f, NeedsShade = true, Harvest = HarvestKind.Annual, FruitSteps = day },
                new Species { Name = "Reeds", MaturitySteps = day / 2, LifespanSteps = 6 * day, SeedChance = 3f / day, SeedRange = 2, Spacing = 1, MinFertility = 0.1f, FertilityUse = 0.0002f, Height = 1.5f, Width = 0.6f, NeedsWater = true, Grounds = banks },
                new Species { Name = "LilyPad", MaturitySteps = day / 2, LifespanSteps = 6 * day, SeedChance = 3f / day, SeedRange = 3, Spacing = 2, MinFertility = 0.1f, FertilityUse = 0.0002f, Height = 0.2f, Width = 0.8f, Rooting = Rooting.Flooded, Grounds = new[] { Blocks.Mud, Blocks.Clay, Blocks.Dirt, Blocks.Sand, Blocks.Gravel } },
                new Species { Name = "Wheat", MaturitySteps = day, LifespanSteps = 6 * day, SeedChance = 2f / day, SeedRange = 4, Spacing = 3, MinFertility = 0.3f, FertilityUse = 0.0006f, NeedPhosphorus = 0.4f, NeedPotassium = 0.4f, Height = 1f, Width = 0.6f, Harvest = HarvestKind.Annual, FruitSteps = day },
                new Species { Name = "Bean", MaturitySteps = day, LifespanSteps = 12 * day, SeedChance = 1f / day, SeedRange = 5, Spacing = 4, MinFertility = 0.3f, FertilityUse = 0.0006f, NeedNitrogen = 0.1f, NeedPhosphorus = 0.7f, NeedPotassium = 0.6f, FixesNitrogen = 0.002f, Height = 3.2f, Width = 0.8f, Harvest = HarvestKind.Perennial, FruitSteps = day },
            };
        }

        public static AnimalSpecies[] ReferenceAnimals()
        {
            int day = SimClock.StepsPerDay;
            return new[]
            {
                new AnimalSpecies { Name = "Grazer", EatsPlants = new[] { 0, 1, 2, 6, 7 }, Herd = true, MoveRange = 3, SenseRange = 6, LifespanSteps = 20 * day, MaturitySteps = day, HungerPerStep = 0.01f, MealValue = 0.4f, BirthChance = 0.03f, TerritoryRange = 6, MaxNeighbours = 6, MinDensity = 10f, ReachHeight = 1.5f, Height = 1f, Width = 0.8f },
                new AnimalSpecies { Name = "Hunter", EatsAnimals = new[] { 0, 4 }, EatsCarrion = true, MoveRange = 4, SenseRange = 10, LifespanSteps = 30 * day, MaturitySteps = 2 * day, HungerPerStep = 0.006f, MealValue = 0.5f, CatchChance = 0.4f, BirthChance = 0.01f, TerritoryRange = 12, MaxNeighbours = 3, MinDensity = 3f, Height = 0.8f, Width = 0.8f },
                new AnimalSpecies { Name = "Bird", EatsPlants = new[] { 1, 2, 6 }, SpreadsSeeds = true, Herd = true, Habitat = AnimalHabitat.Air, MoveRange = 6, SenseRange = 8, LifespanSteps = 15 * day, MaturitySteps = day, HungerPerStep = 0.008f, MealValue = 0.4f, BirthChance = 0.02f, TerritoryRange = 8, MaxNeighbours = 6, MinDensity = 5f, Height = 0.3f, Width = 0.3f },
                new AnimalSpecies { Name = "Fish", Herd = true, Habitat = AnimalHabitat.Water, MoveRange = 3, SenseRange = 4, LifespanSteps = 20 * day, MaturitySteps = day, BirthChance = 0.01f, TerritoryRange = 8, MaxNeighbours = 8, MinDensity = 10f, Height = 0.3f, Width = 0.3f },
                new AnimalSpecies { Name = "Duck", EatsPlants = new[] { 2, 4, 5 }, Habitat = AnimalHabitat.Shore, MoveRange = 3, SenseRange = 6, LifespanSteps = 15 * day, MaturitySteps = day, HungerPerStep = 0.008f, MealValue = 0.4f, BirthChance = 0.02f, TerritoryRange = 8, MaxNeighbours = 4, MinDensity = 5f, Height = 0.5f, Width = 0.5f },
            };
        }
    }

    internal static class OldSaves
    {
        // A save in the current format, turned into format 5, written before crops ripened and
        // before the soil had nutrients: each plant without its ripeness, each cell with a single
        // fertility at its capacity.
        public static byte[] WithoutFruit(byte[] data, World world) => Convert(data, world, 5, 1f);

        // The same in format 6: ripeness kept, each cell a fertility at this share of its capacity.
        public static byte[] WithoutNutrients(byte[] data, World world, float share) => Convert(data, world, 6, share);

        private static byte[] Convert(byte[] data, World world, int format, float share)
        {
            var input = new BinaryReader(new MemoryStream(data));
            var output = new MemoryStream();
            var writer = new BinaryWriter(output);

            writer.Write(input.ReadUInt32());
            input.ReadInt32();
            writer.Write(format);
            int speciesCount = input.ReadInt32();
            writer.Write(speciesCount);
            for (int i = 0; i < speciesCount; i++) writer.Write(input.ReadString());
            for (int i = 0; i < 4; i++) writer.Write(input.ReadInt32());
            writer.Write(input.ReadInt64());
            writer.Write(input.ReadUInt64());
            writer.Write(input.ReadInt32());

            // Format 7 keeps three nutrient levels per cell; before, a fertility and its capacity.
            int cells = (world.SizeX + Ecosystem.CellSize - 1) / Ecosystem.CellSize * ((world.SizeZ + Ecosystem.CellSize - 1) / Ecosystem.CellSize);
            for (int i = 0; i < cells; i++)
            {
                for (int n = 0; n < Ecosystem.NutrientCount; n++) input.ReadSingle();
                writer.Write(share);
                writer.Write(1f);
            }

            int plantCount = input.ReadInt32();
            writer.Write(plantCount);
            for (int i = 0; i < plantCount; i++)
            {
                for (int field = 0; field < 6; field++) writer.Write(input.ReadInt32());
                writer.Write(input.ReadSingle());
                float fruit = input.ReadSingle();
                if (format >= 6) writer.Write(fruit);
            }

            byte[] rest = input.ReadBytes(data.Length);
            writer.Write(rest);
            writer.Flush();
            return output.ToArray();
        }
    }
}

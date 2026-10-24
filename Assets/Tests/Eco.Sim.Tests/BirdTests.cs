using System.IO;
using NUnit.Framework;
using Voxel.Core;

namespace Eco.Sim.Tests
{
    public class BirdTests
    {
        private const int Seed = 9;
        private const int BushIndex = 1;
        private const int BirdIndex = 0;
        private const int Surface = MeadowGenerator.Surface;

        [Test]
        public void Birds_LandOnTopOfAnything()
        {
            Assert.IsTrue(AnimalMoves.GroundFor(AnimalHabitat.Air, 8, false, 8, out int y) && y == 8, "on the ground");
            Assert.IsTrue(AnimalMoves.GroundFor(AnimalHabitat.Air, 8, true, 5, out y) && y == 8, "on the water");
        }

        [Test]
        public void Bird_FliesOverAWallNoLandAnimalCouldCross()
        {
            World world = MeadowGenerator.CreateWorld(2);
            for (int z = 0; z < world.SizeZ; z++)
            {
                for (int y = Surface; y < Surface + 4; y++)
                {
                    world.SetBlock(10, y, z, Blocks.Stone);
                }
            }

            AnimalSpecies bird = Bird();
            bird.MoveRange = 6;
            Ecosystem ecosystem = CreateEcosystem(world, bird);
            ecosystem.TryPlaceAnimal(BirdIndex, 6, 10);

            bool crossed = false;
            for (int i = 0; i < 200 && !crossed; i++)
            {
                ecosystem.Step();
                crossed = ecosystem.Animals.Count > 0 && ecosystem.Animals[0].X > 10;
            }

            Assert.IsTrue(crossed, "a four-block wall does not stop a bird");
        }

        [Test]
        public void SeedEatingBird_SowsThePlantItAteElsewhere()
        {
            World world = MeadowGenerator.CreateWorld(2);
            AnimalSpecies bird = Bird();
            bird.EatsPlants = new[] { BushIndex };
            bird.SpreadsSeeds = true;
            bird.HungerPerStep = 0.1f;
            bird.MealValue = 0.6f;
            bird.MoveRange = 6;
            bird.SenseRange = 10;
            Ecosystem ecosystem = CreateEcosystem(world, bird);
            int[,] planted = { { 12, 12 }, { 12, 20 }, { 20, 12 }, { 20, 20 } };
            for (int i = 0; i < planted.GetLength(0); i++)
            {
                ecosystem.TryPlant(BushIndex, planted[i, 0], planted[i, 1]);
            }

            for (int i = 0; i < 3 * SimClock.StepsPerDay; i++)
            {
                ecosystem.Step();
            }

            ecosystem.TryPlaceAnimal(BirdIndex, 16, 16);
            bool sown = false;
            for (int i = 0; i < 600 && !sown; i++)
            {
                ecosystem.Step();
                foreach (Plant plant in ecosystem.Plants)
                {
                    bool wasPlanted = false;
                    for (int p = 0; p < planted.GetLength(0); p++)
                    {
                        if (plant.X == planted[p, 0] && plant.Z == planted[p, 1]) wasPlanted = true;
                    }

                    if (!wasPlanted) sown = true;
                }
            }

            Assert.IsTrue(sown, "the bushes do not seed themselves here: only the bird can have sown a new one");
        }

        [Test]
        public void WriteThenRead_KeepsTheSeedABirdCarries()
        {
            World world = MeadowGenerator.CreateWorld(2);
            AnimalSpecies bird = Bird();
            bird.EatsPlants = new[] { BushIndex };
            bird.SpreadsSeeds = true;
            bird.HungerPerStep = 0.2f;
            Ecosystem original = CreateEcosystem(world, bird);
            original.TryPlant(BushIndex, 16, 16);
            for (int i = 0; i < 3 * SimClock.StepsPerDay; i++)
            {
                original.Step();
            }

            original.TryPlaceAnimal(BirdIndex, 17, 16);
            int steps = 0;
            while (original.Animals.Count > 0 && original.Animals[0].CarriedSeed == 0 && steps++ < 50)
            {
                original.Step();
            }

            Assert.AreEqual(BushIndex + 1, original.Animals[0].CarriedSeed);
            var stream = new MemoryStream();
            original.Write(stream);
            stream.Position = 0;

            Ecosystem loaded = Ecosystem.Read(stream, world, Plants(), new[] { bird });

            Assert.AreEqual(original.Animals[0].CarriedSeed, loaded.Animals[0].CarriedSeed);
        }

        private static AnimalSpecies Bird() => new AnimalSpecies
        {
            Name = "Bird",
            Habitat = AnimalHabitat.Air,
            MoveRange = 4,
            SenseRange = 6,
            LifespanSteps = 30 * SimClock.StepsPerDay,
            MaturitySteps = SimClock.StepsPerDay,
            Height = 0.3f,
            Width = 0.3f
        };

        // No seeds: a new plant can only come from a bird.
        private static Species[] Plants()
        {
            Species tree = TestSpecies.Tree();
            tree.SeedChance = 0f;
            Species bush = TestSpecies.Bush();
            bush.SeedChance = 0f;
            bush.MinSoil = 0.2f;
            return new[] { tree, bush };
        }

        private static Ecosystem CreateEcosystem(World world, AnimalSpecies bird)
        {
            return new Ecosystem(world, Plants(), new[] { bird }, Seed);
        }
    }
}

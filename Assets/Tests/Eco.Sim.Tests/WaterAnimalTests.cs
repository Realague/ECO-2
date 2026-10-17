using NUnit.Framework;
using Voxel.Core;

namespace Eco.Sim.Tests
{
    public class WaterAnimalTests
    {
        private const int Seed = 5;
        private const int FishIndex = 0;
        private const int DuckIndex = 1;
        private const int HeronIndex = 2;
        private const int Surface = MeadowGenerator.Surface;

        [Test]
        public void GroundFor_GivesEachHabitatItsPlace()
        {
            // A dry column whose ground is at 8; a pond column, water from 5 to 7 (top 8, bed 5); a shallow one (bed 7).
            Assert.IsTrue(AnimalMoves.GroundFor(AnimalHabitat.Land, 8, false, 8, out int y) && y == 8);
            Assert.IsFalse(AnimalMoves.GroundFor(AnimalHabitat.Land, 8, true, 5, out _));

            Assert.IsFalse(AnimalMoves.GroundFor(AnimalHabitat.Water, 8, false, 8, out _));
            Assert.IsTrue(AnimalMoves.GroundFor(AnimalHabitat.Water, 8, true, 5, out y) && y == 7, "just under the surface");

            Assert.IsTrue(AnimalMoves.GroundFor(AnimalHabitat.Shore, 8, false, 8, out y) && y == 8);
            Assert.IsTrue(AnimalMoves.GroundFor(AnimalHabitat.Shore, 8, true, 5, out y) && y == 8, "afloat");

            Assert.IsTrue(AnimalMoves.GroundFor(AnimalHabitat.Wader, 8, true, 7, out y) && y == 7, "on the bed of shallow water");
            Assert.IsFalse(AnimalMoves.GroundFor(AnimalHabitat.Wader, 8, true, 5, out _), "too deep");
        }

        [Test]
        public void Fish_OnlyLiveAndSwimInWater()
        {
            World world = CreatePondWorld();
            Ecosystem ecosystem = CreateEcosystem(world, Fish());

            Assert.IsFalse(ecosystem.TryPlaceAnimal(FishIndex, 2, 2), "not on land");
            Assert.IsTrue(ecosystem.TryPlaceAnimal(FishIndex, 10, 10));
            Assert.IsTrue(ecosystem.TryPlaceAnimal(FishIndex, 13, 12));
            Assert.AreEqual(Surface - 1, ecosystem.Animals[0].Y, "just under the surface");

            Run(ecosystem, 300);

            Assert.That(ecosystem.Animals.Count, Is.GreaterThan(0));
            foreach (Animal fish in ecosystem.Animals)
            {
                Assert.IsTrue(world.IsWater(fish.X, fish.Y, fish.Z), $"Fish at ({fish.X}, {fish.Y}, {fish.Z})");
            }
        }

        [Test]
        public void Fish_DiesWhenItsWaterIsFilledIn()
        {
            World world = CreatePondWorld();
            Ecosystem ecosystem = CreateEcosystem(world, Fish());
            ecosystem.TryPlaceAnimal(FishIndex, 10, 10);

            for (int z = 8; z <= 15; z++)
            {
                for (int x = 8; x <= 17; x++)
                {
                    world.SetBlock(x, Surface - 1, z, Blocks.Dirt);
                    world.SetBlock(x, Surface - 2, z, Blocks.Dirt);
                    world.SetBlock(x, Surface - 3, z, Blocks.Dirt);
                }
            }

            Run(ecosystem, 1);

            Assert.AreEqual(0, ecosystem.Animals.Count);
        }

        [Test]
        public void Duck_FloatsOnWater_AndWalksOnLand()
        {
            World world = CreatePondWorld();
            Ecosystem ecosystem = CreateEcosystem(world, Fish(), Duck());

            Assert.IsTrue(ecosystem.TryPlaceAnimal(DuckIndex, 10, 10));
            Assert.IsTrue(ecosystem.TryPlaceAnimal(DuckIndex, 2, 2));
            Assert.AreEqual(Surface, ecosystem.Animals[0].Y, "on the water's surface");

            Run(ecosystem, 200);

            int onWater = 0;
            int onLand = 0;
            foreach (Animal duck in ecosystem.Animals)
            {
                Assert.AreEqual(Surface, duck.Y);
                if (world.IsWater(duck.X, duck.Y - 1, duck.Z)) onWater++;
                else onLand++;
            }

            Assert.That(onWater + onLand, Is.GreaterThan(0));
        }

        [Test]
        public void Heron_WadesInShallowWater_ButNotInDeepWater()
        {
            World world = CreatePondWorld();
            Ecosystem ecosystem = CreateEcosystem(world, Fish(), Duck(), Heron());

            Assert.IsFalse(ecosystem.TryPlaceAnimal(HeronIndex, 10, 10), "three blocks deep");
            Assert.IsTrue(ecosystem.TryPlaceAnimal(HeronIndex, 16, 10), "one block deep");
            Assert.AreEqual(Surface - 1, ecosystem.Animals[0].Y, "on the bed");
        }

        [Test]
        public void HungryHeron_CatchesAFish()
        {
            World world = CreatePondWorld();
            AnimalSpecies heron = Heron();
            heron.HungerPerStep = 0.2f;
            heron.CatchChance = 1f;
            AnimalSpecies fish = Fish();
            fish.MoveRange = 0;
            Ecosystem ecosystem = CreateEcosystem(world, fish, Duck(), heron);
            ecosystem.TryPlaceAnimal(FishIndex, 15, 10);
            ecosystem.TryPlaceAnimal(HeronIndex, 17, 10);

            Run(ecosystem, 4);

            Assert.AreEqual(0, ecosystem.CountAnimals(FishIndex));
        }

        // The meadow with a pond: 3 blocks deep over x and z from 8 to 15, then a shallow strip, 1
        // block deep, over x 16 and 17.
        private static World CreatePondWorld()
        {
            World world = MeadowGenerator.CreateWorld(2);
            for (int z = 8; z <= 15; z++)
            {
                for (int x = 8; x <= 17; x++)
                {
                    world.SetBlock(x, Surface - 1, z, Blocks.Water);
                    if (x > 15) continue;

                    world.SetBlock(x, Surface - 2, z, Blocks.Water);
                    world.SetBlock(x, Surface - 3, z, Blocks.Water);
                }
            }

            return world;
        }

        private static AnimalSpecies Fish() => new AnimalSpecies
        {
            Name = "Fish",
            Habitat = AnimalHabitat.Water,
            MoveRange = 2,
            SenseRange = 4,
            LifespanSteps = 30 * SimClock.StepsPerDay,
            MaturitySteps = SimClock.StepsPerDay,
            Herd = true,
            Height = 0.6f,
            Width = 0.3f
        };

        private static AnimalSpecies Duck() => new AnimalSpecies
        {
            Name = "Duck",
            Habitat = AnimalHabitat.Shore,
            MoveRange = 3,
            SenseRange = 5,
            LifespanSteps = 30 * SimClock.StepsPerDay,
            MaturitySteps = SimClock.StepsPerDay,
            Height = 0.4f,
            Width = 0.3f
        };

        private static AnimalSpecies Heron() => new AnimalSpecies
        {
            Name = "Heron",
            Habitat = AnimalHabitat.Wader,
            EatsAnimals = new[] { FishIndex },
            MoveRange = 3,
            SenseRange = 6,
            LifespanSteps = 30 * SimClock.StepsPerDay,
            MaturitySteps = SimClock.StepsPerDay,
            MealValue = 0.5f,
            CatchChance = 0.5f,
            Height = 1.1f,
            Width = 0.4f
        };

        private static Ecosystem CreateEcosystem(World world, params AnimalSpecies[] animals)
        {
            Species tree = TestSpecies.Tree();
            tree.SeedChance = 0f;
            Species bush = TestSpecies.Bush();
            bush.SeedChance = 0f;
            return new Ecosystem(world, new[] { tree, bush }, animals, Seed);
        }

        private static void Run(Ecosystem ecosystem, int steps)
        {
            for (int i = 0; i < steps; i++)
            {
                ecosystem.Step();
            }
        }
    }
}

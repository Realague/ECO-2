using NUnit.Framework;
using Voxel.Core;

namespace Eco.Sim.Tests
{
    // Plants whose base stands in water one block deep (Species.Rooting): water lilies, swamp cypresses.
    public class WaterPlantTests
    {
        private const int Seed = 3;
        private const int Ground = MeadowGenerator.Surface - 1;

        private static readonly ushort[] BedGrounds = { Blocks.Mud, Blocks.Clay, Blocks.Dirt, Blocks.Sand };

        private World world;

        [SetUp]
        public void CreatePools()
        {
            world = MeadowGenerator.CreateWorld(2);

            // A pool one block deep on mud at (10, 10), one two blocks deep at (20, 20).
            world.SetBlock(10, Ground, 10, Blocks.Water);
            world.SetBlock(10, Ground - 1, 10, Blocks.Mud);
            world.SetBlock(20, Ground, 20, Blocks.Water);
            world.SetBlock(20, Ground - 1, 20, Blocks.Water);
            world.SetBlock(20, Ground - 2, 20, Blocks.Mud);
        }

        private static Species Lily()
        {
            Species lily = TestSpecies.Bush();
            lily.Name = "Lily";
            lily.MinSoil = 0f;
            lily.Rooting = Rooting.Flooded;
            lily.Grounds = BedGrounds;
            return lily;
        }

        private static Species Cypress()
        {
            Species cypress = TestSpecies.Tree();
            cypress.Name = "Cypress";
            cypress.MinSoil = 0f;
            cypress.Rooting = Rooting.Either;
            cypress.Grounds = new[] { Blocks.Mud, Blocks.Grass, Blocks.Dirt };
            return cypress;
        }

        [Test]
        public void AWaterPlant_TakesInShallowWater_NotInDeepWater_NorOnLand()
        {
            var ecosystem = new Ecosystem(world, new[] { Lily() }, Seed);

            Assert.IsTrue(ecosystem.TryPlant(0, 10, 10), "shallow water");
            Assert.AreEqual(Ground, ecosystem.Plants[0].Y, "its base is the water block");
            Assert.IsFalse(ecosystem.TryPlant(0, 20, 20), "deep water");
            Assert.IsFalse(ecosystem.TryPlant(0, 30, 30), "dry land");
            Assert.IsFalse(ecosystem.TryPlaceGrownPlant(0, 30, 30), "not even by command");
        }

        [Test]
        public void ALandPlant_NeverTakesInWater_ButOneThatGrowsEitherWayDoes()
        {
            var ecosystem = new Ecosystem(world, new[] { TestSpecies.Tree(), Cypress() }, Seed);

            Assert.IsFalse(ecosystem.TryPlaceGrownPlant(0, 10, 10), "a tree in water");
            Assert.IsTrue(ecosystem.TryPlaceGrownPlant(1, 10, 10), "a cypress in water");
            Assert.IsTrue(ecosystem.TryPlaceGrownPlant(1, 30, 30), "a cypress on land");
            Assert.AreEqual(Ground + 1, ecosystem.Plants[1].Y, "on land, above the grass");
        }

        [Test]
        public void AWaterPlant_Dies_WhenItsPoolIsFilledIn()
        {
            var ecosystem = new Ecosystem(world, new[] { Lily() }, Seed);
            Assert.IsTrue(ecosystem.TryPlaceGrownPlant(0, 10, 10));

            world.SetBlock(10, Ground, 10, Blocks.Dirt);
            ecosystem.Step();

            Assert.AreEqual(0, ecosystem.Plants.Count);
        }

        [Test]
        public void AWaterPlant_Dies_WhenABlockCoversItsWater()
        {
            var ecosystem = new Ecosystem(world, new[] { Lily() }, Seed);
            Assert.IsTrue(ecosystem.TryPlaceGrownPlant(0, 10, 10));

            world.SetBlock(10, Ground + 1, 10, Blocks.Stone);
            ecosystem.Step();

            Assert.AreEqual(0, ecosystem.Plants.Count);
        }

        [Test]
        public void AWaterPlant_Spreads_OnlyOverShallowWater()
        {
            // A wide pool, one block deep, in the middle of a meadow.
            for (int z = 4; z < 28; z++)
            {
                for (int x = 4; x < 28; x++)
                {
                    world.SetBlock(x, Ground, z, Blocks.Water);
                    world.SetBlock(x, Ground - 1, z, Blocks.Mud);
                }
            }

            Species lily = Lily();
            lily.SeedChance = 0.2f;
            var ecosystem = new Ecosystem(world, new[] { lily }, Seed);
            Assert.IsTrue(ecosystem.TryPlaceGrownPlant(0, 16, 16));
            for (int step = 0; step < 300; step++)
            {
                ecosystem.Step();
            }

            Assert.That(ecosystem.Plants.Count, Is.GreaterThan(10));
            foreach (Plant plant in ecosystem.Plants)
            {
                Assert.IsTrue(world.IsWater(plant.X, plant.Y, plant.Z), $"Lily at ({plant.X}, {plant.Z})");
            }
        }
    }
}

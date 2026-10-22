using System.IO;
using NUnit.Framework;
using Voxel.Core;

namespace Eco.Sim.Tests
{
    // A felled tree's stump (Ecosystem.CutToStump): it keeps its column and size, does nothing but wait
    // (no feeding, seeding, shade, harvest, nor count in the census), then sprouts a sapling of its tree.
    public class StumpTests
    {
        private const int Seed = 5;
        private const int X = 10;
        private const int Z = 10;

        private static Species SeedingTree()
        {
            Species tree = TestSpecies.Tree();
            tree.SeedChance = 1f;
            return tree;
        }

        private static Ecosystem WithGrownTree(Species tree, out World world, out int plantId)
        {
            world = MeadowGenerator.CreateWorld(2);
            var ecosystem = new Ecosystem(world, new[] { tree }, Seed);
            Assert.IsTrue(ecosystem.TryPlaceGrownPlant(0, X, Z));
            plantId = ecosystem.Plants[0].Id;
            return ecosystem;
        }

        private static Plant Get(Ecosystem ecosystem, int plantId)
        {
            Assert.IsTrue(ecosystem.TryGet(plantId, out Plant plant));
            return plant;
        }

        [Test]
        public void CutToStump_KeepsThePlant_ItsColumnAndSize()
        {
            Ecosystem ecosystem = WithGrownTree(TestSpecies.Tree(), out _, out int plantId);
            Plant before = Get(ecosystem, plantId);
            int changes = 0;
            ecosystem.PlantChanged += plant => changes++;

            Assert.IsTrue(ecosystem.CutToStump(plantId));
            Assert.IsFalse(ecosystem.CutToStump(plantId), "a stump is not felled again");

            Plant stump = Get(ecosystem, plantId);
            Assert.IsTrue(stump.IsStump);
            Assert.AreEqual(before.SpeciesIndex, stump.SpeciesIndex);
            Assert.AreEqual(before.Stage, stump.Stage);
            Assert.AreEqual(1, changes);
        }

        [Test]
        public void AStump_NeitherSeedsNorFeeds()
        {
            Ecosystem ecosystem = WithGrownTree(SeedingTree(), out World world, out int plantId);
            ecosystem.CutToStump(plantId);
            var bare = new Ecosystem(world, new[] { SeedingTree() }, Seed);

            for (int step = 0; step < SimClock.StepsPerDay; step++)
            {
                ecosystem.Step();
                bare.Step();
            }

            Assert.AreEqual(1, ecosystem.Plants.Count, "no seed sown");
            SoilSample withStump = ecosystem.GetSoil(X, Z);
            SoilSample without = bare.GetSoil(X, Z);
            Assert.AreEqual(without.Nitrogen, withStump.Nitrogen, 1e-6f, "the soil as if nothing stood there");
            Assert.AreEqual(without.Potassium, withStump.Potassium, 1e-6f);
        }

        [Test]
        public void AStump_SproutsASapling_AfterItsTime()
        {
            Ecosystem ecosystem = WithGrownTree(TestSpecies.Tree(), out _, out int plantId);
            ecosystem.CutToStump(plantId);

            for (int step = 0; step < Ecosystem.StumpRegrowSteps - 1; step++)
            {
                ecosystem.Step();
            }

            Assert.IsTrue(Get(ecosystem, plantId).IsStump, "still waiting");

            ecosystem.Step();
            Plant sapling = Get(ecosystem, plantId);
            Assert.IsFalse(sapling.IsStump);
            Assert.AreEqual(1, sapling.Stage, "a young tree, under the same id");
        }

        [Test]
        public void AStump_IsNotHarvested_NorCounted()
        {
            Species tree = TestSpecies.Tree();
            tree.Harvest = HarvestKind.Perennial;
            tree.FruitSteps = 10;
            Ecosystem ecosystem = WithGrownTree(tree, out _, out int plantId);
            while (Get(ecosystem, plantId).RipeStage < 1) ecosystem.Step();

            ecosystem.CutToStump(plantId);

            Assert.IsFalse(ecosystem.TryHarvest(plantId, out _));
            Assert.AreEqual(0, ecosystem.PlantCounts()[0]);
            Assert.AreEqual(0, ecosystem.PlantDensity(0, 8)[X / 8 + ecosystem.CensusCellsX(8) * (Z / 8)]);
        }

        [Test]
        public void AStump_IsUprooted_WhenItsGroundChanges()
        {
            Ecosystem ecosystem = WithGrownTree(TestSpecies.Tree(), out World world, out int plantId);
            ecosystem.CutToStump(plantId);

            world.SetBlock(new BlockPos(X, MeadowGenerator.Surface - 1, Z), Blocks.Stone);
            ecosystem.Step();

            Assert.IsFalse(ecosystem.TryGet(plantId, out _));
        }

        [Test]
        public void AStump_IsSavedWithItsCountdown()
        {
            Ecosystem ecosystem = WithGrownTree(TestSpecies.Tree(), out World world, out int plantId);
            ecosystem.CutToStump(plantId);
            for (int step = 0; step < 10; step++) ecosystem.Step();
            var stream = new MemoryStream();
            ecosystem.Write(stream);
            stream.Position = 0;

            Ecosystem loaded = Ecosystem.Read(stream, world, new[] { TestSpecies.Tree() });

            Plant saved = Get(ecosystem, plantId);
            Plant stump = Get(loaded, plantId);
            Assert.IsTrue(stump.IsStump);
            Assert.AreEqual(saved.Age, stump.Age);
        }
    }
}

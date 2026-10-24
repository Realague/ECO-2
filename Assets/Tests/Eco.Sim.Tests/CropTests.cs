using System.IO;
using NUnit.Framework;
using Voxel.Core;

namespace Eco.Sim.Tests
{
    // Crops: plants that ripen once adult (Plant.Fruit) and are harvested, annuals pulled up,
    // perennials picked again and again.
    public class CropTests
    {
        private const int Seed = 11;
        private const int FruitSteps = 30;

        private static Species Crop(HarvestKind harvest)
        {
            Species crop = TestSpecies.Bush();
            crop.Name = harvest == HarvestKind.Annual ? "Carrot" : "Strawberry";
            crop.SeedChance = 0f;
            crop.MinSoil = 0f;
            crop.SoilUse = 0f;
            crop.Harvest = harvest;
            crop.FruitSteps = FruitSteps;
            return crop;
        }

        private static Ecosystem WithGrownCrop(HarvestKind harvest, out int plantId)
        {
            World world = MeadowGenerator.CreateWorld(2);
            var ecosystem = new Ecosystem(world, new[] { Crop(harvest) }, Seed);
            Assert.IsTrue(ecosystem.TryPlaceGrownPlant(0, 10, 10));
            plantId = ecosystem.Plants[0].Id;
            return ecosystem;
        }

        private static Plant Get(Ecosystem ecosystem, int plantId)
        {
            Assert.IsTrue(ecosystem.TryGet(plantId, out Plant plant));
            return plant;
        }

        [Test]
        public void AnAdultCrop_RipensThroughEachStage()
        {
            Ecosystem ecosystem = WithGrownCrop(HarvestKind.Perennial, out int plantId);
            Assert.AreEqual(0, Get(ecosystem, plantId).RipeStage);

            int lastStage = 0;
            int changes = 0;
            ecosystem.PlantChanged += plant => changes++;
            for (int step = 0; step < 4 * FruitSteps; step++)
            {
                ecosystem.Step();
                int stage = Get(ecosystem, plantId).RipeStage;
                Assert.That(stage, Is.GreaterThanOrEqualTo(lastStage), "ripeness never goes back by itself");
                lastStage = stage;
            }

            Assert.AreEqual(Plant.RipeStageCount, lastStage, "fully ripe on rich soil");
            Assert.AreEqual(Plant.RipeStageCount, changes, "one change per stage, not per step");
        }

        [Test]
        public void TryHarvest_RefusesAnUnripeCrop_AndAPlantWithoutCrop()
        {
            Ecosystem ecosystem = WithGrownCrop(HarvestKind.Annual, out int plantId);
            Assert.IsFalse(ecosystem.TryHarvest(plantId, out _), "just grown, not yet ripe");

            World world = MeadowGenerator.CreateWorld(2);
            var trees = new Ecosystem(world, TestSpecies.TreeOnly(), Seed);
            Assert.IsTrue(trees.TryPlaceGrownPlant(0, 10, 10));
            Assert.IsFalse(trees.TryHarvest(trees.Plants[0].Id, out _), "a tree with no crop");
        }

        [Test]
        public void AnAnnual_IsPulledUpWhenHarvested()
        {
            Ecosystem ecosystem = WithGrownCrop(HarvestKind.Annual, out int plantId);
            while (Get(ecosystem, plantId).RipeStage < 2) ecosystem.Step();

            Assert.IsTrue(ecosystem.TryHarvest(plantId, out int stage));
            Assert.AreEqual(2, stage);
            Assert.IsFalse(ecosystem.TryGet(plantId, out _));
        }

        [Test]
        public void APerennial_LivesOn_AndRipensAgain()
        {
            Ecosystem ecosystem = WithGrownCrop(HarvestKind.Perennial, out int plantId);
            while (Get(ecosystem, plantId).RipeStage < Plant.RipeStageCount) ecosystem.Step();

            Assert.IsTrue(ecosystem.TryHarvest(plantId, out int stage));
            Assert.AreEqual(Plant.RipeStageCount, stage);
            Assert.AreEqual(0, Get(ecosystem, plantId).RipeStage, "picked: nothing left to pick");
            Assert.IsFalse(ecosystem.TryHarvest(plantId, out _));

            for (int step = 0; step < 4 * FruitSteps; step++) ecosystem.Step();
            Assert.AreEqual(Plant.RipeStageCount, Get(ecosystem, plantId).RipeStage, "ripe again");
        }

        [Test]
        public void TheHarvest_GrowsWithTheStage()
        {
            Assert.AreEqual(0, CropYield.Share(6, 0));
            Assert.AreEqual(2, CropYield.Share(6, 1));
            Assert.AreEqual(4, CropYield.Share(6, 2));
            Assert.AreEqual(6, CropYield.Share(6, 3));
            Assert.AreEqual(1, CropYield.Share(1, 1), "rounded up: something always comes");
            Assert.AreEqual(0, CropYield.Share(0, 3));

            Assert.AreEqual(0, CropYield.Seeds(3, 1), "no seeds when picked unripe");
            Assert.AreEqual(2, CropYield.Seeds(3, 2));
            Assert.AreEqual(3, CropYield.Seeds(3, 3));
            Assert.AreEqual(1, CropYield.Seeds(1, 2));
        }

        [Test]
        public void AShadePlant_OnlySproutsNearAGrownTree()
        {
            World world = MeadowGenerator.CreateWorld(2);
            Species mushroom = TestSpecies.Bush();
            mushroom.Name = "Mushroom";
            mushroom.MinSoil = 0f;
            mushroom.NeedsShade = true;
            var ecosystem = new Ecosystem(world, new[] { TestSpecies.Tree(), mushroom }, Seed);
            Assert.IsTrue(ecosystem.TryPlaceGrownPlant(0, 10, 10));

            Assert.IsFalse(ecosystem.TryPlant(1, 20, 20), "in the open");
            Assert.IsTrue(ecosystem.TryPlant(1, 11, 12), "under the tree");
        }

        [Test]
        public void AnAnimal_TakesThePerennialsFruit_AndSparesThePlant()
        {
            World world = MeadowGenerator.CreateWorld(2);
            Species tree = TestSpecies.Tree();
            tree.Name = "AppleTree";
            tree.Harvest = HarvestKind.Perennial;
            tree.FruitSteps = FruitSteps;
            tree.SeedChance = 0f;
            var deer = new AnimalSpecies
            {
                Name = "Deer",
                EatsPlants = new[] { 0 },
                HungerPerStep = 0.05f,
                MealValue = 0.5f,
                SenseRange = 6,
                MoveRange = 3,
                ReachHeight = 1f,
                LifespanSteps = 1000,
                MaturitySteps = SimClock.StepsPerDay,
                Height = 1f,
                Width = 1f
            };
            var ecosystem = new Ecosystem(world, new[] { tree }, new[] { deer }, Seed);
            Assert.IsTrue(ecosystem.TryPlaceGrownPlant(0, 10, 10));
            int treeId = ecosystem.Plants[0].Id;
            while (Get(ecosystem, treeId).RipeStage < Plant.RipeStageCount) ecosystem.Step();

            Assert.IsTrue(ecosystem.TryPlaceAnimal(0, 12, 10));
            bool eaten = false;
            for (int step = 0; step < 100 && !eaten; step++)
            {
                ecosystem.Step();
                eaten = Get(ecosystem, treeId).RipeStage < Plant.RipeStageCount;
            }

            Plant plant = Get(ecosystem, treeId);
            Assert.IsTrue(eaten, "a tree too tall to browse, but its fruit within reach");
            Assert.AreEqual(1f, plant.Growth, "the tree itself is spared");
        }

        [Test]
        public void Ripeness_IsSaved()
        {
            Ecosystem ecosystem = WithGrownCrop(HarvestKind.Perennial, out int plantId);
            for (int step = 0; step < FruitSteps / 2; step++) ecosystem.Step();
            float fruit = Get(ecosystem, plantId).Fruit;
            Assert.That(fruit, Is.GreaterThan(0f));

            var stream = new MemoryStream();
            ecosystem.Write(stream);
            Ecosystem loaded = Ecosystem.Read(new MemoryStream(stream.ToArray()), MeadowGenerator.CreateWorld(2), new[] { Crop(HarvestKind.Perennial) });
            Assert.AreEqual(fruit, Get(loaded, plantId).Fruit);
        }

        [Test]
        public void ASaveFromBeforeCropsRipened_Loads_EveryCropUnripe()
        {
            Ecosystem ecosystem = WithGrownCrop(HarvestKind.Perennial, out int plantId);
            for (int step = 0; step < FruitSteps; step++) ecosystem.Step();
            var stream = new MemoryStream();
            ecosystem.Write(stream);
            World world = MeadowGenerator.CreateWorld(2);
            byte[] formatFive = OldSaves.WithoutFruit(stream.ToArray(), world);

            Ecosystem loaded = Ecosystem.Read(new MemoryStream(formatFive), world, new[] { Crop(HarvestKind.Perennial) });
            Plant plant = Get(loaded, plantId);
            Assert.AreEqual(1f, plant.Growth);
            Assert.AreEqual(0f, plant.Fruit);
        }

        [Test]
        public void ASave_LoadsWithSpeciesAddedAtTheEnd()
        {
            World world = MeadowGenerator.CreateWorld(2);
            var ecosystem = new Ecosystem(world, TestSpecies.TreeOnly(), Seed);
            ecosystem.Populate(200);
            var stream = new MemoryStream();
            ecosystem.Write(stream);

            Ecosystem loaded = Ecosystem.Read(new MemoryStream(stream.ToArray()), world, new[] { TestSpecies.Tree(), Crop(HarvestKind.Annual) });
            Assert.AreEqual(ecosystem.Plants.Count, loaded.Plants.Count);
            Assert.AreEqual(1, loaded.SavedSpeciesCount);

            // The species added since are sown, and only they.
            int sown = loaded.Populate(400, loaded.SavedSpeciesCount);
            Assert.That(sown, Is.GreaterThan(0));
            Assert.AreEqual(ecosystem.Plants.Count + sown, loaded.Plants.Count);
            int crops = 0;
            foreach (Plant plant in loaded.Plants)
            {
                if (plant.SpeciesIndex == 1) crops++;
            }

            Assert.AreEqual(sown, crops);
        }
    }
}

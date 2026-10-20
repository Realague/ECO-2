using System.IO;
using NUnit.Framework;
using Voxel.Core;

namespace Eco.Sim.Tests
{
    // The soil's nitrogen, phosphorus and potassium: where they come from, what plants take and give back.
    public class SoilTests
    {
        private const int Seed = 6;

        // A world of 32 × 32 columns in four quarters: granite under grass (x < 16, z < 16), limestone
        // under grass (x ≥ 16, z < 16), sand over granite (x < 16, z ≥ 16), marsh mud over limestone
        // (x ≥ 16, z ≥ 16).
        private sealed class QuartersGenerator : IChunkGenerator
        {
            public const int Surface = 8;

            public int Version => 1;

            public void Prepare(int seed, int sizeX, int sizeZ)
            {
            }

            public void Generate(Chunk chunk, int seed)
            {
                for (int z = 0; z < Chunk.Size; z++)
                {
                    for (int x = 0; x < Chunk.Size; x++)
                    {
                        int worldX = chunk.Coord.X * Chunk.Size + x;
                        int worldZ = chunk.Coord.Z * Chunk.Size + z;
                        ushort rock = worldX < 16 ? Blocks.Granite : Blocks.Limestone;
                        ushort top = worldZ < 16 ? Blocks.Grass : worldX < 16 ? Blocks.Sand : Blocks.Mud;
                        for (int y = 0; y < Surface; y++)
                        {
                            chunk.Set(x, y, z, y < Surface - 2 ? rock : y == Surface - 2 ? Blocks.Dirt : top);
                        }
                    }
                }
            }
        }

        private static World Quarters()
        {
            var world = new World(2, 1, 2);
            world.Generate(new QuartersGenerator(), 0);
            return world;
        }

        private static Species Needing(float nitrogen, float phosphorus, float potassium, float minFertility = 0f)
        {
            Species plant = TestSpecies.Bush();
            plant.Name = "Plant";
            plant.SeedChance = 0f;
            plant.MinFertility = minFertility;
            plant.NeedNitrogen = nitrogen;
            plant.NeedPhosphorus = phosphorus;
            plant.NeedPotassium = potassium;
            plant.Grounds = new[] { Blocks.Grass, Blocks.Dirt, Blocks.Sand, Blocks.Mud };
            return plant;
        }

        [Test]
        public void Capacities_FollowTheRockAndTheSoil()
        {
            var ecosystem = new Ecosystem(Quarters(), new[] { Needing(1f, 1f, 1f) }, Seed);
            SoilSample granite = ecosystem.GetSoilCapacity(4, 4);
            SoilSample limestone = ecosystem.GetSoilCapacity(24, 4);
            SoilSample sand = ecosystem.GetSoilCapacity(4, 24);
            SoilSample mud = ecosystem.GetSoilCapacity(24, 24);

            Assert.That(granite.Potassium, Is.GreaterThan(granite.Phosphorus), "granite gives potassium");
            Assert.That(limestone.Phosphorus, Is.GreaterThan(limestone.Potassium), "limestone gives phosphorus");
            Assert.That(sand.Nitrogen, Is.LessThan(granite.Nitrogen), "sand holds little");
            Assert.That(sand.Phosphorus, Is.LessThan(granite.Phosphorus));
            Assert.That(sand.Potassium, Is.LessThan(granite.Potassium));
            Assert.That(mud.Nitrogen, Is.GreaterThan(limestone.Nitrogen), "marsh mud is rich in nitrogen");
            Assert.AreEqual(granite, ecosystem.GetSoil(4, 4), "the soil starts full");
        }

        [Test]
        public void APlant_IsLimitedByTheNutrientItLacksMost()
        {
            var ecosystem = new Ecosystem(Quarters(), new[] { Needing(0f, 1f, 0f, 0.7f), Needing(0f, 0f, 1f, 0.7f) }, Seed);

            Assert.IsTrue(ecosystem.TryPlant(0, 24, 4), "phosphorus enough on limestone");
            Assert.IsFalse(ecosystem.TryPlant(0, 4, 4), "too little phosphorus on granite");
            Assert.IsTrue(ecosystem.TryPlant(1, 4, 5), "potassium enough on granite");
            Assert.IsFalse(ecosystem.TryPlant(1, 4, 24), "too little potassium on sand");
        }

        [Test]
        public void APlant_DrawsMostOnWhatItNeeds()
        {
            Species grass = Needing(1f, 0.1f, 0.1f);
            grass.FertilityUse = 0.02f;
            var ecosystem = new Ecosystem(Quarters(), new[] { grass }, Seed);
            SoilSample before = ecosystem.GetSoil(4, 4);
            Assert.IsTrue(ecosystem.TryPlaceGrownPlant(0, 5, 5));

            for (int step = 0; step < 50; step++) ecosystem.Step();

            SoilSample after = ecosystem.GetSoil(4, 4);
            Assert.That(before.Nitrogen - after.Nitrogen, Is.GreaterThan(2f * (before.Phosphorus - after.Phosphorus)));
            Assert.That(after.Nitrogen, Is.LessThan(before.Nitrogen));
        }

        [Test]
        public void ALegume_GivesNitrogenBack()
        {
            Species bean = Needing(0.1f, 0.7f, 0.6f);
            bean.FixesNitrogen = 0.01f;
            var ecosystem = new Ecosystem(Quarters(), new[] { bean }, Seed);
            float capacity = ecosystem.GetSoilCapacity(4, 4).Nitrogen;
            Assert.IsTrue(ecosystem.TryPlaceGrownPlant(0, 5, 5));

            for (int step = 0; step < 100; step++) ecosystem.Step();

            Assert.That(ecosystem.GetSoil(4, 4).Nitrogen, Is.GreaterThan(capacity), "the bean raises nitrogen past the soil's usual level");
        }

        [Test]
        public void APlantDyingWhereItStands_ReturnsToTheSoil_AFelledOneDoesNot()
        {
            Species shortLived = Needing(1f, 1f, 1f);
            shortLived.FertilityUse = 0.001f;
            shortLived.LifespanSteps = shortLived.MaturitySteps + 1;

            var dying = new Ecosystem(Quarters(), new[] { shortLived }, Seed);
            Assert.IsTrue(dying.TryPlaceGrownPlant(0, 5, 5));
            dying.Step();
            dying.Step();
            Assert.AreEqual(0, dying.Plants.Count, "dead of old age");

            var felled = new Ecosystem(Quarters(), new[] { shortLived }, Seed);
            Assert.IsTrue(felled.TryPlaceGrownPlant(0, 5, 5));
            felled.Step();
            Assert.IsTrue(felled.Remove(felled.Plants[0].Id));
            felled.Step();

            float capacity = dying.GetSoilCapacity(4, 4).Nitrogen;
            Assert.That(dying.GetSoil(4, 4).Nitrogen, Is.GreaterThan(capacity), "its litter enriches the soil");
            Assert.That(felled.GetSoil(4, 4).Nitrogen, Is.LessThanOrEqualTo(capacity), "a felled plant is carried away");
        }

        [Test]
        public void ASaveFromBeforeNutrients_Loads_EachNutrientAtTheSameShareOfItsCapacity()
        {
            World world = Quarters();
            var ecosystem = new Ecosystem(world, new[] { Needing(1f, 1f, 1f) }, Seed);
            var stream = new MemoryStream();
            ecosystem.Write(stream);
            byte[] formatSix = OldSaves.WithoutNutrients(stream.ToArray(), world, 0.5f);

            Ecosystem loaded = Ecosystem.Read(new MemoryStream(formatSix), world, new[] { Needing(1f, 1f, 1f) });

            SoilSample capacity = loaded.GetSoilCapacity(24, 4);
            SoilSample soil = loaded.GetSoil(24, 4);
            Assert.AreEqual(capacity.Nitrogen * 0.5f, soil.Nitrogen, 1e-5f);
            Assert.AreEqual(capacity.Phosphorus * 0.5f, soil.Phosphorus, 1e-5f);
            Assert.AreEqual(capacity.Potassium * 0.5f, soil.Potassium, 1e-5f);
        }

        [Test]
        public void TheSoil_IsSaved()
        {
            World world = Quarters();
            Species grass = Needing(1f, 0.3f, 0.3f);
            grass.FertilityUse = 0.02f;
            var ecosystem = new Ecosystem(world, new[] { grass }, Seed);
            Assert.IsTrue(ecosystem.TryPlaceGrownPlant(0, 5, 5));
            for (int step = 0; step < 30; step++) ecosystem.Step();

            var stream = new MemoryStream();
            ecosystem.Write(stream);
            Ecosystem loaded = Ecosystem.Read(new MemoryStream(stream.ToArray()), world, new[] { grass });

            Assert.AreEqual(ecosystem.GetSoil(4, 4), loaded.GetSoil(4, 4));
        }
    }
}

using System;
using System.IO;
using NUnit.Framework;
using Voxel.Core;

namespace Eco.Sim.Tests
{
    public class CarrionTests
    {
        private const int Seed = 11;
        private const int RabbitIndex = 0;
        private const int FoxIndex = 1;

        [Test]
        public void StarvedAnimal_LeavesACarcass_ThatRotsIntoTheSoilInAFewDays()
        {
            Ecosystem ecosystem = CreateEcosystem(Rabbit(hungerPerStep: 0.2f), Fox());
            ecosystem.TryPlaceAnimal(RabbitIndex, 10, 10);
            float soilBefore = ecosystem.GetSoil(10, 10).Nitrogen;
            float phosphorusBefore = ecosystem.GetSoil(10, 10).Phosphorus;

            Run(ecosystem, 6);

            Assert.AreEqual(0, ecosystem.Animals.Count);
            Assert.AreEqual(1, ecosystem.Carcasses.Count);
            Carcass carcass = ecosystem.Carcasses[0];
            Assert.AreEqual(RabbitIndex, carcass.SpeciesIndex);

            Run(ecosystem, SimClock.StepsPerDay);
            Assert.That(ecosystem.GetSoil(carcass.X, carcass.Z).Nitrogen, Is.GreaterThan(soilBefore), "the rotting carcass feeds the soil past its usual richness");
            Assert.That(ecosystem.GetSoil(carcass.X, carcass.Z).Phosphorus, Is.GreaterThan(phosphorusBefore), "with phosphorus too");

            Run(ecosystem, 3 * SimClock.StepsPerDay);
            Assert.AreEqual(0, ecosystem.Carcasses.Count, "rotted away");
        }

        [Test]
        public void EnrichedSoil_ComesBackToItsUsualRichness()
        {
            Ecosystem ecosystem = CreateEcosystem(Rabbit(hungerPerStep: 0.2f), Fox());
            ecosystem.TryPlaceAnimal(RabbitIndex, 10, 10);
            float usual = ecosystem.GetSoil(10, 10).Nitrogen;

            Run(ecosystem, 30 * SimClock.StepsPerDay);

            Assert.AreEqual(usual, ecosystem.GetSoil(10, 10).Nitrogen, 0.001f);
        }

        [Test]
        public void HungryScavenger_EatsACarcass()
        {
            AnimalSpecies scavenger = Fox(hungerPerStep: 0.2f);
            scavenger.EatsAnimals = Array.Empty<int>();
            scavenger.EatsCarrion = true;
            Ecosystem ecosystem = CreateEcosystem(Rabbit(hungerPerStep: 0.2f), scavenger);
            ecosystem.TryPlaceAnimal(RabbitIndex, 10, 10);
            Run(ecosystem, 6);
            Assert.AreEqual(1, ecosystem.Carcasses.Count);
            float meat = ecosystem.Carcasses[0].Meat;

            ecosystem.TryPlaceAnimal(FoxIndex, 13, 10);
            Run(ecosystem, 3);

            Assert.AreEqual(1, ecosystem.Animals.Count);
            Assert.That(ecosystem.Animals[0].Hunger, Is.LessThan(0.6f), "it fed");
            Assert.That(ecosystem.Carcasses.Count == 0 || ecosystem.Carcasses[0].Meat < meat - 0.2f, "the carcass was eaten from");
        }

        [Test]
        public void Predator_LeavesTheRemainsOfItsCatch()
        {
            Ecosystem ecosystem = CreateEcosystem(Rabbit(), Fox(hungerPerStep: 0.2f, catchChance: 1f));
            ecosystem.TryPlaceAnimal(RabbitIndex, 10, 10);
            ecosystem.TryPlaceAnimal(FoxIndex, 12, 10);

            Run(ecosystem, 3);

            Assert.AreEqual(0, ecosystem.CountAnimals(RabbitIndex));
            Assert.AreEqual(1, ecosystem.Carcasses.Count);
            Assert.That(ecosystem.Carcasses[0].Meat, Is.LessThanOrEqualTo(0.5f));
        }

        [Test]
        public void Player_TakesAWholeCarcass_ButNotWhatIsLeftOfACatch()
        {
            Ecosystem whole = CreateEcosystem(Rabbit(), Fox());
            whole.TryPlaceAnimal(RabbitIndex, 10, 10);
            whole.RemoveAnimal(whole.Animals[0].Id);
            Carcass lying = whole.Carcasses[0];

            Assert.IsFalse(whole.TryTakeCarcass(lying.X + 1, lying.Z, 0.5f, out _), "nothing lies there");
            Assert.IsTrue(whole.TryTakeCarcass(lying.X, lying.Z, 0.5f, out Carcass taken));
            Assert.AreEqual(RabbitIndex, taken.SpeciesIndex);
            Assert.AreEqual(0, whole.Carcasses.Count);
            Assert.IsFalse(whole.TryTakeCarcass(lying.X, lying.Z, 0.5f, out _), "it is gone");

            Ecosystem eaten = CreateEcosystem(Rabbit(), Fox(hungerPerStep: 0.2f, catchChance: 1f));
            eaten.TryPlaceAnimal(RabbitIndex, 10, 10);
            eaten.TryPlaceAnimal(FoxIndex, 12, 10);
            Run(eaten, 3);
            Carcass remains = eaten.Carcasses[0];

            Assert.IsFalse(eaten.TryTakeCarcass(remains.X, remains.Z, remains.Meat + 0.01f, out _));
            Assert.AreEqual(1, eaten.Carcasses.Count, "a refused carcass stays");
        }

        [Test]
        public void PlayerKill_LeavesAWholeCarcass()
        {
            Ecosystem ecosystem = CreateEcosystem(Rabbit(), Fox());
            ecosystem.TryPlaceAnimal(RabbitIndex, 10, 10);

            ecosystem.RemoveAnimal(ecosystem.Animals[0].Id);

            Assert.AreEqual(1, ecosystem.Carcasses.Count);
            Assert.AreEqual(1f, ecosystem.Carcasses[0].Meat);
        }

        [Test]
        public void WriteThenRead_KeepsTheCarcasses()
        {
            World world = MeadowGenerator.CreateWorld(2);
            Ecosystem original = CreateEcosystem(Rabbit(hungerPerStep: 0.2f), Fox(), world);
            original.TryPlaceAnimal(RabbitIndex, 10, 10);
            original.TryPlaceAnimal(RabbitIndex, 20, 20);
            Run(original, 10);
            var stream = new MemoryStream();
            original.Write(stream);
            stream.Position = 0;

            Ecosystem loaded = Ecosystem.Read(stream, world, Plants(), new[] { Rabbit(hungerPerStep: 0.2f), Fox() });

            Assert.AreEqual(2, loaded.Carcasses.Count);
            for (int i = 0; i < original.Carcasses.Count; i++)
            {
                Carcass a = original.Carcasses[i];
                Carcass b = loaded.Carcasses[i];
                Assert.AreEqual((a.SpeciesIndex, a.X, a.Y, a.Z, a.Meat, a.Age), (b.SpeciesIndex, b.X, b.Y, b.Z, b.Meat, b.Age));
            }

            Run(original, 100);
            Run(loaded, 100);
            Assert.AreEqual(original.Carcasses.Count, loaded.Carcasses.Count);
            Assert.AreEqual(original.GetSoil(10, 10), loaded.GetSoil(10, 10));
        }

        private static AnimalSpecies Rabbit(float hungerPerStep = 0f)
        {
            return new AnimalSpecies
            {
                Name = "Rabbit",
                EatsPlants = new[] { 1 },
                MoveRange = 0,
                SenseRange = 5,
                LifespanSteps = 15 * SimClock.StepsPerDay,
                MaturitySteps = SimClock.StepsPerDay,
                HungerPerStep = hungerPerStep,
                MealValue = 0.5f,
                Height = 0.6f,
                Width = 0.6f
            };
        }

        private static AnimalSpecies Fox(float hungerPerStep = 0f, float catchChance = 0.5f)
        {
            return new AnimalSpecies
            {
                Name = "Fox",
                EatsAnimals = new[] { RabbitIndex },
                MoveRange = 2,
                SenseRange = 6,
                LifespanSteps = 30 * SimClock.StepsPerDay,
                MaturitySteps = 3 * SimClock.StepsPerDay,
                HungerPerStep = hungerPerStep,
                MealValue = 0.5f,
                CatchChance = catchChance,
                Height = 0.8f,
                Width = 0.8f
            };
        }

        // No seeds: the meadow stays bare, so only carcasses change the soil.
        private static Species[] Plants()
        {
            Species tree = TestSpecies.Tree();
            tree.SeedChance = 0f;
            Species bush = TestSpecies.Bush();
            bush.SeedChance = 0f;
            return new[] { tree, bush };
        }

        private static Ecosystem CreateEcosystem(AnimalSpecies rabbit, AnimalSpecies fox, World world = null)
        {
            return new Ecosystem(world ?? MeadowGenerator.CreateWorld(2), Plants(), new[] { rabbit, fox }, Seed);
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

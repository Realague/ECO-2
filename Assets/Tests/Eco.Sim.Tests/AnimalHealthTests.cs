using System.IO;
using NUnit.Framework;
using Voxel.Core;

namespace Eco.Sim.Tests
{
    public class AnimalHealthTests
    {
        private const int Seed = 5;

        [Test]
        public void AnAnimal_DiesOnceItsDamageReachesItsHealth_AndLeavesACarcass()
        {
            Ecosystem ecosystem = CreateEcosystem();
            ecosystem.TryPlaceAnimal(0, 10, 10);
            int id = ecosystem.Animals[0].Id;

            Assert.IsFalse(ecosystem.Damage(id, 10f), "a body shot wounds a deer of 20");
            Assert.AreEqual(1, ecosystem.Animals.Count);
            Assert.AreEqual(10f, ecosystem.Animals[0].Damage);

            Assert.IsTrue(ecosystem.Damage(id, 10f), "a second one kills it");
            Assert.AreEqual(0, ecosystem.Animals.Count);
            Assert.AreEqual(1, ecosystem.Carcasses.Count);
            Assert.AreEqual(1f, ecosystem.Carcasses[0].Meat, "a whole carcass, as for any kill by a player");
            Assert.IsFalse(ecosystem.Damage(id, 10f), "nothing left to hit");
        }

        // Clients show animals a little away from their column: one killed falls where it was hit, if
        // that is within its MoveRange and a block of its column, else in its own column.
        [Test]
        public void AKilledAnimal_FallsWhereItWasHit_WhenThatIsNearEnough()
        {
            Ecosystem ecosystem = CreateEcosystem();
            ecosystem.TryPlaceAnimal(0, 10, 10);
            ecosystem.TryPlaceAnimal(0, 20, 20);
            int near = ecosystem.Animals[0].Id;
            int far = ecosystem.Animals[1].Id;

            Assert.IsTrue(ecosystem.Damage(near, 20f, 11, 9));
            Assert.IsTrue(ecosystem.Damage(far, 20f, 25, 20));

            Assert.AreEqual(2, ecosystem.Carcasses.Count);
            Assert.AreEqual((11, 9), (ecosystem.Carcasses[0].X, ecosystem.Carcasses[0].Z), "where the shooter saw it");
            Assert.AreEqual((20, 20), (ecosystem.Carcasses[1].X, ecosystem.Carcasses[1].Z), "too far from where it stood: its own column");
        }

        [Test]
        public void AWound_HealsInADay()
        {
            Ecosystem ecosystem = CreateEcosystem();
            ecosystem.TryPlaceAnimal(0, 10, 10);
            ecosystem.Damage(ecosystem.Animals[0].Id, 15f);

            Run(ecosystem, SimClock.StepsPerDay / 2);
            Assert.That(ecosystem.Animals[0].Damage, Is.EqualTo(5f).Within(0.01f));

            Run(ecosystem, SimClock.StepsPerDay / 2);
            Assert.AreEqual(0f, ecosystem.Animals[0].Damage);
        }

        [Test]
        public void AFleeingAnimal_RunsStraightAwayFromTheThreat()
        {
            Ecosystem ecosystem = CreateEcosystem();
            ecosystem.TryPlaceAnimal(0, 10, 10);
            int id = ecosystem.Animals[0].Id;

            Assert.IsTrue(ecosystem.Flee(id, 8.5f, 10.5f, 6));
            Assert.AreEqual((16, 10), (ecosystem.Animals[0].X, ecosystem.Animals[0].Z), "six columns on, away from the threat");
            Assert.IsTrue(ecosystem.IsAnimalInside(16, ecosystem.Animals[0].Y, 10), "its column follows it");
            Assert.IsFalse(ecosystem.IsAnimalInside(10, ecosystem.Animals[0].Y, 10));

            Assert.IsTrue(ecosystem.Flee(id, 16.5f, 4.5f, 30));
            Assert.AreEqual(31, ecosystem.Animals[0].Z, "it stops at the edge of the world");
            Assert.IsFalse(ecosystem.Flee(id + 1, 0f, 0f, 6), "no such animal");
        }

        [Test]
        public void WriteThenRead_KeepsTheWounds()
        {
            World world = MeadowGenerator.CreateWorld(2);
            Ecosystem original = CreateEcosystem(world);
            original.TryPlaceAnimal(0, 10, 10);
            original.TryPlaceAnimal(0, 20, 20);
            original.Damage(original.Animals[1].Id, 12.5f);
            var stream = new MemoryStream();
            original.Write(stream);
            stream.Position = 0;

            Ecosystem loaded = Ecosystem.Read(stream, world, Plants(), new[] { Deer() });

            Assert.AreEqual(0f, loaded.Animals[0].Damage);
            Assert.AreEqual(12.5f, loaded.Animals[1].Damage);
        }

        private static AnimalSpecies Deer()
        {
            return new AnimalSpecies
            {
                Name = "Deer",
                MoveRange = 0,
                SenseRange = 5,
                LifespanSteps = 30 * SimClock.StepsPerDay,
                MaturitySteps = SimClock.StepsPerDay,
                Height = 1.3f,
                Width = 1f,
                Health = 20f
            };
        }

        private static Species[] Plants()
        {
            Species bush = TestSpecies.Bush();
            bush.SeedChance = 0f;
            return new[] { bush };
        }

        private static Ecosystem CreateEcosystem(World world = null)
        {
            return new Ecosystem(world ?? MeadowGenerator.CreateWorld(2), Plants(), new[] { Deer() }, Seed);
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

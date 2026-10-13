using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Voxel.Core;

namespace Eco.Sim.Tests
{
    public class EcosystemTests
    {
        private const int Seed = 7;
        private const int Ground = MeadowGenerator.Surface - 1;

        [Test]
        public void TryPlant_OnGrass_AddsASeedlingAtGroundLevel()
        {
            var ecosystem = new Ecosystem(MeadowGenerator.CreateWorld(2), TestSpecies.TreeOnly(), Seed);
            var added = new List<Plant>();
            ecosystem.PlantAdded += added.Add;

            bool planted = ecosystem.TryPlant(0, 10, 12);

            Assert.IsTrue(planted);
            Assert.AreEqual(1, ecosystem.Plants.Count);
            Assert.AreEqual(1, added.Count);
            Plant plant = ecosystem.Plants[0];
            Assert.AreEqual((10, MeadowGenerator.Surface, 12), (plant.X, plant.Y, plant.Z));
            Assert.AreEqual(0f, plant.Growth);
            Assert.AreEqual(0, plant.Stage);
        }

        [Test]
        public void TryPlant_OnStoneOutsideWorldOrTooCloseToAnotherPlant_Fails()
        {
            World world = MeadowGenerator.CreateWorld(2);
            world.SetBlock(5, Ground, 5, Blocks.Stone);
            var ecosystem = new Ecosystem(world, TestSpecies.TreeOnly(), Seed);
            ecosystem.TryPlant(0, 10, 10);

            Assert.IsFalse(ecosystem.TryPlant(0, 5, 5), "sur de la pierre");
            Assert.IsFalse(ecosystem.TryPlant(0, -1, 3), "hors du monde");
            Assert.IsFalse(ecosystem.TryPlant(0, 10, 10), "même colonne");
            Assert.IsFalse(ecosystem.TryPlant(0, 11, 11), "colonne voisine, espacement de 2");
            Assert.IsTrue(ecosystem.TryPlant(0, 12, 10), "à deux blocs");
        }

        [Test]
        public void TryPlant_NextToAnotherSpecies_Succeeds_ButNeverInTheSameColumn()
        {
            var ecosystem = new Ecosystem(MeadowGenerator.CreateWorld(2), TestSpecies.TreeAndBush(), Seed);
            ecosystem.TryPlant(1, 10, 10);

            Assert.IsTrue(ecosystem.TryPlant(0, 11, 10), "un arbre à côté d'un buisson");
            Assert.IsFalse(ecosystem.TryPlant(0, 10, 10), "un arbre dans la colonne du buisson");
        }

        [Test]
        public void Step_GrowsASeedlingToAdultThroughEveryStage()
        {
            var ecosystem = new Ecosystem(MeadowGenerator.CreateWorld(2), TestSpecies.TreeOnly(), Seed);
            ecosystem.TryPlant(0, 10, 10);
            int firstId = ecosystem.Plants[0].Id;
            var stages = new List<int>();
            ecosystem.PlantChanged += plant =>
            {
                if (plant.Id == firstId) stages.Add(plant.Stage);
            };

            // Même sur le sol le plus pauvre (fertilité 0,4), trois jours de croissance en demandent moins de huit.
            Run(ecosystem, 8 * SimClock.StepsPerDay);

            Assert.IsTrue(ecosystem.TryGet(firstId, out Plant adult));
            Assert.AreEqual(1f, adult.Growth);
            CollectionAssert.AreEqual(Enumerable.Range(1, Plant.StageCount - 1), stages);
        }

        [Test]
        public void Step_PlantDies_WhenItsGroundIsDugOrABlockIsPlacedOnIt()
        {
            World world = MeadowGenerator.CreateWorld(2);
            var ecosystem = new Ecosystem(world, TestSpecies.TreeOnly(), Seed);
            ecosystem.TryPlant(0, 10, 10);
            ecosystem.TryPlant(0, 20, 20);
            var removed = new List<int>();
            ecosystem.PlantRemoved += removed.Add;
            int dugId = ecosystem.Plants[0].Id;
            int coveredId = ecosystem.Plants[1].Id;

            world.SetBlock(10, Ground, 10, Blocks.Air);
            world.SetBlock(20, MeadowGenerator.Surface, 20, Blocks.Stone);
            ecosystem.Step();

            Assert.AreEqual(0, ecosystem.Plants.Count);
            CollectionAssert.AreEquivalent(new[] { dugId, coveredId }, removed);
        }

        [Test]
        public void Step_PlantSurvives_WhenBlocksChangeAroundItButNotUnderIt()
        {
            World world = MeadowGenerator.CreateWorld(2);
            var ecosystem = new Ecosystem(world, TestSpecies.TreeOnly(), Seed);
            ecosystem.TryPlant(0, 10, 10);

            world.SetBlock(11, Ground, 10, Blocks.Air);
            world.SetBlock(10, MeadowGenerator.Surface + 3, 10, Blocks.Stone);
            world.SetBlock(10, Ground - 1, 10, Blocks.Stone);
            ecosystem.Step();

            Assert.AreEqual(1, ecosystem.Plants.Count);
        }

        [Test]
        public void TryGetAndRemove_FindPlantsById_AfterOthersHaveDied()
        {
            Species shortLived = TestSpecies.Tree();
            shortLived.LifespanSteps = 5;
            Species longLived = TestSpecies.Tree();
            longLived.Name = "Chêne";
            var ecosystem = new Ecosystem(MeadowGenerator.CreateWorld(2), new[] { shortLived, longLived }, Seed);
            ecosystem.TryPlant(0, 4, 4);
            ecosystem.TryPlant(1, 8, 8);
            ecosystem.TryPlant(0, 12, 12);
            ecosystem.TryPlant(1, 16, 16);
            Assert.AreEqual(4, ecosystem.Plants.Count);
            int firstOak = ecosystem.Plants[1].Id;
            int secondOak = ecosystem.Plants[3].Id;

            // Les deux arbres à vie courte meurent : les chênes changent de place dans le tableau.
            for (int i = 0; i < 6; i++) ecosystem.Step();

            Assert.AreEqual(2, ecosystem.Plants.Count);
            Assert.IsTrue(ecosystem.TryGet(secondOak, out Plant found));
            Assert.AreEqual(16, found.X);
            Assert.IsTrue(ecosystem.Remove(firstOak));
            Assert.IsTrue(ecosystem.TryGet(secondOak, out found));
            Assert.AreEqual(16, found.X);
        }

        [Test]
        public void Step_PlantDiesOfOldAge_AndFreesItsColumn()
        {
            Species shortLived = TestSpecies.Tree();
            shortLived.LifespanSteps = 10;
            var ecosystem = new Ecosystem(MeadowGenerator.CreateWorld(2), new[] { shortLived }, Seed);
            ecosystem.TryPlant(0, 10, 10);

            Run(ecosystem, 10);
            Assert.AreEqual(1, ecosystem.Plants.Count);
            ecosystem.Step();

            Assert.AreEqual(0, ecosystem.Plants.Count);
            Assert.IsTrue(ecosystem.TryPlant(0, 10, 10));
        }

        [Test]
        public void Remove_FellsThePlant_AndUnknownIdsAreIgnored()
        {
            var ecosystem = new Ecosystem(MeadowGenerator.CreateWorld(2), TestSpecies.TreeOnly(), Seed);
            ecosystem.TryPlant(0, 10, 10);
            ecosystem.TryPlant(0, 20, 20);
            int felled = ecosystem.Plants[0].Id;
            int standing = ecosystem.Plants[1].Id;
            var removed = new List<int>();
            ecosystem.PlantRemoved += removed.Add;

            Assert.IsTrue(ecosystem.Remove(felled));
            Assert.IsFalse(ecosystem.Remove(felled));
            Assert.IsFalse(ecosystem.Remove(9999));

            Assert.IsFalse(ecosystem.TryGet(felled, out _));
            Assert.IsTrue(ecosystem.TryGet(standing, out Plant survivor));
            Assert.AreEqual(20, survivor.X);
            CollectionAssert.AreEqual(new[] { felled }, removed);
        }

        [Test]
        public void ForestLeftAlone_Spreads()
        {
            var ecosystem = new Ecosystem(MeadowGenerator.CreateWorld(4), TestSpecies.TreeOnly(), Seed);
            ecosystem.TryPlant(0, 32, 32);

            Run(ecosystem, 25 * SimClock.StepsPerDay);

            Assert.That(ecosystem.Plants.Count, Is.GreaterThan(20));
            int farthest = ecosystem.Plants.Max(plant => System.Math.Max(System.Math.Abs(plant.X - 32), System.Math.Abs(plant.Z - 32)));
            Assert.That(farthest, Is.GreaterThan(TestSpecies.Tree().SeedRange), "la forêt dépasse la portée d'une seule graine");
        }

        [Test]
        public void MatureForest_IsLimitedBySoilFertility_ButSurvives()
        {
            World world = MeadowGenerator.CreateWorld(2);
            var ecosystem = new Ecosystem(world, TestSpecies.TreeOnly(), Seed);
            float fertilityBefore = AverageFertility(ecosystem, world);
            ecosystem.Populate(40);

            Run(ecosystem, 120 * SimClock.StepsPerDay);

            int cells = world.SizeX / Ecosystem.CellSize * (world.SizeZ / Ecosystem.CellSize);
            Assert.That(ecosystem.Plants.Count, Is.GreaterThan(cells / 2), "la forêt occupe le terrain");
            Assert.That(ecosystem.Plants.Count, Is.LessThan(cells * 4), "moins que ce que l'espacement seul permettrait");
            Assert.That(AverageFertility(ecosystem, world), Is.LessThan(fertilityBefore), "la forêt puise dans le sol");
        }

        [Test]
        public void OverharvestedForest_Disappears_AndDoesNotComeBack()
        {
            var ecosystem = new Ecosystem(MeadowGenerator.CreateWorld(2), TestSpecies.TreeOnly(), Seed);
            ecosystem.Populate(40);
            Run(ecosystem, 10 * SimClock.StepsPerDay);
            Assert.That(ecosystem.Plants.Count, Is.GreaterThan(0));

            // Chaque jour, on coupe tout ce qui a atteint la moitié de sa taille : rien n'a le temps de semer.
            for (int day = 0; day < 20; day++)
            {
                foreach (int id in ecosystem.Plants.Where(plant => plant.Growth >= 0.5f).Select(plant => plant.Id).ToList())
                {
                    ecosystem.Remove(id);
                }

                Run(ecosystem, SimClock.StepsPerDay);
            }

            Assert.AreEqual(0, ecosystem.Plants.Count);
            Run(ecosystem, 30 * SimClock.StepsPerDay);
            Assert.AreEqual(0, ecosystem.Plants.Count);
        }

        [Test]
        public void SameSeedAndSameActions_GiveTheSameForest()
        {
            Ecosystem first = CreatePopulated();
            Ecosystem second = CreatePopulated();

            foreach (Ecosystem ecosystem in new[] { first, second })
            {
                Run(ecosystem, 500);
                ecosystem.Remove(ecosystem.Plants[0].Id);
                Run(ecosystem, 1500);
            }

            AssertSameState(first, second);
        }

        [Test]
        public void StepsRunFastOrSlow_GiveTheSameForest()
        {
            const int totalSteps = 2000;
            Ecosystem accelerated = CreatePopulated();
            Ecosystem realTime = CreatePopulated();

            // Très accéléré, à deux images par seconde.
            var fastClock = new SimClock { Multiplier = 1000f };
            while (accelerated.StepCount < totalSteps)
            {
                fastClock.Advance(0.5f);
                while (accelerated.StepCount < totalSteps && fastClock.TryTakeStep()) accelerated.Step();
            }

            // Peu accéléré, à soixante images par seconde.
            var slowClock = new SimClock { Multiplier = 10f };
            while (realTime.StepCount < totalSteps)
            {
                slowClock.Advance(1f / 60f);
                while (realTime.StepCount < totalSteps && slowClock.TryTakeStep()) realTime.Step();
            }

            AssertSameState(accelerated, realTime);
        }

        [Test]
        public void WriteThenRead_ResumesExactlyWhereItStopped()
        {
            World world = MeadowGenerator.CreateWorld(2);
            var original = new Ecosystem(world, TestSpecies.TreeAndBush(), Seed);
            original.Populate(40);
            Run(original, 500);

            var stream = new MemoryStream();
            original.Write(stream);
            stream.Position = 0;
            Ecosystem loaded = Ecosystem.Read(stream, world, TestSpecies.TreeAndBush());

            AssertSameState(original, loaded);
            Run(original, 1000);
            Run(loaded, 1000);
            AssertSameState(original, loaded);
        }

        [Test]
        public void Read_SaveFromOtherSpeciesOrOtherWorld_Throws()
        {
            World world = MeadowGenerator.CreateWorld(2);
            var ecosystem = new Ecosystem(world, TestSpecies.TreeAndBush(), Seed);
            var stream = new MemoryStream();
            ecosystem.Write(stream);
            byte[] data = stream.ToArray();

            Assert.Throws<InvalidDataException>(() => Ecosystem.Read(new MemoryStream(data), world, TestSpecies.TreeOnly()));
            Assert.Throws<InvalidDataException>(
                () => Ecosystem.Read(new MemoryStream(data), MeadowGenerator.CreateWorld(4), TestSpecies.TreeAndBush()));
            Assert.Throws<InvalidDataException>(() => Ecosystem.Read(new MemoryStream(new byte[64]), world, TestSpecies.TreeAndBush()));
            Assert.Throws<EndOfStreamException>(
                () => Ecosystem.Read(new MemoryStream(data, 0, data.Length / 2), world, TestSpecies.TreeAndBush()));
        }

        private static Ecosystem CreatePopulated()
        {
            var ecosystem = new Ecosystem(MeadowGenerator.CreateWorld(2), TestSpecies.TreeAndBush(), Seed);
            ecosystem.Populate(40);
            return ecosystem;
        }

        private static void Run(Ecosystem ecosystem, int steps)
        {
            for (int i = 0; i < steps; i++)
            {
                ecosystem.Step();
            }
        }

        private static float AverageFertility(Ecosystem ecosystem, World world)
        {
            float total = 0f;
            int cells = 0;
            for (int z = 0; z < world.SizeZ; z += Ecosystem.CellSize)
            {
                for (int x = 0; x < world.SizeX; x += Ecosystem.CellSize)
                {
                    total += ecosystem.GetFertility(x, z);
                    cells++;
                }
            }

            return total / cells;
        }

        private static void AssertSameState(Ecosystem expected, Ecosystem actual)
        {
            Assert.AreEqual(expected.StepCount, actual.StepCount);
            Assert.AreEqual(expected.Plants.Count, actual.Plants.Count);
            for (int i = 0; i < expected.Plants.Count; i++)
            {
                Plant a = expected.Plants[i];
                Plant b = actual.Plants[i];
                Assert.AreEqual((a.Id, a.SpeciesIndex, a.X, a.Y, a.Z, a.Age, a.Growth), (b.Id, b.SpeciesIndex, b.X, b.Y, b.Z, b.Age, b.Growth));
            }

            Assert.AreEqual(expected.GetFertility(0, 0), actual.GetFertility(0, 0));
            Assert.AreEqual(expected.GetFertility(17, 9), actual.GetFertility(17, 9));
        }
    }
}

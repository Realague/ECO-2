using NUnit.Framework;
using Voxel.Core;

namespace Eco.Sim.Tests
{
    // The census behind the map's species layers: how many of each species, and where.
    public class CensusTests
    {
        private const int Seed = 4;

        private static AnimalSpecies Grazer() => new AnimalSpecies
        {
            Name = "Grazer",
            EatsPlants = new[] { 1 },
            MoveRange = 2,
            SenseRange = 4,
            LifespanSteps = 1000,
            MaturitySteps = SimClock.StepsPerDay,
            MealValue = 0.5f,
            Height = 0.6f,
            Width = 0.6f
        };

        private static Ecosystem Create(World world) => new Ecosystem(world, TestSpecies.TreeAndBush(), new[] { Grazer() }, Seed);

        [Test]
        public void Counts_AreGivenPerSpecies()
        {
            Ecosystem ecosystem = Create(MeadowGenerator.CreateWorld(2));
            Assert.IsTrue(ecosystem.TryPlaceGrownPlant(0, 3, 3));
            Assert.IsTrue(ecosystem.TryPlaceGrownPlant(1, 5, 5));
            Assert.IsTrue(ecosystem.TryPlaceGrownPlant(1, 20, 20));
            Assert.IsTrue(ecosystem.TryPlaceAnimal(0, 10, 10));

            CollectionAssert.AreEqual(new[] { 1, 2 }, ecosystem.PlantCounts());
            CollectionAssert.AreEqual(new[] { 1 }, ecosystem.AnimalCounts());
        }

        [Test]
        public void Density_CountsEachSpeciesInItsCell()
        {
            Ecosystem ecosystem = Create(MeadowGenerator.CreateWorld(2));
            Assert.IsTrue(ecosystem.TryPlaceGrownPlant(1, 1, 2));
            Assert.IsTrue(ecosystem.TryPlaceGrownPlant(1, 6, 7));
            Assert.IsTrue(ecosystem.TryPlaceGrownPlant(1, 25, 17));
            Assert.IsTrue(ecosystem.TryPlaceGrownPlant(0, 9, 1));
            Assert.IsTrue(ecosystem.TryPlaceAnimal(0, 30, 2));

            int[] bushes = ecosystem.PlantDensity(1, 8);
            int cellsX = ecosystem.CensusCellsX(8);
            Assert.AreEqual(4, cellsX, "a world of 32 columns, cells of 8");
            Assert.AreEqual(16, bushes.Length);
            Assert.AreEqual(2, bushes[0], "two bushes in the first cell");
            Assert.AreEqual(1, bushes[3 + cellsX * 2]);
            Assert.AreEqual(3, Sum(bushes), "the tree is not counted");

            int[] grazers = ecosystem.AnimalDensity(0, 8);
            Assert.AreEqual(1, grazers[3]);
            Assert.AreEqual(1, Sum(grazers));
        }

        [Test]
        public void Density_KeepsTheLastNarrowerRow_WhenTheCellsDoNotFitTheWorld()
        {
            Ecosystem ecosystem = Create(MeadowGenerator.CreateWorld(2));
            Assert.IsTrue(ecosystem.TryPlaceGrownPlant(1, 31, 31));

            int[] cells = ecosystem.PlantDensity(1, 10);

            Assert.AreEqual(4, ecosystem.CensusCellsX(10));
            Assert.AreEqual(16, cells.Length);
            Assert.AreEqual(1, cells[15], "the corner column falls in the last, narrower cell");
        }

        [Test]
        public void Density_OfAnUnknownSpecies_IsEmpty()
        {
            Ecosystem ecosystem = Create(MeadowGenerator.CreateWorld(2));
            Assert.IsTrue(ecosystem.TryPlaceGrownPlant(1, 4, 4));

            Assert.AreEqual(0, Sum(ecosystem.PlantDensity(7, 8)));
            Assert.AreEqual(0, Sum(ecosystem.AnimalDensity(-1, 8)));
        }

        [Test]
        public void ACensus_ChangesNothingTheSimulationDoes()
        {
            Ecosystem counted = Create(MeadowGenerator.CreateWorld(2));
            Ecosystem untouched = Create(MeadowGenerator.CreateWorld(2));
            counted.Populate(200);
            untouched.Populate(200);
            counted.PopulateAnimals();
            untouched.PopulateAnimals();

            for (int step = 0; step < 300; step++)
            {
                counted.PlantCounts();
                counted.AnimalCounts();
                counted.PlantDensity(1, 8);
                counted.AnimalDensity(0, 8);
                counted.Step();
                untouched.Step();
            }

            Assert.AreEqual(untouched.Plants.Count, counted.Plants.Count);
            Assert.AreEqual(untouched.Animals.Count, counted.Animals.Count);
            CollectionAssert.AreEqual(untouched.PlantCounts(), counted.PlantCounts());
        }

        private static int Sum(int[] cells)
        {
            int sum = 0;
            foreach (int count in cells) sum += count;
            return sum;
        }
    }
}

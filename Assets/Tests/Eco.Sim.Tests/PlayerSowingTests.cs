using NUnit.Framework;
using Voxel.Core;

namespace Eco.Sim.Tests
{
    // Seeds sown by a player (Ecosystem.TryPlantByPlayer): the natural rules without their draw, so a
    // seed either sprouts or says why not.
    public class PlayerSowingTests
    {
        private const int Seed = 7;

        [Test]
        public void ASeedThatSuits_Sprouts_AsASapling()
        {
            var ecosystem = new Ecosystem(MeadowGenerator.CreateWorld(2), TestSpecies.TreeOnly(), Seed);

            Assert.AreEqual(PlantRefusal.None, ecosystem.TryPlantByPlayer(0, 10, 10));
            Assert.AreEqual(1, ecosystem.Plants.Count);
            Assert.AreEqual(0f, ecosystem.Plants[0].Growth);
            Assert.AreEqual(MeadowGenerator.Surface, ecosystem.Plants[0].Y);
        }

        [Test]
        public void TheColumn_MustBeFree_AndOthersOfItsKindFarEnough()
        {
            var ecosystem = new Ecosystem(MeadowGenerator.CreateWorld(2), TestSpecies.TreeOnly(), Seed);
            ecosystem.TryPlantByPlayer(0, 10, 10);

            Assert.AreEqual(PlantRefusal.Occupied, ecosystem.TryPlantByPlayer(0, 10, 10));
            Assert.AreEqual(PlantRefusal.TooClose, ecosystem.TryPlantByPlayer(0, 11, 10), "spacing 2");
            Assert.AreEqual(PlantRefusal.None, ecosystem.TryPlantByPlayer(0, 13, 10));

            ecosystem.BlockColumn(20, 20, true);
            Assert.AreEqual(PlantRefusal.Occupied, ecosystem.TryPlantByPlayer(0, 20, 20), "storage stands there");
            Assert.AreEqual(PlantRefusal.Ground, ecosystem.TryPlantByPlayer(0, -1, 10));
        }

        [Test]
        public void TheClimate_TheSoilAndTheGround_SayWhyNot()
        {
            Species cold = TestSpecies.Tree();
            cold.WarmthMin = 2f;
            cold.WarmthMax = 3f;
            cold.ClimateTolerance = 0f;
            Species hot = TestSpecies.Tree();
            hot.WarmthMin = -3f;
            hot.WarmthMax = -2f;
            hot.ClimateTolerance = 0f;
            Species hungry = TestSpecies.Tree();
            hungry.MinSoil = 1.5f;
            Species sandy = TestSpecies.Tree();
            sandy.Grounds = new[] { Blocks.Sand };
            var ecosystem = new Ecosystem(MeadowGenerator.CreateWorld(2), new[] { cold, hot, hungry, sandy }, Seed);

            Assert.AreEqual(PlantRefusal.TooCold, ecosystem.TryPlantByPlayer(0, 10, 10));
            Assert.AreEqual(PlantRefusal.TooHot, ecosystem.TryPlantByPlayer(1, 10, 10));
            Assert.AreEqual(PlantRefusal.PoorSoil, ecosystem.TryPlantByPlayer(2, 10, 10));
            Assert.AreEqual(PlantRefusal.Ground, ecosystem.TryPlantByPlayer(3, 10, 10));
            Assert.AreEqual(0, ecosystem.Plants.Count, "a refused seed changes nothing");
        }
    }
}

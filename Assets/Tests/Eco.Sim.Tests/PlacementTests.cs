using NUnit.Framework;

namespace Eco.Sim.Tests
{
    // Plants placed grown by a player's command (Ecosystem.TryPlaceGrownPlant).
    public class PlacementTests
    {
        private const int Seed = 7;

        [Test]
        public void GrownPlant_IgnoresTheClimate_ButNeedsAFreeColumn()
        {
            // A tree whose climate exists nowhere: no seed of it ever takes.
            Species tree = TestSpecies.Tree();
            tree.WarmthMin = 2f;
            tree.WarmthMax = 3f;
            tree.ClimateTolerance = 0f;
            var ecosystem = new Ecosystem(MeadowGenerator.CreateWorld(2), new[] { tree }, Seed);

            Assert.IsFalse(ecosystem.TryPlant(0, 10, 10));
            Assert.IsTrue(ecosystem.TryPlaceGrownPlant(0, 10, 10));
            Assert.AreEqual(1, ecosystem.Plants.Count);
            Assert.AreEqual(1f, ecosystem.Plants[0].Growth);

            // One plant per column, and none outside the world.
            Assert.IsFalse(ecosystem.TryPlaceGrownPlant(0, 10, 10));
            Assert.IsFalse(ecosystem.TryPlaceGrownPlant(0, -1, 10));
        }
    }
}

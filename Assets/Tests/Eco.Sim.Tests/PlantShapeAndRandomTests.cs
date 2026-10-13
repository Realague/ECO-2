using System.Numerics;
using NUnit.Framework;

namespace Eco.Sim.Tests
{
    public class PlantShapeTests
    {
        private const float Tolerance = 0.001f;

        [Test]
        public void GetBounds_GrowsWithTheStage_AroundTheCentreOfTheColumn()
        {
            Species tree = TestSpecies.Tree();

            PlantShape.GetBounds(tree, 10, 8, 20, Plant.StageCount - 1, out Vector3 min, out Vector3 max);
            PlantShape.GetBounds(tree, 10, 8, 20, 0, out Vector3 seedlingMin, out Vector3 seedlingMax);

            Assert.AreEqual(new Vector3(10f, 8f, 20f), min);
            Assert.AreEqual(new Vector3(11f, 13.5f, 21f), max);
            Assert.That(seedlingMax.Y - seedlingMin.Y, Is.LessThan(1f));
            Assert.AreEqual(10.5f, (seedlingMin.X + seedlingMax.X) / 2f, Tolerance);
        }

        [Test]
        public void RayDistance_ReturnsTheDistanceToTheBox_OrANegativeValueOnAMiss()
        {
            var min = new Vector3(10f, 8f, 20f);
            var max = new Vector3(11f, 13.5f, 21f);
            var origin = new Vector3(5f, 10f, 20.5f);

            Assert.AreEqual(5f, PlantShape.RayDistance(origin, Vector3.UnitX, min, max), Tolerance);
            Assert.That(PlantShape.RayDistance(origin, -Vector3.UnitX, min, max), Is.LessThan(0f), "box behind");
            Assert.That(PlantShape.RayDistance(origin, Vector3.UnitY, min, max), Is.LessThan(0f), "parallel ray, beside it");
            Assert.AreEqual(0f, PlantShape.RayDistance(new Vector3(10.5f, 9f, 20.5f), Vector3.UnitZ, min, max), Tolerance);
        }

        [Test]
        public void DistanceSquared_IsZeroInside_AndMeasuredToTheNearestFaceOutside()
        {
            var min = new Vector3(10f, 8f, 20f);
            var max = new Vector3(11f, 13.5f, 21f);

            Assert.AreEqual(0f, PlantShape.DistanceSquared(new Vector3(10.5f, 9f, 20.5f), min, max));
            Assert.AreEqual(9f, PlantShape.DistanceSquared(new Vector3(7f, 9f, 20.5f), min, max), Tolerance);
        }

        [Test]
        public void StageOf_CoversEveryStageAndNeverExceedsTheLast()
        {
            Assert.AreEqual(0, Plant.StageOf(0f));
            Assert.AreEqual(4, Plant.StageOf(0.5f));
            Assert.AreEqual(Plant.StageCount - 1, Plant.StageOf(1f));
        }
    }

    public class SimRandomTests
    {
        [Test]
        public void SameSeed_GivesTheSameSequence_AndDifferentSeedsDiffer()
        {
            var first = new SimRandom(42);
            var second = new SimRandom(42);
            var other = new SimRandom(43);

            ulong value = first.NextULong();

            Assert.AreEqual(value, second.NextULong());
            Assert.AreNotEqual(value, other.NextULong());
        }

        [Test]
        public void Values_StayInRange()
        {
            var random = new SimRandom(0);

            for (int i = 0; i < 10000; i++)
            {
                Assert.That(random.NextFloat(), Is.GreaterThanOrEqualTo(0f).And.LessThan(1f));
                Assert.That(random.NextInt(13), Is.GreaterThanOrEqualTo(0).And.LessThan(13));
            }
        }

        [Test]
        public void RestoringTheState_ContinuesTheSequence()
        {
            var random = new SimRandom(5);
            random.NextULong();
            ulong state = random.State;
            ulong expected = random.NextULong();

            var restored = new SimRandom(999) { State = state };

            Assert.AreEqual(expected, restored.NextULong());
        }
    }
}

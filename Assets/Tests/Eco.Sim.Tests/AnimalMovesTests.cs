using System.Collections.Generic;
using NUnit.Framework;

namespace Eco.Sim.Tests
{
    public class AnimalMovesTests
    {
        private const int Floor = 8;

        [TestCase(Floor, true)]
        [TestCase(Floor + 1, true)]
        [TestCase(Floor + 2, false)]
        [TestCase(Floor - 3, true)]
        [TestCase(Floor - 4, false)]
        public void CanStep_ClimbsOneBlockAndDropsThree(int toY, bool expected)
        {
            Assert.AreEqual(expected, AnimalMoves.CanStep(Floor, toY));
        }

        [Test]
        public void TryNextStep_GoesStraightTowardTheTarget()
        {
            var ground = new FakeGround();

            Assert.IsTrue(AnimalMoves.TryNextStep(ground, 5, Floor, 5, 9, 9, out int x, out int y, out int z));

            Assert.AreEqual((6, Floor, 6), (x, y, z));
        }

        [Test]
        public void TryNextStep_ClimbsOneBlock_AndStandsOnIt()
        {
            var ground = new FakeGround();
            ground.Raise(6, 5, 1);

            Assert.IsTrue(AnimalMoves.TryNextStep(ground, 5, Floor, 5, 9, 5, out int x, out int y, out _));

            Assert.AreEqual((6, Floor + 1), (x, y));
        }

        // A single block in the way: a sidestep, since the diagonal would cut its corner, then past it.
        [Test]
        public void TryNextStep_GoesAroundABlock_WithoutCuttingItsCorner()
        {
            var ground = new FakeGround();
            ground.Raise(6, 5, 2);

            Assert.IsTrue(AnimalMoves.TryNextStep(ground, 5, Floor, 5, 9, 5, out int x, out int y, out int z));
            Assert.AreEqual(5, x, "a sidestep");
            Assert.AreEqual(1, System.Math.Abs(z - 5));

            Assert.IsTrue(AnimalMoves.TryNextStep(ground, x, y, z, 9, 5, out x, out _, out z));
            Assert.AreEqual(6, x, "then past the block, beside it");
            Assert.AreNotEqual(5, z);
        }

        [Test]
        public void TryNextStep_NeverTurnsBack()
        {
            var ground = new FakeGround();
            foreach ((int x, int z) in new[] { (6, 4), (6, 5), (6, 6), (5, 4), (5, 6) })
            {
                ground.Raise(x, z, 2);
            }

            Assert.IsFalse(AnimalMoves.TryNextStep(ground, 5, Floor, 5, 9, 5, out _, out _, out _));
        }

        [Test]
        public void TryNextStep_DoesNotSqueezeDiagonallyBetweenTwoWalls()
        {
            var ground = new FakeGround();
            ground.Raise(6, 5, 2);
            ground.Raise(5, 6, 2);

            Assert.IsFalse(AnimalMoves.TryNextStep(ground, 5, Floor, 5, 6, 6, out _, out _, out _));
        }

        [Test]
        public void TryNextStep_DoesNotEnterATakenColumn()
        {
            var ground = new FakeGround();
            foreach ((int x, int z) in new[] { (6, 4), (6, 5), (6, 6), (5, 4), (5, 6) })
            {
                ground.Taken.Add((x, z));
            }

            Assert.IsFalse(AnimalMoves.TryNextStep(ground, 5, Floor, 5, 9, 5, out _, out _, out _));
        }

        [Test]
        public void TryNextStep_AlreadyThere_ReturnsFalse()
        {
            Assert.IsFalse(AnimalMoves.TryNextStep(new FakeGround(), 5, Floor, 5, 5, 5, out _, out _, out _));
        }

        [Test]
        public void TryNextStep_NeverLeavesTheGround()
        {
            if (AnimalMoves.TryNextStep(new FakeGround(), 0, Floor, 5, -3, 5, out int x, out _, out _))
            {
                Assert.That(x, Is.GreaterThanOrEqualTo(0));
            }
        }

        // Flat ground 20 x 20 at Floor, with raised columns and taken columns.
        private sealed class FakeGround : IAnimalGround
        {
            private const int Size = 20;

            private readonly Dictionary<(int, int), int> raised = new Dictionary<(int, int), int>();

            public HashSet<(int, int)> Taken { get; } = new HashSet<(int, int)>();

            public void Raise(int x, int z, int blocks) => raised[(x, z)] = blocks;

            public bool TryGetGround(int x, int z, out int y)
            {
                y = Floor + (raised.TryGetValue((x, z), out int blocks) ? blocks : 0);
                return x >= 0 && x < Size && z >= 0 && z < Size;
            }

            public bool IsFree(int x, int z) => !Taken.Contains((x, z));
        }
    }
}

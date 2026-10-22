using System.Collections.Generic;
using System.Numerics;
using NUnit.Framework;

namespace Voxel.Core.Tests
{
    public class ArrowFlightTests
    {
        [Test]
        public void AnArrowShotLevel_FallsToTheGround_AtTheDistanceGravityAllows()
        {
            World world = FlatGenerator.CreateWorld();
            var origin = new Vector3(1f, FlatGenerator.GroundHeight + 2f, 8.5f);
            Vector3 velocity = ArrowFlight.VelocityOf(new Vector3(1f, 0f, 0f), 1f);

            Assert.IsTrue(ArrowFlight.Trace(world, origin, velocity, out Vector3 landing, out float time));

            // Two blocks of fall at 20 blocks/s² take sqrt(0.2) s, at 30 blocks a second.
            float expected = System.MathF.Sqrt(2f * 2f / ArrowFlight.Gravity);
            Assert.AreEqual(expected, time, 0.02f);
            Assert.AreEqual(FlatGenerator.GroundHeight, landing.Y, 0.01f, "it stops on the ground");
            Assert.AreEqual(1f + ArrowFlight.FullSpeed * expected, landing.X, 0.5f);
        }

        [Test]
        public void AWall_StopsTheArrow_AtItsFace()
        {
            World world = FlatGenerator.CreateWorld();
            for (int y = FlatGenerator.GroundHeight; y < FlatGenerator.GroundHeight + 5; y++)
            {
                world.SetBlock(6, y, 8, Blocks.Dirt);
            }

            var origin = new Vector3(1.5f, FlatGenerator.GroundHeight + 2.5f, 8.5f);
            var path = new List<Vector3>();
            Assert.IsTrue(ArrowFlight.Trace(world, origin, ArrowFlight.VelocityOf(Vector3.UnitX, 1f), out Vector3 landing, out _, path));

            Assert.AreEqual(6f, landing.X, 0.01f);
            Assert.AreEqual(origin, path[0]);
            Assert.AreEqual(landing, path[path.Count - 1]);
        }

        [Test]
        public void AWeakShot_GoesSlower_ButNeverUnderTheLeastPower()
        {
            Assert.AreEqual(ArrowFlight.FullSpeed * 0.5f, ArrowFlight.VelocityOf(Vector3.UnitZ, 0.5f).Length(), 1e-3f);
            Assert.AreEqual(ArrowFlight.FullSpeed * ArrowFlight.MinPower, ArrowFlight.VelocityOf(Vector3.UnitZ, 0f).Length(), 1e-3f);
        }

        [Test]
        public void HitChecks_FindABoxOnTheWay_AndHowFarAPathPassesFromOne()
        {
            var min = new Vector3(4f, 0f, -0.5f);
            var max = new Vector3(5f, 1f, 0.5f);

            Assert.AreEqual(4f, ArrowFlight.SegmentEntry(new Vector3(0f, 0.5f, 0f), new Vector3(10f, 0.5f, 0f), min, max), 1e-4f);
            Assert.AreEqual(-1f, ArrowFlight.SegmentEntry(new Vector3(0f, 3f, 0f), new Vector3(10f, 3f, 0f), min, max));

            var above = new List<Vector3> { new Vector3(0f, 3f, 0f), new Vector3(10f, 3f, 0f) };
            Assert.AreEqual(2f, ArrowFlight.DistanceToBox(above, min, max), 1e-4f);
        }
    }
}

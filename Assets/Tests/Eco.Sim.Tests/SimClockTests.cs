using NUnit.Framework;

namespace Eco.Sim.Tests
{
    public class SimClockTests
    {
        [Test]
        public void AtNormalSpeed_OneStepIsDueEveryTenRealSeconds()
        {
            var clock = new SimClock();

            clock.Advance(9.5f);
            Assert.IsFalse(clock.TryTakeStep());

            clock.Advance(1f);
            Assert.AreEqual(1, TakeAll(clock));
        }

        [Test]
        public void Multiplier_ScalesTheNumberOfStepsDue()
        {
            var clock = new SimClock { Multiplier = 100f };

            clock.Advance(3f);

            Assert.AreEqual(30, TakeAll(clock));
        }

        [Test]
        public void Multiplier_IsClampedBetweenZeroAndTheMaximum()
        {
            var clock = new SimClock { Multiplier = -5f };
            Assert.AreEqual(0f, clock.Multiplier);

            clock.Multiplier = 1e9f;
            Assert.AreEqual(SimClock.MaxMultiplier, clock.Multiplier);
        }

        [Test]
        public void QueueDays_AddsWholeDaysOfSteps_EvenWhenTimeIsPaused()
        {
            var clock = new SimClock { Multiplier = 0f };

            clock.QueueDays(2);
            clock.Advance(1000f);

            Assert.AreEqual(2 * SimClock.StepsPerDay, clock.QueuedSteps);
            Assert.AreEqual(2 * SimClock.StepsPerDay, TakeAll(clock));
            Assert.AreEqual(0, clock.QueuedSteps);
        }

        [Test]
        public void DropBacklog_DiscardsLateSteps_ButKeepsQueuedOnes()
        {
            var clock = new SimClock { Multiplier = 10000f };
            clock.Advance(1f);
            clock.QueueDays(1);
            for (int i = 0; i < SimClock.StepsPerDay + 10; i++)
            {
                Assert.IsTrue(clock.TryTakeStep());
            }

            clock.DropBacklog();

            Assert.IsTrue(clock.IsBehind);
            Assert.IsFalse(clock.TryTakeStep());
            Assert.IsFalse(clock.IsBehind, "up to date as soon as no step is pending");
        }

        [Test]
        public void DropBacklog_WithNothingLate_DoesNotReportBeingBehind()
        {
            var clock = new SimClock();
            clock.Advance(1f);

            clock.DropBacklog();

            Assert.IsFalse(clock.IsBehind);
        }

        private static int TakeAll(SimClock clock)
        {
            int steps = 0;
            while (clock.TryTakeStep()) steps++;
            return steps;
        }
    }
}

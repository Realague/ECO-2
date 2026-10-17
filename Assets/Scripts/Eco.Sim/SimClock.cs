using System;

namespace Eco.Sim
{
    // Decides how many simulation steps to run. The simulation advances in fixed steps of simulated
    // time and never reads real time: speeding time up means running more steps per second,
    // and the result only depends on the number of steps.
    public sealed class SimClock
    {
        public const int MinutesPerStep = 10;
        public const int StepsPerDay = 24 * 60 / MinutesPerStep;
        public const int MinutesPerDay = 24 * 60;

        // A new simulation starts on day 1 at 8 in the morning, not at midnight.
        public const int StartMinute = 8 * 60;

        // Game time after this many steps, in minutes since midnight of day 1. Display only: what a
        // step does never depends on the time of day.
        public static double MinutesAt(double steps) => StartMinute + steps * MinutesPerStep;

        // At speed 1, one game minute lasts one real second.
        public const float RealSecondsPerStep = 10f;
        public const float MaxMultiplier = 10000f;

        private float multiplier = 1f;
        private double pendingSteps;

        public float Multiplier
        {
            get => multiplier;
            set => multiplier = Math.Min(Math.Max(value, 0f), MaxMultiplier);
        }

        // Steps requested explicitly ("advance N days"): they always run, as fast as the machine
        // allows.
        public int QueuedSteps { get; private set; }

        // True when the machine cannot keep up with the requested multiplier.
        public bool IsBehind { get; private set; }

        public void Advance(float realSeconds)
        {
            pendingSteps += realSeconds * multiplier / RealSecondsPerStep;
        }

        public void QueueDays(int days)
        {
            if (days > 0) QueuedSteps += days * StepsPerDay;
        }

        // Returns true if there is a step to run now, and counts it off.
        public bool TryTakeStep()
        {
            if (QueuedSteps > 0)
            {
                QueuedSteps--;
                return true;
            }

            if (pendingSteps >= 1.0)
            {
                pendingSteps -= 1.0;
                return true;
            }

            IsBehind = false;
            return false;
        }

        // To call when the computing time granted to this frame is used up: the backlog caused by the
        // multiplier is dropped rather than accumulated, otherwise the server would never catch up.
        public void DropBacklog()
        {
            if (pendingSteps < 1.0) return;

            pendingSteps -= Math.Floor(pendingSteps);
            IsBehind = true;
        }
    }
}

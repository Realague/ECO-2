using System;

namespace Voxel.Core
{
    // Keeps a player's hits to one per swing over time, as the server sees them arrive: a hit is
    // accepted if it comes no earlier than a swing before its turn, so two hits bunched together by
    // the network still count, but clicking faster never hits faster than one a swing.
    public struct HitPacer
    {
        // When the next hit is due, in the caller's clock (seconds).
        private double due;

        public bool TryHit(double now, double interval)
        {
            if (now < due - interval) return false;

            due = Math.Max(due, now) + interval;
            return true;
        }
    }
}

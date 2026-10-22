using System;
using System.Collections.Generic;
using System.Numerics;

namespace Voxel.Core
{
    // How an arrow flies: shot at up to 30 blocks a second (by how far the bow was drawn), it falls
    // under gravity and stops at the first solid block. The server and every client follow the same
    // steps, so where it lands is worked out alike; the server's is the one that counts.
    public static class ArrowFlight
    {
        public const float FullSpeed = 30f;
        public const float MinPower = 0.3f;
        public const float Gravity = 20f;
        public const float TimeStep = 1f / 60f;
        public const float MaxTime = 4f;

        // An arrow flies no further than this from where it was shot, whatever happens.
        public const float MaxRange = FullSpeed * MaxTime;

        public static float ClampPower(float power) => Math.Max(MinPower, Math.Min(1f, power));

        public static Vector3 VelocityOf(Vector3 direction, float power)
        {
            float length = direction.Length();
            if (length < 1e-6f) return Vector3.Zero;

            return direction / length * FullSpeed * ClampPower(power);
        }

        // Where the arrow is t seconds after leaving origin, ignoring blocks.
        public static Vector3 PositionAt(Vector3 origin, Vector3 velocity, float t)
        {
            return origin + velocity * t - new Vector3(0f, 0.5f * Gravity * t * t, 0f);
        }

        public static Vector3 VelocityAt(Vector3 velocity, float t) => velocity - new Vector3(0f, Gravity * t, 0f);

        // Follows the flight step by step until a solid block stops it (true), or until MaxTime
        // (false: it flew away). landing is where it stopped, time how long it flew. Each step's end
        // point is added to path when one is given (with origin first), for hit checks.
        public static bool Trace(World world, Vector3 origin, Vector3 velocity, out Vector3 landing, out float time, List<Vector3> path = null)
        {
            path?.Add(origin);
            Vector3 from = origin;
            for (int step = 1; step * TimeStep <= MaxTime + 1e-4f; step++)
            {
                float t = step * TimeStep;
                Vector3 to = PositionAt(origin, velocity, t);
                Vector3 segment = to - from;
                float length = segment.Length();
                if (length > 1e-6f && VoxelRaycast.Cast(world, from, segment, length, out VoxelHit hit))
                {
                    landing = from + segment / length * hit.Distance;
                    time = t - TimeStep + TimeStep * hit.Distance / length;
                    path?.Add(landing);
                    return true;
                }

                path?.Add(to);
                from = to;
            }

            landing = from;
            time = MaxTime;
            return false;
        }

        // Distance along the segment from a to b at which it enters the box, or -1 if it misses it.
        public static float SegmentEntry(Vector3 a, Vector3 b, Vector3 min, Vector3 max)
        {
            Vector3 d = b - a;
            float enter = 0f;
            float exit = 1f;
            if (!Slab(a.X, d.X, min.X, max.X, ref enter, ref exit)) return -1f;
            if (!Slab(a.Y, d.Y, min.Y, max.Y, ref enter, ref exit)) return -1f;
            if (!Slab(a.Z, d.Z, min.Z, max.Z, ref enter, ref exit)) return -1f;
            return enter * d.Length();
        }

        // The shortest distance between a path of points and a box (0 if the path goes through it).
        public static float DistanceToBox(IReadOnlyList<Vector3> path, Vector3 min, Vector3 max)
        {
            float best = float.MaxValue;
            for (int i = 1; i < path.Count; i++)
            {
                if (SegmentEntry(path[i - 1], path[i], min, max) >= 0f) return 0f;

                // Close enough for short segments: the nearer of the end points and the middle.
                best = Math.Min(best, PointToBox((path[i - 1] + path[i]) * 0.5f, min, max));
                best = Math.Min(best, PointToBox(path[i], min, max));
            }

            return path.Count == 1 ? PointToBox(path[0], min, max) : best;
        }

        private static float PointToBox(Vector3 p, Vector3 min, Vector3 max)
        {
            return (Vector3.Clamp(p, min, max) - p).Length();
        }

        private static bool Slab(float start, float delta, float min, float max, ref float enter, ref float exit)
        {
            if (Math.Abs(delta) < 1e-9f) return start >= min && start <= max;

            float t0 = (min - start) / delta;
            float t1 = (max - start) / delta;
            if (t0 > t1) (t0, t1) = (t1, t0);
            enter = Math.Max(enter, t0);
            exit = Math.Min(exit, t1);
            return enter <= exit;
        }
    }
}

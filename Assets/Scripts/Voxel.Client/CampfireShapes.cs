using System.Collections.Generic;
using UnityEngine;

namespace Voxel.Client
{
    // The boxes of a campfire (centre, size), its foot at the origin in the middle of its cell: a ring
    // of eight stones, two layers of logs crossed over the middle, and the flame (one box, scaled while
    // it burns). Used to draw placed campfires (StorageViews) and the campfire's icon (ItemIconBuilder).
    // No two boxes have faces in one plane.
    public static class CampfireShapes
    {
        private const float Ring = 0.36f;

        public static readonly List<(Vector3 centre, Vector3 size)> Stones = RingOfStones();

        public static readonly List<(Vector3 centre, Vector3 size)> Logs = new List<(Vector3, Vector3)>
        {
            (new Vector3(-0.09f, 0.055f, 0f), new Vector3(0.1f, 0.1f, 0.6f)),
            (new Vector3(0.1f, 0.06f, 0.01f), new Vector3(0.11f, 0.11f, 0.56f)),
            (new Vector3(0.01f, 0.155f, -0.08f), new Vector3(0.58f, 0.095f, 0.1f)),
            (new Vector3(-0.01f, 0.16f, 0.1f), new Vector3(0.54f, 0.105f, 0.11f))
        };

        // The flame's box, its foot on the logs: drawn scaled about its foot.
        public static readonly (Vector3 centre, Vector3 size) Flame = (new Vector3(0f, 0.17f, 0f), new Vector3(0.22f, 0.34f, 0.22f));

        private static List<(Vector3, Vector3)> RingOfStones()
        {
            var stones = new List<(Vector3, Vector3)>();
            for (int i = 0; i < 8; i++)
            {
                float angle = i * Mathf.PI / 4f + 0.2f;
                float size = 0.13f + (i % 3) * 0.015f;
                float height = 0.09f + (i % 2) * 0.025f;
                stones.Add((new Vector3(Mathf.Cos(angle) * Ring, height * 0.5f, Mathf.Sin(angle) * Ring), new Vector3(size, height, size * 0.9f)));
            }

            return stones;
        }
    }
}

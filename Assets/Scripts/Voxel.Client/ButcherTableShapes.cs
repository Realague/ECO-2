using System.Collections.Generic;
using UnityEngine;

namespace Voxel.Client
{
    // The boxes of a butcher's table (centre, size), its foot at the origin in the middle of its cell: a
    // thick top on four legs joined by a stretcher, and a cleaver lying on it (an iron blade, a wooden
    // handle). Used to draw placed tables (StorageViews) and the table's icon (ItemIconBuilder). No two
    // boxes have faces in one plane.
    public static class ButcherTableShapes
    {
        public static readonly List<(Vector3 centre, Vector3 size)> Wood = new List<(Vector3, Vector3)>
        {
            (new Vector3(0f, 0.76f, 0f), new Vector3(0.84f, 0.08f, 0.72f)),
            (new Vector3(-0.34f, 0.365f, -0.28f), new Vector3(0.08f, 0.73f, 0.08f)),
            (new Vector3(0.34f, 0.365f, -0.28f), new Vector3(0.081f, 0.73f, 0.081f)),
            (new Vector3(-0.34f, 0.365f, 0.28f), new Vector3(0.082f, 0.73f, 0.082f)),
            (new Vector3(0.34f, 0.365f, 0.28f), new Vector3(0.083f, 0.73f, 0.083f)),
            (new Vector3(0f, 0.2f, 0f), new Vector3(0.66f, 0.055f, 0.05f)),

            // The cleaver's handle.
            (new Vector3(0.2f, 0.818f, 0.1f), new Vector3(0.15f, 0.032f, 0.034f))
        };

        public static readonly List<(Vector3 centre, Vector3 size)> Iron = new List<(Vector3, Vector3)>
        {
            (new Vector3(0.03f, 0.808f, 0.12f), new Vector3(0.2f, 0.013f, 0.13f))
        };
    }
}

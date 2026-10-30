using System.Collections.Generic;
using UnityEngine;

namespace Voxel.Client
{
    // The boxes of a carpentry table (centre, size), its foot at the origin in the middle of its cell: a
    // long workbench on four legs with a shelf, a vice at one end, a plank laid on it, a saw (iron blade,
    // wooden handle) and a plane. Used to draw placed tables (StorageViews) and the table's icon
    // (ItemIconBuilder). No two boxes have faces in one plane.
    public static class CarpentryTableShapes
    {
        public static readonly List<(Vector3 centre, Vector3 size)> Wood = new List<(Vector3, Vector3)>
        {
            (new Vector3(0f, 0.74f, 0f), new Vector3(0.92f, 0.09f, 0.62f)),
            (new Vector3(-0.38f, 0.35f, -0.24f), new Vector3(0.09f, 0.7f, 0.09f)),
            (new Vector3(0.38f, 0.35f, -0.24f), new Vector3(0.091f, 0.7f, 0.091f)),
            (new Vector3(-0.38f, 0.35f, 0.24f), new Vector3(0.092f, 0.7f, 0.092f)),
            (new Vector3(0.38f, 0.35f, 0.24f), new Vector3(0.093f, 0.7f, 0.093f)),
            (new Vector3(0f, 0.18f, 0f), new Vector3(0.72f, 0.04f, 0.44f)),

            // The vice's jaw at the left end, a plank laid across, the saw's handle and the plane's body.
            (new Vector3(-0.43f, 0.72f, -0.29f), new Vector3(0.12f, 0.16f, 0.07f)),
            (new Vector3(0.08f, 0.805f, -0.05f), new Vector3(0.5f, 0.035f, 0.16f)),
            (new Vector3(0.31f, 0.81f, 0.2f), new Vector3(0.09f, 0.045f, 0.06f)),
            (new Vector3(-0.22f, 0.83f, 0.18f), new Vector3(0.16f, 0.07f, 0.07f))
        };

        public static readonly List<(Vector3 centre, Vector3 size)> Iron = new List<(Vector3, Vector3)>
        {
            // The saw's blade, the plane's blade on top, the vice's screw.
            (new Vector3(0.12f, 0.792f, 0.2f), new Vector3(0.3f, 0.012f, 0.09f)),
            (new Vector3(-0.22f, 0.871f, 0.18f), new Vector3(0.04f, 0.015f, 0.05f)),
            (new Vector3(-0.43f, 0.72f, -0.34f), new Vector3(0.03f, 0.03f, 0.05f))
        };
    }
}

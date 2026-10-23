using System.Collections.Generic;
using UnityEngine;

namespace Voxel.Client
{
    // The boxes of a chest (centre, size), its front toward +z and its foot at the origin: a body of
    // planks with two iron bands, and a lid hinged along the body's top back edge (its boxes given from
    // the hinge) with its bands and a clasp in front. Used to draw placed chests (StorageViews) and the
    // chest's icon (ItemIconBuilder). No two boxes have faces in one plane.
    public static class ChestShapes
    {
        private const float Width = 0.8f;
        private const float Depth = 0.6f;
        private const float BodyHeight = 0.52f;
        private const float LidHeight = 0.22f;
        private const float BandWidth = 0.06f;
        private const float BandX = 0.25f;

        // Where the lid turns, at the top of the body's back.
        public static readonly Vector3 Hinge = new Vector3(0f, BodyHeight, -Depth * 0.5f);

        public static readonly List<(Vector3 centre, Vector3 size)> BodyWood = new List<(Vector3, Vector3)>
        {
            (new Vector3(0f, BodyHeight * 0.5f, 0f), new Vector3(Width, BodyHeight, Depth))
        };

        public static readonly List<(Vector3 centre, Vector3 size)> BodyIron = new List<(Vector3, Vector3)>
        {
            (new Vector3(-BandX, BodyHeight * 0.5f - 0.005f, 0f), new Vector3(BandWidth, BodyHeight + 0.02f, Depth + 0.02f)),
            (new Vector3(BandX, BodyHeight * 0.5f - 0.005f, 0f), new Vector3(BandWidth, BodyHeight + 0.02f, Depth + 0.02f))
        };

        public static readonly List<(Vector3 centre, Vector3 size)> LidWood = new List<(Vector3, Vector3)>
        {
            (new Vector3(0f, LidHeight * 0.5f, Depth * 0.5f), new Vector3(Width + 0.02f, LidHeight, Depth + 0.02f))
        };

        public static readonly List<(Vector3 centre, Vector3 size)> LidIron = new List<(Vector3, Vector3)>
        {
            (new Vector3(-BandX, LidHeight * 0.5f + 0.004f, Depth * 0.5f), new Vector3(BandWidth + 0.006f, LidHeight + 0.018f, Depth + 0.044f)),
            (new Vector3(BandX, LidHeight * 0.5f + 0.004f, Depth * 0.5f), new Vector3(BandWidth + 0.006f, LidHeight + 0.018f, Depth + 0.044f)),
            (new Vector3(0f, 0.04f, Depth + 0.035f), new Vector3(0.12f, 0.15f, 0.05f))
        };
    }
}

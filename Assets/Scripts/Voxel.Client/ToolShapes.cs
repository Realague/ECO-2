using System;
using System.Collections.Generic;
using UnityEngine;
using Voxel.Core;

namespace Voxel.Client
{
    // The shape of each tool and of the arrow, as boxes in the tool's own space: the handle standing
    // along +y from the ground (y = 0), the working end on top, facing +x. They draw the tool in a
    // player's hand (PlayerModel); the inventory icons are painted apart (ToolIconPainter).
    public static class ToolShapes
    {
        public enum Stuff { Wood, Iron, Light }

        public readonly struct Box
        {
            public readonly Stuff Stuff;
            public readonly Vector3 Centre;
            public readonly Vector3 Size;

            public Box(Stuff stuff, float x, float y, float z, float width, float height, float depth)
            {
                Stuff = stuff;
                Centre = new Vector3(x, y, z);
                Size = new Vector3(width, height, depth);
            }
        }

        // Where a hand holds the tool, up its handle (a knife's handle is short: held by its middle).
        public const float GripHeight = 0.3f;
        public const float KnifeGripHeight = 0.07f;

        public static float GripOf(int item) => item == Items.Knife ? KnifeGripHeight : GripHeight;

        private static readonly Box[] Shovel =
        {
            new Box(Stuff.Wood, 0f, 0.75f, 0f, 0.07f, 1f, 0.07f),
            new Box(Stuff.Wood, 0f, 1.27f, 0f, 0.26f, 0.06f, 0.06f),
            new Box(Stuff.Iron, 0f, 0.13f, 0f, 0.32f, 0.38f, 0.05f)
        };

        private static readonly Box[] Pickaxe =
        {
            new Box(Stuff.Wood, 0f, 0.55f, 0f, 0.07f, 1.1f, 0.07f),
            new Box(Stuff.Iron, 0f, 1.1f, 0f, 0.8f, 0.1f, 0.1f),
            new Box(Stuff.Iron, -0.46f, 1.06f, 0f, 0.14f, 0.07f, 0.07f),
            new Box(Stuff.Iron, 0.46f, 1.06f, 0f, 0.14f, 0.07f, 0.07f)
        };

        private static readonly Box[] Axe =
        {
            new Box(Stuff.Wood, 0f, 0.5f, 0f, 0.07f, 1f, 0.07f),
            new Box(Stuff.Iron, 0.19f, 0.9f, 0f, 0.3f, 0.32f, 0.05f),
            new Box(Stuff.Iron, -0.07f, 0.94f, 0f, 0.12f, 0.12f, 0.1f)
        };

        private static readonly Box[] Scythe =
        {
            new Box(Stuff.Wood, 0f, 0.6f, 0f, 0.06f, 1.2f, 0.06f),
            new Box(Stuff.Wood, 0.1f, 0.72f, 0f, 0.2f, 0.05f, 0.05f),
            new Box(Stuff.Iron, -0.38f, 1.16f, 0f, 0.78f, 0.08f, 0.03f)
        };

        private static readonly Box[] Hoe =
        {
            new Box(Stuff.Wood, 0f, 0.55f, 0f, 0.06f, 1.1f, 0.06f),
            new Box(Stuff.Iron, 0f, 1.08f, 0.12f, 0.3f, 0.05f, 0.26f)
        };

        private static readonly Box[] Bow =
        {
            new Box(Stuff.Wood, 0f, 0.6f, 0f, 0.07f, 0.42f, 0.07f),
            new Box(Stuff.Wood, -0.08f, 0.96f, 0f, 0.07f, 0.34f, 0.07f),
            new Box(Stuff.Wood, -0.08f, 0.24f, 0f, 0.07f, 0.34f, 0.07f),
            new Box(Stuff.Wood, -0.16f, 1.16f, 0f, 0.06f, 0.1f, 0.06f),
            new Box(Stuff.Wood, -0.16f, 0.04f, 0f, 0.06f, 0.1f, 0.06f),
            new Box(Stuff.Light, -0.2f, 0.6f, 0f, 0.014f, 1.14f, 0.014f)
        };

        // A short handle, a guard, and a blade with its edge toward +x.
        private static readonly Box[] Knife =
        {
            new Box(Stuff.Wood, 0f, 0.07f, 0f, 0.045f, 0.14f, 0.04f),
            new Box(Stuff.Iron, 0f, 0.15f, 0f, 0.085f, 0.022f, 0.05f),
            new Box(Stuff.Iron, 0.008f, 0.29f, 0f, 0.06f, 0.26f, 0.012f),
            new Box(Stuff.Iron, 0.002f, 0.44f, 0f, 0.035f, 0.05f, 0.011f)
        };

        private static readonly Box[] Arrow =
        {
            new Box(Stuff.Wood, 0f, 0.55f, 0f, 0.04f, 1f, 0.04f),
            new Box(Stuff.Iron, 0f, 1.1f, 0f, 0.1f, 0.14f, 0.1f),
            new Box(Stuff.Light, 0f, 0.1f, 0f, 0.18f, 0.16f, 0.012f),
            new Box(Stuff.Light, 0f, 0.1f, 0f, 0.012f, 0.16f, 0.18f)
        };

        // The boxes of a tool or of the arrow; none for anything else.
        public static IReadOnlyList<Box> Of(int item)
        {
            switch (item)
            {
                case Items.Shovel: return Shovel;
                case Items.Pickaxe: return Pickaxe;
                case Items.Axe: return Axe;
                case Items.Scythe: return Scythe;
                case Items.Hoe: return Hoe;
                case Items.Bow: return Bow;
                case Items.Knife: return Knife;
                case Items.Arrow: return Arrow;
                default: return Array.Empty<Box>();
            }
        }
    }
}

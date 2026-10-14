using UnityEngine;

namespace Voxel.Client
{
    // Texture of the player model, generated at runtime like the block atlas: one tile per kind of
    // face, drawn by simple rules, then shaded (darker towards the bottom and along the edges) so that
    // each box reads as a solid piece. Only the shirt colour differs from one player to another.
    //
    // Tile coordinates: u goes from left to right as the face is seen from outside, v from bottom to
    // top. On side faces PlayerModel orients u from the back (0) to the front (1) on both sides.
    internal static class PlayerSkin
    {
        public enum Tile
        {
            Skin,
            SkinShadow,
            Face,
            HeadSide,
            HeadBack,
            Hair,
            HairEdge,
            ChestFront,
            ChestSide,
            ChestBack,
            Shirt,
            Sleeve,
            Forearm,
            Hand,
            Fingers,
            BeltFront,
            Belt,
            TrouserFront,
            TrouserSide,
            Trousers,
            ShinFront,
            ShinSide,
            ShoeSide,
            ShoeTop,
            ShoeFront,
            Sole,
            PackBack,
            PackSide,
            PackTop,
            Canvas,
            Strap
        }

        private const int TileSize = 32;
        private const int Columns = 8;
        private const int TileCount = (int)Tile.Strap + 1;
        private const int Rows = (TileCount + Columns - 1) / Columns;

        private static readonly Color SkinColour = new Color(0.87f, 0.67f, 0.52f);
        private static readonly Color HairColour = new Color(0.32f, 0.20f, 0.11f);
        private static readonly Color EyeWhite = new Color(0.96f, 0.96f, 0.94f);
        private static readonly Color Iris = new Color(0.24f, 0.45f, 0.36f);
        private static readonly Color Pupil = new Color(0.08f, 0.08f, 0.10f);
        private static readonly Color Lips = new Color(0.70f, 0.42f, 0.36f);
        private static readonly Color Blush = new Color(0.92f, 0.55f, 0.50f);
        private static readonly Color TrouserColour = new Color(0.24f, 0.28f, 0.40f);
        private static readonly Color Leather = new Color(0.40f, 0.25f, 0.14f);
        private static readonly Color DarkLeather = new Color(0.26f, 0.16f, 0.09f);
        private static readonly Color SoleColour = new Color(0.16f, 0.14f, 0.13f);
        private static readonly Color Lace = new Color(0.82f, 0.76f, 0.62f);
        private static readonly Color Metal = new Color(0.78f, 0.66f, 0.32f);
        private static readonly Color CanvasColour = new Color(0.47f, 0.44f, 0.29f);
        private static readonly Color Button = new Color(0.90f, 0.88f, 0.82f);

        // Shirt colours handed out in turn, so that players in the same game look different.
        private static readonly Color[] Shirts =
        {
            new Color(0.72f, 0.22f, 0.20f),
            new Color(0.22f, 0.50f, 0.76f),
            new Color(0.30f, 0.58f, 0.28f),
            new Color(0.84f, 0.64f, 0.20f),
            new Color(0.52f, 0.30f, 0.66f),
            new Color(0.88f, 0.48f, 0.20f),
            new Color(0.20f, 0.62f, 0.60f),
            new Color(0.80f, 0.80f, 0.78f)
        };

        public static Color ShirtFor(ulong clientId) => Shirts[(int)(clientId % (ulong)Shirts.Length)];

        public static Texture2D CreateTexture(Color shirt)
        {
            int width = Columns * TileSize;
            var texture = new Texture2D(width, Rows * TileSize, TextureFormat.RGBA32, false)
            {
                name = "PlayerSkin",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };

            var pixels = new Color32[width * Rows * TileSize];
            for (int tile = 0; tile < TileCount; tile++)
            {
                int left = tile % Columns * TileSize;
                int bottom = tile / Columns * TileSize;
                for (int y = 0; y < TileSize; y++)
                {
                    for (int x = 0; x < TileSize; x++)
                    {
                        float u = (x + 0.5f) / TileSize;
                        float v = (y + 0.5f) / TileSize;
                        Color color = Shade(BaseColor((Tile)tile, u, v, shirt), tile, x, y, v);
                        color.a = 1f;
                        pixels[(bottom + y) * width + left + x] = color;
                    }
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            return texture;
        }

        // Texture coordinates of a tile, inset by half a texel so that neighbouring tiles never bleed in.
        public static Rect UvRect(Tile tile)
        {
            float width = Columns * TileSize;
            float height = Rows * TileSize;
            int column = (int)tile % Columns;
            int row = (int)tile / Columns;
            return Rect.MinMaxRect(
                (column * TileSize + 0.5f) / width,
                (row * TileSize + 0.5f) / height,
                ((column + 1) * TileSize - 0.5f) / width,
                ((row + 1) * TileSize - 0.5f) / height);
        }

        // Light from above, a darker outline, and a little grain so that flat colours look like fabric.
        private static Color Shade(Color color, int tile, int x, int y, float v)
        {
            bool edge = x == 0 || y == 0 || x == TileSize - 1 || y == TileSize - 1;
            float light = (0.84f + 0.16f * v) * (edge ? 0.82f : 1f);
            return color * light * (0.94f + 0.06f * Noise(tile, x, y));
        }

        private static Color BaseColor(Tile tile, float u, float v, Color shirt)
        {
            switch (tile)
            {
                case Tile.Skin:
                    return SkinColour;
                case Tile.SkinShadow:
                    return SkinColour * 0.82f;
                case Tile.Face:
                    return Face(u, v);
                case Tile.HeadSide:
                    return HeadSide(u, v);
                case Tile.HeadBack:
                    return v > 0.15f ? Strands(u, v) : SkinColour * 0.9f;
                case Tile.Hair:
                    return Strands(u, v);
                case Tile.HairEdge:
                    return Strands(u, v) * (v < 0.3f ? 0.8f : 1f);
                case Tile.ChestFront:
                    return ChestFront(u, v, shirt);
                case Tile.ChestSide:
                    return Mathf.Abs(u - 0.5f) < 0.025f ? shirt * 0.82f : shirt;
                case Tile.ChestBack:
                    return Mathf.Abs(v - 0.8f) < 0.02f ? shirt * 0.82f : shirt;
                case Tile.Shirt:
                    return shirt;
                case Tile.Sleeve:
                    return v < 0.14f ? shirt * 0.78f : shirt;
                case Tile.Forearm:
                    return v > 0.9f ? SkinColour * 0.85f : SkinColour;
                case Tile.Hand:
                    return SkinColour * 0.97f;
                case Tile.Fingers:
                    return IsLine(u, 4, 0.02f) && v < 0.7f ? SkinColour * 0.78f : SkinColour * 0.97f;
                case Tile.BeltFront:
                    return BeltFront(u, v);
                case Tile.Belt:
                    return v > 0.45f ? Leather : TrouserColour;
                case Tile.TrouserFront:
                    return v < 0.1f ? TrouserColour * 0.88f : TrouserColour;
                case Tile.TrouserSide:
                    return Mathf.Abs(u - 0.5f) < 0.03f ? TrouserColour * 0.8f : TrouserColour;
                case Tile.Trousers:
                    return TrouserColour;
                case Tile.ShinFront:
                case Tile.ShinSide:
                    return v < 0.16f ? TrouserColour * 1.15f : (tile == Tile.ShinSide && Mathf.Abs(u - 0.5f) < 0.03f ? TrouserColour * 0.8f : TrouserColour);
                case Tile.ShoeSide:
                    return v < 0.3f ? SoleColour : (u > 0.7f ? Leather * 1.1f : Leather);
                case Tile.ShoeTop:
                    return ShoeTop(u, v);
                case Tile.ShoeFront:
                    return v < 0.3f ? SoleColour : Leather * 1.1f;
                case Tile.Sole:
                    return IsLine(v, 6, 0.04f) ? SoleColour * 0.7f : SoleColour;
                case Tile.PackBack:
                    return PackBack(u, v);
                case Tile.PackSide:
                    return InFrame(u, v, 0.2f, 0.8f, 0.12f, 0.55f) ? CanvasColour * 0.82f : CanvasColour;
                case Tile.PackTop:
                    return Mathf.Abs(u - 0.5f) < 0.18f && Mathf.Abs(v - 0.5f) < 0.06f ? DarkLeather : CanvasColour;
                case Tile.Canvas:
                    return CanvasColour;
                default:
                    return Mathf.Abs(u - 0.5f) > 0.32f ? DarkLeather : Leather;
            }
        }

        // Eyebrows, eyes looking straight ahead, a shadow under the nose, a mouth and rosy cheeks.
        private static Color Face(float u, float v)
        {
            if (v > 0.88f) return Strands(u, v);

            float side = u < 0.5f ? u : 1f - u;
            if (v > 0.63f && v < 0.68f && side > 0.16f && side < 0.40f) return HairColour;

            if (v > 0.47f && v < 0.59f && side > 0.18f && side < 0.40f)
            {
                if (side < 0.29f) return EyeWhite;
                if (side > 0.31f && side < 0.37f && v > 0.49f && v < 0.57f) return Pupil;
                return Iris;
            }

            if (v > 0.35f && v < 0.39f && side > 0.43f) return SkinColour * 0.85f;
            if (v > 0.22f && v < 0.27f && side > 0.37f) return v < 0.245f ? Lips * 0.9f : Lips;

            float cheek = new Vector2(side - 0.22f, v - 0.36f).sqrMagnitude;
            if (cheek < 0.005f) return Color.Lerp(SkinColour, Blush, 0.35f);
            return v < 0.08f ? SkinColour * 0.9f : SkinColour;
        }

        // From the back of the head (u = 0) to the face (u = 1): hair over the back and the top, a sideburn.
        private static Color HeadSide(float u, float v)
        {
            if (v > 0.78f) return Strands(u, v);
            if (u < 0.4f && v > 0.22f) return Strands(u, v);
            if (u < 0.55f && v > 0.5f) return Strands(u, v);
            if (u > 0.6f && u < 0.7f && v > 0.42f) return HairColour * 0.9f;
            return SkinColour;
        }

        private static Color Strands(float u, float v)
        {
            float wave = Mathf.Sin(u * 47f + v * 9f) * 0.5f + 0.5f;
            return HairColour * (0.88f + 0.2f * wave);
        }

        // Open collar, button placket and a breast pocket on the left side.
        private static Color ChestFront(float u, float v, Color shirt)
        {
            float fromCentre = Mathf.Abs(u - 0.5f);
            float collar = (v - 0.78f) * 1.4f;
            if (fromCentre < collar) return SkinColour * 0.85f;
            if (fromCentre < collar + 0.05f) return shirt * 1.12f;

            if (fromCentre < 0.02f) return shirt * 0.8f;
            if (fromCentre < 0.04f && IsDot(v, new[] { 0.62f, 0.42f, 0.22f }, 0.025f)) return Button;

            if (InFrame(u, v, 0.6f, 0.82f, 0.52f, 0.7f)) return shirt * 0.85f;
            return shirt;
        }

        private static Color BeltFront(float u, float v)
        {
            if (v < 0.45f) return Mathf.Abs(u - 0.5f) < 0.015f ? TrouserColour * 0.8f : TrouserColour;

            float fromCentre = Mathf.Abs(u - 0.5f);
            if (fromCentre < 0.1f && v > 0.5f && v < 0.95f)
            {
                return fromCentre < 0.05f && v > 0.62f && v < 0.83f ? Leather * 0.7f : Metal;
            }

            return Leather;
        }

        // Laces over the back half of the shoe (v = 1 is the toe).
        private static Color ShoeTop(float u, float v)
        {
            bool laces = Mathf.Abs(u - 0.5f) < 0.24f && v > 0.15f && v < 0.6f && IsLine(v, 8, 0.03f);
            if (laces) return Lace;
            return v > 0.7f ? Leather * 1.1f : Leather;
        }

        // The face of the backpack seen from behind the player: a flap with a buckle, a pocket below.
        private static Color PackBack(float u, float v)
        {
            if (v > 0.5f)
            {
                if (Mathf.Abs(u - 0.5f) < 0.06f && v < 0.62f) return Metal;
                return v < 0.54f ? CanvasColour * 0.7f : CanvasColour * 0.9f;
            }

            return InFrame(u, v, 0.18f, 0.82f, 0.1f, 0.4f) ? CanvasColour * 0.8f : CanvasColour;
        }

        // True on the outline of the rectangle [u0, u1] × [v0, v1].
        private static bool InFrame(float u, float v, float u0, float u1, float v0, float v1)
        {
            const float thickness = 0.035f;
            bool inside = u > u0 && u < u1 && v > v0 && v < v1;
            bool core = u > u0 + thickness && u < u1 - thickness && v > v0 + thickness && v < v1 - thickness;
            return inside && !core;
        }

        // True near one of the count - 1 lines that divide [0, 1] into count equal parts.
        private static bool IsLine(float coordinate, int count, float halfWidth)
        {
            float position = coordinate * count;
            float nearest = Mathf.Round(position);
            return nearest > 0f && nearest < count && Mathf.Abs(position - nearest) < halfWidth * count;
        }

        private static bool IsDot(float coordinate, float[] centres, float radius)
        {
            foreach (float centre in centres)
            {
                if (Mathf.Abs(coordinate - centre) < radius) return true;
            }

            return false;
        }

        private static float Noise(int tile, int x, int y)
        {
            int hash = (x * 73856093) ^ (y * 19349663) ^ (tile * 83492791);
            return (hash & 0xFF) / 255f;
        }
    }
}

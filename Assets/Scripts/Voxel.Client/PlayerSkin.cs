using UnityEngine;
using Voxel.Core;

namespace Voxel.Client
{
    // Texture of the player model, generated at runtime like the block atlas: one tile per kind of
    // face, drawn by simple rules, then shaded (darker towards the bottom and along the edges) so that
    // each box reads as a solid piece. It depends on the player's appearance (skin tone, sex, hair
    // style) and on a shirt colour that tells players apart.
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

        // From lightest to darkest, one per Appearance.SkinTone.
        private static readonly Color[] SkinTones =
        {
            new Color(0.96f, 0.80f, 0.69f),
            new Color(0.87f, 0.67f, 0.52f),
            new Color(0.80f, 0.60f, 0.43f),
            new Color(0.68f, 0.48f, 0.32f),
            new Color(0.52f, 0.35f, 0.22f),
            new Color(0.36f, 0.24f, 0.15f)
        };

        // One per Appearance.EyeColorNames: green, brown, hazel, blue, grey, dark.
        private static readonly Color[] EyeColours =
        {
            new Color(0.24f, 0.45f, 0.36f),
            new Color(0.40f, 0.24f, 0.12f),
            new Color(0.52f, 0.42f, 0.20f),
            new Color(0.25f, 0.45f, 0.75f),
            new Color(0.50f, 0.55f, 0.58f),
            new Color(0.16f, 0.10f, 0.07f)
        };

        // One per Appearance.HairColorNames: brown, black, dark brown, auburn, ginger, blond, grey.
        private static readonly Color[] HairColours =
        {
            new Color(0.32f, 0.20f, 0.11f),
            new Color(0.09f, 0.08f, 0.08f),
            new Color(0.19f, 0.12f, 0.07f),
            new Color(0.45f, 0.18f, 0.10f),
            new Color(0.78f, 0.38f, 0.14f),
            new Color(0.86f, 0.72f, 0.42f),
            new Color(0.66f, 0.66f, 0.64f)
        };

        private static readonly Color EyeWhite = new Color(0.96f, 0.96f, 0.94f);
        private static readonly Color Pupil = new Color(0.08f, 0.08f, 0.10f);
        private static readonly Color Lips = new Color(0.70f, 0.42f, 0.36f);
        private static readonly Color RedLips = new Color(0.72f, 0.30f, 0.30f);
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

        public static Color SkinTone(int tone) => SkinTones[Mathf.Clamp(tone, 0, SkinTones.Length - 1)];

        public static Color EyeColour(int index) => EyeColours[Mathf.Clamp(index, 0, EyeColours.Length - 1)];

        public static Color HairColour(int index) => HairColours[Mathf.Clamp(index, 0, HairColours.Length - 1)];

        public static Texture2D CreateTexture(Color shirt, Appearance appearance)
        {
            var look = new Look(shirt, appearance);
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
                        Color color = Shade(BaseColor((Tile)tile, u, v, look), tile, x, y, v);
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

        // Everything the drawing rules need to know about one player.
        private readonly struct Look
        {
            public readonly Color Shirt;
            public readonly Color Skin;
            public readonly Color HairColour;
            public readonly Color Iris;
            public readonly HairStyle Hair;
            public readonly FacialHair FacialHair;
            public readonly bool Female;

            public Look(Color shirt, Appearance appearance)
            {
                Shirt = shirt;
                Skin = SkinTone(appearance.SkinTone);
                HairColour = PlayerSkin.HairColour(appearance.HairColor);
                Iris = EyeColour(appearance.EyeColor);
                Hair = appearance.Hair;
                FacialHair = appearance.FacialHair;
                Female = appearance.Sex == Sex.Female;
            }

            // A few days' growth: the skin speckled with the hair colour.
            public Color Stubble(float u, float v)
            {
                bool dark = Mathf.Repeat(Mathf.Sin(u * 913f + v * 371f) * 4375.85f, 1f) > 0.5f;
                return Color.Lerp(Skin, HairColour, dark ? 0.5f : 0.25f);
            }

            public bool Bald => Hair == HairStyle.Bald;

            public bool Buzz => Hair == HairStyle.Buzz;

            // A buzz cut lets the skin show through.
            public Color HairOn(float u, float v) => Buzz ? Color.Lerp(Skin, HairColour, 0.75f) : Strands(u, v);

            // Strands of hair, with a slight wave of light and shade.
            public Color Strands(float u, float v)
            {
                float wave = Mathf.Sin(u * 47f + v * 9f) * 0.5f + 0.5f;
                return HairColour * (0.88f + 0.2f * wave);
            }
        }

        // Light from above, a darker outline, and a little grain so that flat colours look like fabric.
        private static Color Shade(Color color, int tile, int x, int y, float v)
        {
            bool edge = x == 0 || y == 0 || x == TileSize - 1 || y == TileSize - 1;
            float light = (0.84f + 0.16f * v) * (edge ? 0.82f : 1f);
            return color * light * (0.94f + 0.06f * Noise(tile, x, y));
        }

        private static Color BaseColor(Tile tile, float u, float v, in Look look)
        {
            Color skin = look.Skin;
            Color shirt = look.Shirt;
            switch (tile)
            {
                case Tile.Skin:
                    return skin;
                case Tile.SkinShadow:
                    return skin * 0.82f;
                case Tile.Face:
                    return Face(u, v, look);
                case Tile.HeadSide:
                    return HeadSide(u, v, look);
                case Tile.HeadBack:
                    return HeadBack(u, v, look);
                case Tile.Hair:
                    return look.Bald ? skin : look.HairOn(u, v);
                case Tile.HairEdge:
                    return look.HairOn(u, v) * (v < 0.3f ? 0.8f : 1f);
                case Tile.ChestFront:
                    return ChestFront(u, v, look);
                case Tile.ChestSide:
                    return Mathf.Abs(u - 0.5f) < 0.025f ? shirt * 0.82f : shirt;
                case Tile.ChestBack:
                    return Mathf.Abs(v - 0.8f) < 0.02f ? shirt * 0.82f : shirt;
                case Tile.Shirt:
                    return shirt;
                case Tile.Sleeve:
                    return v < 0.14f ? shirt * 0.78f : shirt;
                case Tile.Forearm:
                    return v > 0.9f ? skin * 0.85f : skin;
                case Tile.Hand:
                    return skin * 0.97f;
                case Tile.Fingers:
                    return IsLine(u, 4, 0.02f) && v < 0.7f ? skin * 0.78f : skin * 0.97f;
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

        // Hair along the top, eyebrows, eyes looking straight ahead (with lashes for a woman), a shadow
        // under the nose, a mouth and rosy cheeks.
        private static Color Face(float u, float v, in Look look)
        {
            float hairLine = look.Buzz ? 0.92f : 0.88f;
            if (!look.Bald && v > hairLine) return look.HairOn(u, v);

            float side = u < 0.5f ? u : 1f - u;
            float browBottom = look.Female ? 0.645f : 0.63f;
            if (v > browBottom && v < 0.68f && side > 0.16f && side < 0.40f) return look.HairColour;
            if (look.Female && v > 0.585f && v < 0.615f && side > 0.16f && side < 0.40f) return Pupil;

            if (v > 0.47f && v < 0.59f && side > 0.18f && side < 0.40f)
            {
                if (side < 0.29f) return EyeWhite;
                if (side > 0.31f && side < 0.37f && v > 0.49f && v < 0.57f) return Pupil;
                return look.Iris;
            }

            Color skin = look.Skin;
            if (v > 0.35f && v < 0.39f && side > 0.43f) return skin * 0.85f;

            Color lips = look.Female ? RedLips : Lips;
            bool mouth = v > 0.22f && v < 0.27f && side > 0.37f;
            if (!mouth)
            {
                Color? facialHair = FacialHairOnFace(u, v, side, look);
                if (facialHair.HasValue) return facialHair.Value;
            }

            if (mouth) return v < 0.245f ? lips * 0.9f : lips;

            float cheek = new Vector2(side - 0.22f, v - 0.36f).sqrMagnitude;
            if (cheek < 0.005f) return Color.Lerp(skin, Blush, look.Female ? 0.45f : 0.3f);
            return v < 0.08f ? skin * 0.9f : skin;
        }

        // Facial hair painted on the face, below the nose, or null where there is none. `side` is the
        // distance from the nearest edge of the face (0 at the edges, 0.5 in the middle).
        private static Color? FacialHairOnFace(float u, float v, float side, in Look look)
        {
            bool moustache = v > 0.27f && v < 0.335f && side > 0.31f;
            bool jaw = v < 0.3f || (side < 0.14f && v < 0.48f);
            switch (look.FacialHair)
            {
                case FacialHair.Stubble:
                    return jaw || moustache ? look.Stubble(u, v) : (Color?)null;
                case FacialHair.Moustache:
                    return moustache ? look.Strands(u, v) : (Color?)null;
                case FacialHair.Beard:
                    return jaw || moustache ? look.Strands(u, v) : (Color?)null;
                default:
                    return null;
            }
        }

        // From the back of the head (u = 0) to the face (u = 1): hair over the back and the top, a
        // sideburn, and a beard or stubble along the jaw.
        private static Color HeadSide(float u, float v, in Look look)
        {
            bool jaw = u > 0.55f && v < 0.42f;
            if (jaw && look.FacialHair == FacialHair.Beard) return look.Strands(u, v);
            if (jaw && look.FacialHair == FacialHair.Stubble) return look.Stubble(u, v);

            if (look.Bald) return look.Skin;
            if (look.Buzz) return v > 0.82f || (u < 0.3f && v > 0.45f) ? look.HairOn(u, v) : look.Skin;

            if (v > 0.78f) return look.Strands(u, v);
            if (u < 0.4f && v > 0.22f) return look.Strands(u, v);
            if (u < 0.55f && v > 0.5f) return look.Strands(u, v);
            if (u > 0.6f && u < 0.7f && v > 0.42f) return look.HairColour * 0.9f;
            return look.Skin;
        }

        private static Color HeadBack(float u, float v, in Look look)
        {
            if (look.Bald) return look.Skin;
            if (look.Buzz) return v > 0.3f ? look.HairOn(u, v) : look.Skin * 0.9f;
            if (look.Hair == HairStyle.Long) return look.Strands(u, v);
            return v > 0.15f ? look.Strands(u, v) : look.Skin * 0.9f;
        }

        // Open collar, button placket and a breast pocket on the left side.
        private static Color ChestFront(float u, float v, in Look look)
        {
            Color shirt = look.Shirt;
            float fromCentre = Mathf.Abs(u - 0.5f);
            float collar = (v - 0.78f) * 1.4f;
            if (fromCentre < collar) return look.Skin * 0.85f;
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

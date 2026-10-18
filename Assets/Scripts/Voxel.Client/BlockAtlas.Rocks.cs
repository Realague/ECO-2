using UnityEngine;

namespace Voxel.Client
{
    // The rocks of the atlas, and snow, drawn like pixel art rather than as noisy flat colour: each pixel's
    // shade is picked from a small palette (with ordered dithering between shades), from a tone that
    // follows the rock's structure (crystals, columns, beds, veins, plates, pebbles, nuggets, built
    // from cellular and smooth noise that wrap around the tile) and from a relief lit from the upper
    // left, so grains and stones stand out. Everything wraps, so neighbouring blocks match.
    internal static partial class BlockAtlas
    {
        // Palettes, darkest first.
        private static readonly Color[] GraniteGrey = Palette(0x5C5A5C, 0x7A787A, 0x989494, 0xB0ACAA, 0xC8C4C2);
        private static readonly Color[] GranitePinkShades = Palette(0x8C6B66, 0xAD857D, 0xC79E94, 0xDBB8AD);
        private static readonly Color[] GraniteMica = Palette(0x1E1E21, 0x333338, 0x4D4D52);
        private static readonly Color[] BasaltShades = Palette(0x1C1C21, 0x2B2B30, 0x3A3A42, 0x4C4C54, 0x606068);
        private static readonly Color[] LimestoneShades = Palette(0x998F75, 0xB3A88C, 0xC9BFA1, 0xDBD1B3, 0xEBE3C7);
        private static readonly Color[] MarbleShades = Palette(0x8C8C94, 0xB8B8BD, 0xDBDBDE, 0xEDEDED, 0xFAFAF7);
        private static readonly Color[] SandstoneShades = Palette(0x9E5C33, 0xB87345, 0xD18F57, 0xE0A86E, 0xEDC287);
        private static readonly Color[] SlateShades = Palette(0x2E333D, 0x3D4552, 0x4D5766, 0x5E6878, 0x737D8C);
        private static readonly Color[] BedrockShades = Palette(0x0D0D0F, 0x1A1A1C, 0x29292E, 0x3B3B40, 0x4D4D52);
        private static readonly Color[] StoneShades = Palette(0x4D4D52, 0x66666B, 0x808085, 0x949499, 0xA8A8AD);
        private static readonly Color[] GravelGrey = Palette(0x45454A, 0x66666B, 0x87878C, 0xA3A3A8);
        private static readonly Color[] GravelBrown = Palette(0x4A4038, 0x6B5D52, 0x8C7D70, 0xA8998C);
        private static readonly Color[] GravelDark = Palette(0x2B2B2E, 0x404045, 0x57575C, 0x707075);
        private static readonly Color[] CoalShades = Palette(0x0A0A0D, 0x1A1A1F, 0x333338, 0x66666E);
        private static readonly Color[] IronShades = Palette(0x734026, 0x9E613D, 0xC7855A, 0xE6B38C);
        private static readonly Color[] CopperShades = Palette(0x266654, 0x409475, 0x66BF99, 0xA6E6C7);
        private static readonly Color[] TinShades = Palette(0x8C919E, 0xB3B8C7, 0xD6DBE6, 0xF7FAFF);
        private static readonly Color[] GoldShades = Palette(0xB3801A, 0xE0AD2E, 0xFAD64D, 0xFFF5A6);
        private static readonly Color[] SaltRock = Palette(0xA88F8F, 0xC2A8A8, 0xD6C2C2, 0xE6D6D6);
        private static readonly Color[] SaltCrystals = Palette(0xD9D1D1, 0xF0EBEB, 0xFFFFFF);
        private static readonly Color[] FlintShades = Palette(0x1F1F26, 0x33333D, 0x4D4D59, 0x737380);
        private static readonly Color FlintCortex = new Color(0.9f, 0.88f, 0.82f);

        // Snow is bluish white with blue shadows, never pure white but where it sparkles: the sun is
        // bright and warm, so whiter snow saturates to a flat pink in its light.
        private static readonly Color[] SnowShades = Palette(0x7F93B5, 0x9DB0CC, 0xB8C8DE, 0xCCDAEB, 0xDCE6F3);
        private static readonly Color SnowSparkle = new Color(1f, 1f, 1f);
        private static readonly Color[] MudShades = Palette(0x241A12, 0x33261A, 0x453424, 0x584432, 0x6B5641);
        private static readonly Color[] PuddleShades = Palette(0x2A2622, 0x36322D, 0x45413A, 0x5A574F);

        private static bool IsPainted(int tile)
        {
            switch (tile)
            {
                case GraniteTile:
                case BasaltTile:
                case LimestoneTile:
                case MarbleTile:
                case SandstoneTile:
                case SlateTile:
                case GravelTile:
                case CoalTile:
                case IronTile:
                case CopperTile:
                case TinTile:
                case GoldTile:
                case SaltTile:
                case FlintTile:
                case BedrockTile:
                case SnowTile:
                case MudTile:
                    return true;
                default:
                    return false;
            }
        }

        // A rock pixel: its tone, shifted by the light its relief catches, through its palette.
        private static Color PaintedColour(int tile, int x, int y)
        {
            float light = RockHeight(tile, x + 1, y - 1) - RockHeight(tile, x - 1, y + 1);
            switch (tile)
            {
                case GraniteTile: return GraniteColour(tile, x, y, light);
                case BasaltTile: return BasaltColour(tile, x, y, light);
                case LimestoneTile: return LimestoneColour(tile, x, y, light);
                case MarbleTile: return MarbleColour(tile, x, y, light);
                case SandstoneTile: return SandstoneColour(tile, x, y, light);
                case SlateTile: return SlateColour(tile, x, y, light);
                case GravelTile: return GravelColour(tile, x, y, light);
                case BedrockTile: return BedrockColour(tile, x, y, light);
                case CoalTile: return Ore(tile, x, y, light, CoalShades);
                case IronTile: return Ore(tile, x, y, light, IronShades);
                case CopperTile: return Ore(tile, x, y, light, CopperShades);
                case TinTile: return Ore(tile, x, y, light, TinShades);
                case GoldTile: return Ore(tile, x, y, light, GoldShades);
                case SaltTile: return SaltColour(tile, x, y, light);
                case SnowTile: return SnowColour(tile, x, y, light);
                case MudTile: return MudColour(tile, x, y, light);
                default: return FlintColour(tile, x, y, light);
            }
        }

        // The height each rock's relief is lit from, at any pixel (wrapping around the tile).
        private static float RockHeight(int tile, int x, int y)
        {
            x = Wrap(x);
            y = Wrap(y);
            switch (tile)
            {
                case GraniteTile:
                {
                    Cell cell = Voronoi(tile, x, y, 10, 10);
                    return Clamp01((cell.F2 - cell.F1) / 4f) * 0.5f + Fbm(tile + 1, x, y, 16, 2) * 0.3f;
                }
                case BasaltTile:
                {
                    Cell cell = Voronoi(tile, x, y, 4, 2);
                    return Clamp01((cell.F2 - cell.F1) / 5f) + Fbm(tile + 1, x, y, 8, 3) * 0.3f;
                }
                case LimestoneTile:
                    return Fbm(tile + 1, x, y, 8, 4) * 0.8f - (IsPit(tile, x, y) ? 0.5f : 0f);
                case MarbleTile:
                    return Fbm(tile + 1, x, y, 8, 3) * 0.2f;
                case SandstoneTile:
                    return Fbm(tile + 1, x, y, 8, 3) * 0.6f;
                case SlateTile:
                {
                    Cell cell = Voronoi(tile, x, y, 2, 8);
                    return Clamp01((cell.F2 - cell.F1) / 3f) * 0.6f;
                }
                case GravelTile:
                {
                    Cell cell = Voronoi(tile, x, y, 8, 8);
                    return Clamp01((cell.F2 - cell.F1) / 4f) * 1.4f;
                }
                case BedrockTile:
                {
                    Cell cell = Voronoi(tile, x, y, 6, 6);
                    return Clamp01((cell.F2 - cell.F1) / 7f) * 1.2f;
                }
                case SaltTile:
                    return StoneHeight(tile, x, y) + (CrystalDome(tile, x, y, out _) * 1.2f);
                case FlintTile:
                    return Fbm(tile + 1, x, y, 8, 3) * 0.5f + NoduleDome(tile, x, y) * 1.2f;
                case SnowTile:
                    return Fbm(tile + 1, x, y, 4, 3) * 0.5f + SnowRipple(tile, x, y) * 0.35f;
                case MudTile:
                    // Puddles lie flat; around them the mud is trampled into soft lumps, with a few pebbles.
                    return IsPuddle(tile, x, y) ? 0.2f : Fbm(tile + 1, x, y, 4, 4) * 0.6f + (Noise(tile + 9, x / 2, y / 2) > 0.985f ? 0.5f : 0f);
                default:
                    // Ores: the stone around, with the nuggets standing out of it.
                    return StoneHeight(tile, x, y) + OreCrystal(tile, x, y, out _) * 1.4f;
            }
        }

        // Granite: interlocking crystals of grey quartz, pink feldspar and dark mica, each with its own tone.
        private static Color GraniteColour(int tile, int x, int y, float light)
        {
            Cell cell = Voronoi(tile, x, y, 10, 10);
            float kind = Noise(tile + 2, cell.Id, 0);
            float tone = 0.4f + 0.3f * Noise(tile + 3, cell.Id, 0) + (Fbm(tile + 4, x, y, 16, 2) - 0.5f) * 0.2f + light * 0.5f;
            Color[] palette = kind < 0.72f ? GraniteGrey : kind < 0.9f ? GranitePinkShades : GraniteMica;
            return Ramp(palette, tone, x, y);
        }

        // Basalt: tall columns between dark joints, each column its own shade, rough on its face.
        private static Color BasaltColour(int tile, int x, int y, float light)
        {
            Cell cell = Voronoi(tile, x, y, 4, 2);
            if (cell.F2 - cell.F1 < 1.6f) return BasaltShades[0];

            float tone = 0.35f + 0.3f * Noise(tile + 2, cell.Id, 0) + (Fbm(tile + 1, x, y, 8, 3) - 0.5f) * 0.4f + light * 0.9f;
            return Ramp(BasaltShades, tone, x, y);
        }

        // Limestone: soft beds of slightly different tones, parted by thin wavering lines, with a few pits.
        private static Color LimestoneColour(int tile, int x, int y, float light)
        {
            float bedPosition = (y + Fbm(tile + 5, x, y, 4, 2) * 10f) / 16f;
            float within = bedPosition - Mathf.Floor(bedPosition);
            if (within < 0.07f) return LimestoneShades[1];
            if (IsPit(tile, x, y)) return LimestoneShades[0];

            float bedTone = Noise(tile + 6, ((int)Mathf.Floor(bedPosition) % 4 + 4) % 4, 0) * 0.2f;
            float tone = 0.45f + bedTone + (Fbm(tile + 1, x, y, 8, 4) - 0.5f) * 0.45f + light * 0.7f;
            return Ramp(LimestoneShades, tone, x, y);
        }

        private static bool IsPit(int tile, int x, int y)
        {
            Cell cell = Voronoi(tile + 7, x, y, 6, 6);
            return cell.F1 < 1.3f && Noise(tile + 8, cell.Id, 0) > 0.72f;
        }

        // Marble: white, with grey veins swirling through it (a sine wave bent by turbulence), and
        // fainter veins between.
        private static Color MarbleColour(int tile, int x, int y, float light)
        {
            float turbulence = Fbm(tile + 1, x, y, 4, 4);
            float phase = 2f * Mathf.PI * (2f * x + y) / TileSize + turbulence * 9f;
            float vein = Mathf.Pow(1f - Mathf.Abs(Mathf.Sin(phase)), 10f);
            float faintPhase = 2f * Mathf.PI * (x - 3f * y) / TileSize + Fbm(tile + 9, x, y, 4, 3) * 7f;
            float faint = Mathf.Pow(1f - Mathf.Abs(Mathf.Sin(faintPhase)), 14f);
            float tone = 0.82f - vein * 0.85f - faint * 0.3f + (Fbm(tile + 10, x, y, 8, 2) - 0.5f) * 0.15f + light * 0.4f;
            return Ramp(MarbleShades, tone, x, y);
        }

        // Sandstone: beds four to a block, each its own colour, lighter in their middle, wavering, with a sandy grain.
        private static Color SandstoneColour(int tile, int x, int y, float light)
        {
            float bedPosition = (y + Fbm(tile + 5, x, y, 4, 3) * 12f) / 16f;
            int bed = ((int)Mathf.Floor(bedPosition) % 4 + 4) % 4;
            float within = bedPosition - Mathf.Floor(bedPosition);
            float grain = (Noise(tile + 11, x, y) - 0.5f) * 0.18f;
            float tone = 0.3f + Noise(tile + 6, bed, 0) * 0.35f + (0.5f - Mathf.Abs(within - 0.5f)) * 0.3f + grain + light * 0.6f;
            return Ramp(SandstoneShades, tone, x, y);
        }

        // Slate: thin flat plates with dark seams between them, and fine cleavage lines.
        private static Color SlateColour(int tile, int x, int y, float light)
        {
            Cell cell = Voronoi(tile, x, y, 2, 8);
            if (cell.F2 - cell.F1 < 1.1f) return SlateShades[0];

            bool cleavage = (y + (int)(Fbm(tile + 5, x, y, 4, 2) * 3f)) % 4 == 0;
            float tone = 0.4f + Noise(tile + 2, cell.Id, 0) * 0.3f + (Fbm(tile + 1, x, y, 16, 2) - 0.5f) * 0.2f
                - (cleavage ? 0.15f : 0f) + light * 0.8f;
            return Ramp(SlateShades, tone, x, y);
        }

        // Gravel: rounded pebbles of three kinds of stone, lit from above, with dark gaps between.
        private static Color GravelColour(int tile, int x, int y, float light)
        {
            Cell cell = Voronoi(tile, x, y, 8, 8);
            float kind = Noise(tile + 2, cell.Id, 0);
            Color[] palette = kind < 0.5f ? GravelGrey : kind < 0.8f ? GravelBrown : GravelDark;
            if (cell.F2 - cell.F1 < 1.2f) return GravelDark[0];

            float tone = 0.35f + Noise(tile + 3, cell.Id, 0) * 0.3f + light * 1.2f;
            return Ramp(palette, tone, x, y);
        }

        // Bedrock: dark, hard blocks of rock with black cracks between them.
        private static Color BedrockColour(int tile, int x, int y, float light)
        {
            Cell cell = Voronoi(tile, x, y, 6, 6);
            if (cell.F2 - cell.F1 < 1.8f) return BedrockShades[0];

            float tone = 0.3f + Noise(tile + 2, cell.Id, 0) * 0.35f + (Fbm(tile + 1, x, y, 8, 3) - 0.5f) * 0.3f + light * 1f;
            return Ramp(BedrockShades, tone, x, y);
        }

        // The grey stone ores sit in: cobbly, with a few cracks.
        private static float StoneHeight(int tile, int x, int y)
        {
            Cell cell = Voronoi(tile + 20, x, y, 8, 8);
            return Clamp01((cell.F2 - cell.F1) / 6f) * 0.5f + Fbm(tile + 21, x, y, 8, 3) * 0.5f;
        }

        private static Color StoneColour(int tile, int x, int y, float light, Color[] palette)
        {
            Cell cell = Voronoi(tile + 20, x, y, 8, 8);
            if (cell.F2 - cell.F1 < 0.9f && Noise(tile + 22, cell.Id, 0) > 0.55f) return palette[0];

            float tone = 0.4f + Noise(tile + 23, cell.Id, 0) * 0.2f + (Fbm(tile + 21, x, y, 8, 3) - 0.5f) * 0.3f + light * 0.8f;
            return Ramp(palette, tone, x, y);
        }

        // Ore: angular crystals with dark rims, gathered in veins across the grey stone.
        private static Color Ore(int tile, int x, int y, float light, Color[] ore)
        {
            float height = OreCrystal(tile, x, y, out int crystal);
            if (height <= 0f) return StoneColour(tile, x, y, light, StoneShades);
            if (height < 0.3f) return ore[0];

            return Ramp(ore, 0.25f + Noise(tile + 33, crystal, 0) * 0.3f + height * 0.3f + light * 1.2f, x, y);
        }

        // How high an ore crystal rises at this pixel, 0 outside: the cells of a fine grid that fall
        // where a smooth noise is high, so crystals gather in veins; each cell is one facet, highest
        // in its middle.
        private static float OreCrystal(int tile, int x, int y, out int crystal)
        {
            const int grid = 12;
            Cell cell = Voronoi(tile + 30, x, y, grid, grid);
            crystal = cell.Id;
            int centreX = (int)((cell.Id % grid + 0.5f) * TileSize / grid);
            int centreY = (int)((cell.Id / grid + 0.5f) * TileSize / grid);
            float vein = SmoothNoise(tile + 32, centreX, centreY, 16) * 0.75f + Noise(tile + 31, cell.Id, 0) * 0.25f;
            if (vein < 0.6f) return 0f;

            return 0.15f + 0.85f * Clamp01((cell.F2 - cell.F1) / 3.5f);
        }

        // Rock salt: rosy rock with white cubic crystals in it.
        private static Color SaltColour(int tile, int x, int y, float light)
        {
            float dome = CrystalDome(tile, x, y, out bool edge);
            if (dome > 0f) return edge ? SaltCrystals[0] : Ramp(SaltCrystals, 0.3f + dome * 0.5f + light, x, y);

            return StoneColour(tile, x, y, light, SaltRock);
        }

        // Square crystals: inside a square around the cell's point, about two cells in five.
        private static float CrystalDome(int tile, int x, int y, out bool edge)
        {
            edge = false;
            Cell cell = Voronoi(tile + 40, x, y, 7, 7);
            const float half = 3.5f;
            float distance = Mathf.Max(Mathf.Abs(cell.Dx), Mathf.Abs(cell.Dy));
            if (Noise(tile + 41, cell.Id, 0) > 0.4f || distance >= half) return 0f;

            edge = distance >= half - 1f;
            return 1f - distance / half;
        }

        // Flint: dark glassy nodules with a pale crust, in limestone.
        private static Color FlintColour(int tile, int x, int y, float light)
        {
            float dome = NoduleDome(tile, x, y);
            if (dome <= 0f) return Ramp(LimestoneShades, 0.5f + (Fbm(tile + 1, x, y, 8, 3) - 0.5f) * 0.4f + light * 0.6f, x, y);
            if (dome < 0.2f) return FlintCortex;

            return Ramp(FlintShades, 0.2f + dome * 0.4f + light * 1.4f, x, y);
        }

        // How high a nodule rises at this pixel, 0 outside: lumpy rounds about two cells in three of a coarse grid hold.
        private static float NoduleDome(int tile, int x, int y)
        {
            Cell cell = Voronoi(tile + 50, x, y, 3, 3);
            const float radius = 9f;
            float distance = cell.F1 + (Fbm(tile + 52, x, y, 8, 2) - 0.5f) * 8f;
            if (Noise(tile + 51, cell.Id, 0) > 0.7f || distance >= radius) return 0f;

            return Mathf.Max(0.01f, 1f - distance / radius);
        }

        // Snow: soft drifts and wind ripples whose lee sides are in blue shadow, a fine crystalline
        // grain, and here and there a sparkle.
        private static Color SnowColour(int tile, int x, int y, float light)
        {
            if (Noise(tile + 7, x, y) > 0.988f) return SnowSparkle;

            float grain = (Noise(tile + 8, x, y) - 0.5f) * 0.12f;
            float tone = 0.66f + (Fbm(tile + 1, x, y, 4, 3) - 0.5f) * 0.3f + grain + light * 1.1f;
            return Ramp(SnowShades, tone, x, y);
        }

        // Wind ripples from 0 to 1: three waves down the tile, tilted once across it and bent by a
        // smooth noise, steeper on one side than the other like real ones.
        private static float SnowRipple(int tile, int x, int y)
        {
            float wave = (3f * y + x) / TileSize + Fbm(tile + 5, x, y, 4, 2) * 1.5f;
            float within = wave - Mathf.Floor(wave);
            return within < 0.75f ? within / 0.75f : (1f - within) / 0.25f;
        }

        // ---- Mud ----

        // Dark wet earth, trampled into soft lumps, with greyish puddles that catch the sky: a light
        // streak along their upper left edge, as water's surface shines.
        private static Color MudColour(int tile, int x, int y, float light)
        {
            if (IsPuddle(tile, x, y))
            {
                bool shore = !IsPuddle(tile, x - 1, y + 1);
                float sheen = 0.45f + (Fbm(tile + 6, x, y, 8, 2) - 0.5f) * 0.5f + (shore ? 0.35f : 0f);
                return Ramp(PuddleShades, sheen, x, y);
            }

            if (Noise(tile + 9, x / 2, y / 2) > 0.985f) return Ramp(GravelBrown, 0.3f + light * 1.5f, x, y);

            float grain = (Noise(tile + 8, x, y) - 0.5f) * 0.08f;
            float tone = 0.45f + (Fbm(tile + 1, x, y, 4, 4) - 0.5f) * 0.8f + grain + light * 1.0f;
            return Ramp(MudShades, tone, x, y);
        }

        private static bool IsPuddle(int tile, int x, int y)
        {
            return Fbm(tile + 4, Wrap(x), Wrap(y), 4, 3) > 0.64f;
        }

        // ---- Tools ----

        // A shade from a palette for a tone from 0 to 1, with ordered (Bayer) dithering between shades.
        private static Color Ramp(Color[] palette, float tone, int x, int y)
        {
            float dither = (Bayer[(y & 3) * 4 + (x & 3)] + 0.5f) / 16f - 0.5f;
            int index = Mathf.FloorToInt(tone * palette.Length + dither * 0.9f);
            return palette[Mathf.Clamp(index, 0, palette.Length - 1)];
        }

        private static readonly int[] Bayer = { 0, 8, 2, 10, 12, 4, 14, 6, 3, 11, 1, 9, 15, 7, 13, 5 };

        // The nearest and second nearest of a grid of jittered points (gridX × gridY cells over the
        // tile, wrapping around it), the nearest one's cell and the offset to it.
        private struct Cell
        {
            public float F1;
            public float F2;
            public int Id;
            public float Dx;
            public float Dy;
        }

        private static Cell Voronoi(int seed, int x, int y, int gridX, int gridY)
        {
            float cellWidth = (float)TileSize / gridX;
            float cellHeight = (float)TileSize / gridY;
            int cx = Mathf.FloorToInt(x / cellWidth);
            int cy = Mathf.FloorToInt(y / cellHeight);
            var result = new Cell { F1 = float.MaxValue, F2 = float.MaxValue };
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    int ix = cx + dx;
                    int iy = cy + dy;
                    int wrappedX = (ix % gridX + gridX) % gridX;
                    int wrappedY = (iy % gridY + gridY) % gridY;
                    float px = (ix + 0.15f + 0.7f * Noise(seed, wrappedX, wrappedY)) * cellWidth;
                    float py = (iy + 0.15f + 0.7f * Noise(seed + 997, wrappedX, wrappedY)) * cellHeight;
                    float offsetX = x + 0.5f - px;
                    float offsetY = y + 0.5f - py;
                    float distance = Mathf.Sqrt(offsetX * offsetX + offsetY * offsetY);
                    if (distance < result.F1)
                    {
                        result.F2 = result.F1;
                        result.F1 = distance;
                        result.Id = wrappedX + wrappedY * gridX;
                        result.Dx = offsetX;
                        result.Dy = offsetY;
                    }
                    else if (distance < result.F2)
                    {
                        result.F2 = distance;
                    }
                }
            }

            return result;
        }

        // Smooth noise from 0 to 1 made of octaves, the first with `period` cells across the tile,
        // each next one twice as fine and half as strong; every octave wraps around the tile.
        private static float Fbm(int seed, int x, int y, int period, int octaves)
        {
            float sum = 0f;
            float weight = 0.5f;
            float total = 0f;
            for (int octave = 0; octave < octaves && period <= TileSize; octave++)
            {
                sum += SmoothNoise(seed + octave * 131, x, y, TileSize / period) * weight;
                total += weight;
                weight *= 0.5f;
                period *= 2;
            }

            return sum / total;
        }

        private static int Wrap(int value) => ((value % TileSize) + TileSize) % TileSize;

        private static float Clamp01(float value) => value < 0f ? 0f : value > 1f ? 1f : value;

        private static Color[] Palette(params int[] colours)
        {
            var palette = new Color[colours.Length];
            for (int i = 0; i < colours.Length; i++)
            {
                int c = colours[i];
                palette[i] = new Color(((c >> 16) & 0xFF) / 255f, ((c >> 8) & 0xFF) / 255f, (c & 0xFF) / 255f);
            }

            return palette;
        }
    }
}

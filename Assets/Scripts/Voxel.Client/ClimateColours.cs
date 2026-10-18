using UnityEngine;
using Voxel.Core;

namespace Voxel.Client
{
    // The colours the climate gives the world, shared by the blocks' shader (through ClimateTint)
    // and the maps, so that both show the same thing.
    internal static class ClimateColours
    {
        // Grass from the driest to the wettest land, and in the cold.
        private static readonly Color DryGrass = new Color(0.66f, 0.60f, 0.30f);
        private static readonly Color LushGrass = new Color(0.22f, 0.55f, 0.16f);
        private static readonly Color ColdGrass = new Color(0.42f, 0.58f, 0.52f);

        // Grass where it is hot and very wet (the jungle), and in marshes.
        private static readonly Color TropicalGrass = new Color(0.10f, 0.56f, 0.12f);
        private static readonly Color MarshGrass = new Color(0.33f, 0.40f, 0.16f);

        // Above this temperature and humidity both, grass turns towards TropicalGrass, fully TropicalRange higher.
        private const float TropicalStart = 0.6f;
        private const float TropicalRange = 0.2f;

        // Below this temperature grass turns towards ColdGrass, fully ColdRange lower.
        private const float ColdStart = 0.38f;
        private const float ColdRange = 0.3f;

        // Ramps of the map's climate layers.
        private static readonly Color Frozen = new Color(0.25f, 0.45f, 0.95f);
        private static readonly Color Mild = new Color(0.55f, 0.85f, 0.45f);
        private static readonly Color Scorching = new Color(0.95f, 0.35f, 0.2f);
        private static readonly Color Arid = new Color(0.88f, 0.74f, 0.45f);
        private static readonly Color Soaked = new Color(0.15f, 0.45f, 0.85f);

        // The colour of grass in a mild, fairly wet climate: what grass looks like before the climate is known.
        public static Color MildGrass => Grass(IntNoise.One / 2, IntNoise.One * 6 / 10);

        // Temperature and humidity in fixed point (IntNoise.One is 1), as Geography gives them; marsh,
        // from 0 to 1, the share of marsh in the place (Landscape.IsMarsh).
        public static Color Grass(int temperature, int humidity, float marsh = 0f)
        {
            float warmth = (float)temperature / IntNoise.One;
            float wetness = (float)humidity / IntNoise.One;
            Color colour = Color.Lerp(DryGrass, LushGrass, Mathf.SmoothStep(0f, 1f, wetness));
            float tropical = Mathf.Clamp01(Mathf.Min(warmth - TropicalStart, wetness - TropicalStart) / TropicalRange);
            colour = Color.Lerp(colour, TropicalGrass, Mathf.SmoothStep(0f, 1f, tropical));
            colour = Color.Lerp(colour, ColdGrass, Mathf.Clamp01((ColdStart - warmth) / ColdRange));
            return Color.Lerp(colour, MarshGrass, marsh);
        }

        public static Color TemperatureLayer(int temperature)
        {
            float warmth = (float)temperature / IntNoise.One;
            return warmth < 0.5f ? Color.Lerp(Frozen, Mild, warmth * 2f) : Color.Lerp(Mild, Scorching, warmth * 2f - 1f);
        }

        public static Color HumidityLayer(int humidity)
        {
            return Color.Lerp(Arid, Soaked, (float)humidity / IntNoise.One);
        }

        // Rough readings for players: a temperature in degrees Celsius and a humidity in percent.
        public static int Celsius(int temperature) => Mathf.RoundToInt(-12f + 48f * temperature / IntNoise.One);

        public static int Percent(int humidity) => Mathf.RoundToInt(100f * humidity / IntNoise.One);
    }
}

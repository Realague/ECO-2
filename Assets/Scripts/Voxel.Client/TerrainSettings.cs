using System;
using UnityEngine;
using Voxel.Core;

namespace Voxel.Client
{
    // Whether natural ground is drawn smoothed (SmoothTerrain: Surface Nets, textured from above and
    // the sides) or in blocks, and how smooth (Smoothing, SmoothTerrain.Strength: lower keeps steeper
    // slopes), chosen in the main menu's Options and kept on this computer (PlayerPrefs). Display
    // only: collisions and everything else keep the blocks. Smooth by default, at half strength.
    // Changed during a game, the world is meshed again (VoxelWorld) and the plants placed again.
    public static class TerrainSettings
    {
        public const float MinSmoothing = 0.1f;
        public const float MaxSmoothing = 1f;
        private const float DefaultSmoothing = 0.5f;
        private const string PrefKey = "smoothTerrain";
        private const string SmoothingKey = "terrainSmoothing";

        private static int smooth = -1;
        private static float smoothing = float.NaN;

        public static event Action Changed;

        public static bool Smooth
        {
            get
            {
                if (smooth < 0) smooth = PlayerPrefs.GetInt(PrefKey, 1) != 0 ? 1 : 0;
                return smooth == 1;
            }
        }

        // How far smoothed ground goes from the blocks, MinSmoothing to 1 (SmoothTerrain.Strength).
        public static float Smoothing
        {
            get
            {
                if (float.IsNaN(smoothing)) smoothing = ClampSmoothing(PlayerPrefs.GetFloat(SmoothingKey, DefaultSmoothing));
                return smoothing;
            }
        }

        public static void SetSmooth(bool value)
        {
            if (value == Smooth) return;

            smooth = value ? 1 : 0;
            PlayerPrefs.SetInt(PrefKey, smooth);
            PlayerPrefs.Save();
            Changed?.Invoke();
        }

        public static void SetSmoothing(float value)
        {
            value = ClampSmoothing(value);
            if (Mathf.Approximately(value, Smoothing)) return;

            smoothing = value;
            PlayerPrefs.SetFloat(SmoothingKey, value);
            PlayerPrefs.Save();
            Changed?.Invoke();
        }

        // Gives SmoothTerrain the strength chosen (main thread, while nothing is meshed).
        public static void Apply() => SmoothTerrain.Strength = Smoothing;

        private static float ClampSmoothing(float value) => float.IsNaN(value) ? DefaultSmoothing : Mathf.Clamp(value, MinSmoothing, MaxSmoothing);
    }
}

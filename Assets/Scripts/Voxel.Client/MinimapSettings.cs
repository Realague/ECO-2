using System;
using UnityEngine;

namespace Voxel.Client
{
    // How large the minimap is drawn, a share of its usual size (300 px wide on the 1920 x 1080
    // reference), chosen in the main menu's Options and kept on this computer (PlayerPrefs). It
    // shows the same 64 blocks around the player at any size.
    public static class MinimapSettings
    {
        public const float MinScale = 0.5f;
        public const float MaxScale = 2f;
        private const string PrefKey = "minimapScale";

        private static float scale = float.NaN;

        public static event Action Changed;

        public static float Scale
        {
            get
            {
                if (float.IsNaN(scale)) scale = Clamp(PlayerPrefs.GetFloat(PrefKey, 1f));
                return scale;
            }
        }

        public static void SetScale(float value)
        {
            value = Clamp(value);
            if (Mathf.Approximately(value, Scale)) return;

            scale = value;
            PlayerPrefs.SetFloat(PrefKey, value);
            PlayerPrefs.Save();
            Changed?.Invoke();
        }

        private static float Clamp(float value) => float.IsNaN(value) ? 1f : Mathf.Clamp(value, MinScale, MaxScale);
    }
}

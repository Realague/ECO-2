using System;
using UnityEngine;

namespace Voxel.Client
{
    // What the HUD shows, chosen in the main menu's Options and kept on this computer (PlayerPrefs):
    // for now whether the key hints at the bottom right are shown (HudBanner). The hammer's panel,
    // which takes their place while the hammer is in hand, shows either way.
    public static class HudSettings
    {
        private const string KeyHintsPrefKey = "showKeyHints";

        private static int keyHints = -1;

        public static event Action Changed;

        public static bool ShowKeyHints
        {
            get
            {
                if (keyHints < 0) keyHints = PlayerPrefs.GetInt(KeyHintsPrefKey, 1) != 0 ? 1 : 0;
                return keyHints == 1;
            }
        }

        public static void SetShowKeyHints(bool shown)
        {
            if (shown == ShowKeyHints) return;

            keyHints = shown ? 1 : 0;
            PlayerPrefs.SetInt(KeyHintsPrefKey, keyHints);
            PlayerPrefs.Save();
            Changed?.Invoke();
        }
    }
}

using System;
using UnityEngine;

namespace Voxel.Client
{
    // Whether natural ground is drawn smoothed (SmoothTerrain: Surface Nets, textured from above and
    // the sides) or in blocks, chosen in the main menu's Options and kept on this computer
    // (PlayerPrefs). Display only: collisions and everything else keep the blocks. Smooth by default.
    // Changed during a game, the world is meshed again (VoxelWorld) and the plants placed again.
    public static class TerrainSettings
    {
        private const string PrefKey = "smoothTerrain";

        private static int smooth = -1;

        public static event Action Changed;

        public static bool Smooth
        {
            get
            {
                if (smooth < 0) smooth = PlayerPrefs.GetInt(PrefKey, 1) != 0 ? 1 : 0;
                return smooth == 1;
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
    }
}

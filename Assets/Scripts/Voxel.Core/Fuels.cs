namespace Voxel.Core
{
    // What burns on a campfire, and for how long: every wood alike for now (coal will burn longer).
    public static class Fuels
    {
        public const float WoodSeconds = 30f;

        // How long one of an item burns, in real seconds; 0 if it does not burn.
        public static float SecondsOf(int item) => Items.IsWood(item) ? WoodSeconds : 0f;

        public static bool IsFuel(int item) => SecondsOf(item) > 0f;
    }
}

namespace Voxel.Core
{
    // How fast a player burns the calories in their stomach. It goes by real time and effort, never by
    // the simulation's clock, so speeding up the ecosystem does not starve anyone.
    public static class HungerRules
    {
        // At rest: a full stomach (3,000 kcal) lasts about an hour.
        public const float RestPerSecond = 0.8f;

        // Walking all the time doubles it, about half an hour for a full stomach.
        public const float PerBlockWalked = 0.15f;

        public const float PerBlockClimbed = 1f;

        public const float PerBlockBroken = 2f;

        public const float PerBlockPlaced = 1f;

        // A move longer than this in one frame is a teleport, which costs nothing.
        public const float LongestStep = 10f;

        // A starving player walks at half speed, and jumps just enough to climb a block, so that they
        // never stay stuck.
        public const float StarvingSpeedShare = 0.5f;

        public const float StarvingJumpHeight = 1.05f;
    }
}

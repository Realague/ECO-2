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

        // Each hit on a block costs more the harder the block (MiningRules.HitsToBreak): 0.75 kcal on
        // dirt, 1.5 on granite (6 for the block), 2 on gold (12).
        public const float PerHitBase = 0.5f;
        public const float PerHitPerHardness = 0.25f;

        public const float PerAxeHit = 1.5f;
        public const float PerScytheHit = 0.5f;
        public const float PerShovelHitOnStump = 1f;

        public const float PerTill = 2f;

        public const float PerBlockPlaced = 1f;

        // A move longer than this in one frame is a teleport, which costs nothing.
        public const float LongestStep = 10f;

        // A starving player walks at half speed, and jumps just enough to climb a block, so that they
        // never stay stuck.
        public const float StarvingSpeedShare = 0.5f;

        public const float StarvingJumpHeight = 1.05f;

        // The calories of one hit on a block that takes hardness hits to break.
        public static float PerHit(int hardness) => PerHitBase + PerHitPerHardness * hardness;
    }
}

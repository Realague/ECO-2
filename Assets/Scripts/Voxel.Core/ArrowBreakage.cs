namespace Voxel.Core
{
    // Arrows do not last for ever: each one that lands may break, more often on hard ground than in
    // soft. A broken arrow is gone (BowSync shows splinters); one that hits an animal is lost in it.
    public static class ArrowBreakage
    {
        // Soft ground (one hit to break: dirt, grass, sand, snow, mud, tilled soil).
        public const float OnSoftGround = 0.1f;

        // Firm ground (gravel, clay, ice).
        public const float OnFirmGround = 0.2f;

        // Rock, ores and bedrock.
        public const float OnRock = 0.35f;

        // The chance that an arrow stopped by this block breaks, by how hard the block is.
        public static float ChanceOnBlock(ushort block)
        {
            int hits = MiningRules.HitsToBreak(block);
            if (hits == 1) return OnSoftGround;
            return hits == 2 ? OnFirmGround : OnRock;
        }

        // roll: a number drawn evenly from [0, 1).
        public static bool Breaks(float chance, double roll) => roll < chance;
    }
}

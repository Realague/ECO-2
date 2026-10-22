using System;

namespace Voxel.Core
{
    // How mining and felling go: one click is one hit, at most one hit a swing; a block takes hits by
    // its hardness, a tree by the wood in it, a stump a few shovel hits. Damage shows as cracks, and
    // wears off when a block or plant is left alone for a while.
    public static class MiningRules
    {
        // A swing, and so the shortest time between two hits: three a second. Twice as long for a
        // starving player.
        public const float SwingSeconds = 1f / 3f;
        public const float StarvingSwingFactor = 2f;

        // A crack shows in this many stages before a block breaks.
        public const int CrackStages = 4;

        // Left alone this long, damage starts wearing off, one hit every DecayInterval.
        public const float DecayDelay = 5f;
        public const float DecayInterval = 1.5f;

        // Shovel hits to dig a stump out.
        public const int StumpHits = 3;

        // The fewest and most axe hits a tree takes, whatever its wood.
        public const int FewestFellingHits = 2;
        public const int MostFellingHits = 8;

        // Hits a block takes to break, by hardness; 0 for one that cannot be broken.
        public static int HitsToBreak(ushort block)
        {
            if (!Blocks.IsBreakable(block)) return 0;

            switch (Blocks.TypeOf(block))
            {
                case Blocks.Dirt:
                case Blocks.Grass:
                case Blocks.Sand:
                case Blocks.Snow:
                case Blocks.Mud:
                case Blocks.TilledSoil:
                    return 1;
                case Blocks.Gravel:
                case Blocks.Clay:
                case Blocks.Ice:
                    return 2;
                case Blocks.Limestone:
                case Blocks.Sandstone:
                case Blocks.Slate:
                case Blocks.RockSalt:
                    return 3;
                case Blocks.CoalOre:
                case Blocks.IronOre:
                case Blocks.CopperOre:
                case Blocks.TinOre:
                    return 5;
                case Blocks.GoldOre:
                    return 6;
                default:
                    // Granite, marble, basalt, flint.
                    return 4;
            }
        }

        // The wood a plant gives when felled: its species' yield for a grown one, less while young.
        public static int WoodOf(int woodYield, float scale) => (int)Math.Round(woodYield * scale, MidpointRounding.ToEven);

        // Axe hits to fell a plant giving this much wood; one scythe cut for one that gives none.
        public static int HitsToFell(int wood) => wood <= 0 ? 1 : Math.Clamp(wood, FewestFellingHits, MostFellingHits);

        // The time between two hits.
        public static float SwingInterval(bool starving) => starving ? SwingSeconds * StarvingSwingFactor : SwingSeconds;

        // The crack shown after hits of needed: 0 (none) to CrackStages, the last one on the hit before
        // it breaks. A block broken in one hit never cracks.
        public static int CrackStage(int hits, int needed)
        {
            if (needed <= 1 || hits <= 0) return 0;

            int stage = (int)Math.Ceiling((double)hits * CrackStages / (needed - 1));
            return Math.Clamp(stage, 1, CrackStages);
        }

        // The hits left after idleSeconds without a hit: none lost before DecayDelay, then one each
        // DecayInterval.
        public static int HitsAfterIdle(int hits, double idleSeconds)
        {
            if (idleSeconds <= DecayDelay) return hits;

            int lost = (int)Math.Floor((idleSeconds - DecayDelay) / DecayInterval) + 1;
            return Math.Max(0, hits - lost);
        }

        // Felled wood lies on the ground as logs, one wood each.
        public static int LogsOf(int wood) => Math.Max(0, wood);
    }
}

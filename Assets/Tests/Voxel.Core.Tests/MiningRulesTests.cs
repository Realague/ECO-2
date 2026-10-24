using NUnit.Framework;

namespace Voxel.Core.Tests
{
    public class MiningRulesTests
    {
        [Test]
        public void HitsToBreak_GoesWithHardness()
        {
            Assert.AreEqual(1, MiningRules.HitsToBreak(Blocks.Dirt));
            Assert.AreEqual(1, MiningRules.HitsToBreak(Blocks.Grass));
            Assert.AreEqual(1, MiningRules.HitsToBreak(Blocks.TilledSoil));
            Assert.AreEqual(2, MiningRules.HitsToBreak(Blocks.Gravel));
            Assert.AreEqual(2, MiningRules.HitsToBreak(Blocks.Ice));
            Assert.AreEqual(3, MiningRules.HitsToBreak(Blocks.Limestone));
            Assert.AreEqual(4, MiningRules.HitsToBreak(Blocks.Granite));
            Assert.AreEqual(4, MiningRules.HitsToBreak(Blocks.Flint));
            Assert.AreEqual(5, MiningRules.HitsToBreak(Blocks.IronOre));
            Assert.AreEqual(6, MiningRules.HitsToBreak(Blocks.GoldOre));
        }

        [Test]
        public void HitsToBreak_IsNoneForWhatCannotBeBroken()
        {
            Assert.AreEqual(0, MiningRules.HitsToBreak(Blocks.Bedrock));
            Assert.AreEqual(0, MiningRules.HitsToBreak(Blocks.Air));
            Assert.AreEqual(0, MiningRules.HitsToBreak(Blocks.Water));
        }

        [Test]
        public void EveryBreakableBlock_TakesHits_AndATool()
        {
            for (int type = 1; type <= Blocks.LastType; type++)
            {
                var block = (ushort)type;
                if (!Blocks.IsValid(block)) continue;

                bool breakable = Blocks.IsBreakable(block);
                Assert.AreEqual(breakable, MiningRules.HitsToBreak(block) > 0, $"block {type}");

                // A torch is the one block taken down by hand.
                bool byHand = Blocks.IsTorch(block);
                Assert.AreEqual(breakable && !byHand, ToolRules.ToBreak(block) != Items.None, $"block {type}");
            }
        }

        [Test]
        public void HitsToFell_GoWithTheWood_WithinBounds()
        {
            Assert.AreEqual(1, MiningRules.HitsToFell(0), "a plant without wood is cut at once");
            Assert.AreEqual(MiningRules.FewestFellingHits, MiningRules.HitsToFell(1));
            Assert.AreEqual(4, MiningRules.HitsToFell(4));
            Assert.AreEqual(MiningRules.MostFellingHits, MiningRules.HitsToFell(12));
        }

        [Test]
        public void WoodOf_GrowsWithTheTree()
        {
            Assert.AreEqual(4, MiningRules.WoodOf(4, 1f));
            Assert.AreEqual(2, MiningRules.WoodOf(4, 0.5f));
            Assert.AreEqual(0, MiningRules.WoodOf(4, 0.125f));
        }

        [Test]
        public void CrackStage_RisesWithHits_AndIsFullOnTheLastHitBeforeBreaking()
        {
            Assert.AreEqual(0, MiningRules.CrackStage(0, 4));
            Assert.AreEqual(0, MiningRules.CrackStage(1, 1), "a block broken in one hit never cracks");
            int last = 0;
            for (int hits = 1; hits < 6; hits++)
            {
                int stage = MiningRules.CrackStage(hits, 6);
                Assert.GreaterOrEqual(stage, last);
                last = stage;
            }

            Assert.AreEqual(MiningRules.CrackStages, MiningRules.CrackStage(5, 6));
            Assert.AreEqual(MiningRules.CrackStages, MiningRules.CrackStage(1, 2));
        }

        [Test]
        public void Damage_WearsOff_OnlyAfterBeingLeftAlone()
        {
            Assert.AreEqual(3, MiningRules.HitsAfterIdle(3, MiningRules.DecayDelay));
            Assert.AreEqual(2, MiningRules.HitsAfterIdle(3, MiningRules.DecayDelay + 0.1));
            Assert.AreEqual(1, MiningRules.HitsAfterIdle(3, MiningRules.DecayDelay + MiningRules.DecayInterval + 0.1));
            Assert.AreEqual(0, MiningRules.HitsAfterIdle(3, 60.0));
        }

        [Test]
        public void SwingInterval_IsTwiceAsLong_WhenStarving()
        {
            Assert.AreEqual(MiningRules.SwingSeconds * MiningRules.StarvingSwingFactor, MiningRules.SwingInterval(true), 1e-6f);
            Assert.AreEqual(MiningRules.SwingSeconds, MiningRules.SwingInterval(false), 1e-6f);
        }

        [Test]
        public void HitCalories_RiseWithHardness()
        {
            Assert.Less(HungerRules.PerHit(MiningRules.HitsToBreak(Blocks.Dirt)), HungerRules.PerHit(MiningRules.HitsToBreak(Blocks.Granite)));
            Assert.Less(HungerRules.PerHit(MiningRules.HitsToBreak(Blocks.Granite)), HungerRules.PerHit(MiningRules.HitsToBreak(Blocks.GoldOre)));
        }

        [Test]
        public void Pacer_KeepsHitsToOneASwing_ButAcceptsTwoBunchedTogether()
        {
            var pacer = new HitPacer();
            const double interval = 0.3;
            Assert.IsTrue(pacer.TryHit(10.0, interval));
            Assert.IsTrue(pacer.TryHit(10.0, interval), "a second hit bunched by the network");
            Assert.IsFalse(pacer.TryHit(10.0, interval), "but not a third");

            // Clicking ten times a second for three seconds gives no more than one hit a swing.
            var fast = new HitPacer();
            int accepted = 0;
            for (int i = 0; i < 30; i++)
            {
                if (fast.TryHit(i * 0.1, interval)) accepted++;
            }

            Assert.LessOrEqual(accepted, 3.0 / interval + 2);
        }

        [Test]
        public void Damage_IsCounted_SetAndSweptAway()
        {
            var damage = new MiningDamage<BlockPos>();
            var pos = new BlockPos(1, 2, 3);
            Assert.AreEqual(1, damage.AddHit(pos, 0.0));
            Assert.AreEqual(2, damage.AddHit(pos, 0.5));
            Assert.AreEqual(2, damage.Hits(pos, 1.0));

            damage.Set(pos, 1, 2.0);
            Assert.AreEqual(1, damage.Hits(pos, 2.0));

            damage.Sweep(100.0);
            Assert.AreEqual(0, damage.Count, "worn off");
        }
    }
}

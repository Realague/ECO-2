using NUnit.Framework;
using Voxel.Core;

namespace Voxel.Core.Tests
{
    public class ArrowBreakageTests
    {
        [Test]
        public void HarderGround_BreaksMoreArrows()
        {
            float soft = ArrowBreakage.ChanceOnBlock(Blocks.Grass);
            float firm = ArrowBreakage.ChanceOnBlock(Blocks.Gravel);
            float rock = ArrowBreakage.ChanceOnBlock(Blocks.Stone);

            Assert.Less(soft, firm);
            Assert.Less(firm, rock);
            Assert.AreEqual(rock, ArrowBreakage.ChanceOnBlock(Blocks.Bedrock), "unbreakable bedrock is rock too");
            Assert.AreEqual(soft, ArrowBreakage.ChanceOnBlock(Blocks.Mud));
        }

        [Test]
        public void Breaks_WhenTheRollIsUnderTheChance()
        {
            Assert.IsTrue(ArrowBreakage.Breaks(0.25f, 0.0));
            Assert.IsTrue(ArrowBreakage.Breaks(0.25f, 0.2499));
            Assert.IsFalse(ArrowBreakage.Breaks(0.25f, 0.25));
            Assert.IsFalse(ArrowBreakage.Breaks(0f, 0.0), "a chance of 0 never breaks one");
        }
    }
}

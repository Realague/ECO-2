using NUnit.Framework;

namespace Voxel.Core.Tests
{
    public class CreativeRulesTests
    {
        // A click in the creative catalog takes one tool or storage, a stack of anything else.
        [Test]
        public void AStack_IsOneToolOrStorage_OrTwentyOfAnythingElse()
        {
            Assert.AreEqual(1, CreativeRules.StackOf(Items.Hammer));
            Assert.AreEqual(1, CreativeRules.StackOf(Items.Chest));
            Assert.AreEqual(1, CreativeRules.StackOf(Items.CarpentryTable));
            Assert.AreEqual(CreativeRules.Stack, CreativeRules.StackOf(Blocks.Stone));
            Assert.AreEqual(CreativeRules.Stack, CreativeRules.StackOf(Items.Arrow));
            Assert.AreEqual(CreativeRules.Stack, CreativeRules.StackOf(Items.Seed(0)));
        }

        // A full stomach for a player switching to creative mode, its balance kept (or even when empty).
        [Test]
        public void Fill_FillsTheStomach_KeepingItsBalance()
        {
            var stomach = new Stomach();
            stomach.Burn(Stomach.Capacity);
            Assert.IsTrue(stomach.IsStarving);

            stomach.Fill();
            Assert.AreEqual(Stomach.Capacity, stomach.Calories);
            Assert.AreEqual(0.25f, stomach.Share(0), 1e-5f, "an even balance after an empty stomach");
        }
    }
}

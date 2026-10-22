using NUnit.Framework;

namespace Voxel.Core.Tests
{
    public class ToolRulesTests
    {
        [Test]
        public void SoftGround_NeedsAShovel_AndEverythingHardAPickaxe()
        {
            foreach (ushort soft in new[] { Blocks.Dirt, Blocks.Grass, Blocks.Sand, Blocks.Snow, Blocks.Gravel, Blocks.Clay, Blocks.Mud, Blocks.TilledSoil })
            {
                Assert.AreEqual(Items.Shovel, ToolRules.ToBreak(soft), Items.NameOf(soft));
            }

            foreach (ushort hard in new[] { Blocks.Granite, Blocks.Basalt, Blocks.Limestone, Blocks.Marble, Blocks.CoalOre, Blocks.GoldOre, Blocks.Ice })
            {
                Assert.AreEqual(Items.Pickaxe, ToolRules.ToBreak(hard), Items.NameOf(hard));
            }

            Assert.AreEqual(Items.None, ToolRules.ToBreak(Blocks.Bedrock), "cannot be broken");
            Assert.AreEqual(Items.None, ToolRules.ToBreak(Blocks.Water));
            Assert.AreEqual(Items.None, ToolRules.ToBreak(Blocks.Air));
        }

        [Test]
        public void AHoe_TillsSoil_ButNotSandOrRock()
        {
            Assert.IsTrue(ToolRules.CanTill(Blocks.Grass));
            Assert.IsTrue(ToolRules.CanTill(Blocks.Dirt));
            Assert.IsTrue(ToolRules.CanTill(Blocks.Mud));
            Assert.IsTrue(ToolRules.CanTill(Blocks.Clay));
            Assert.IsFalse(ToolRules.CanTill(Blocks.Sand));
            Assert.IsFalse(ToolRules.CanTill(Blocks.Granite));
            Assert.IsFalse(ToolRules.CanTill(Blocks.TilledSoil), "already tilled");
        }

        [Test]
        public void Plants_NeedAnAxeForWood_AScytheForTheRest_AndFruitNothing()
        {
            Assert.AreEqual(Items.Axe, ToolRules.ToFell(true));
            Assert.AreEqual(Items.Scythe, ToolRules.ToFell(false));
            Assert.AreEqual(Items.Scythe, ToolRules.ToHarvest(true), "an annual crop is cut");
            Assert.AreEqual(Items.None, ToolRules.ToHarvest(false), "fruit is picked by hand");
        }

        [Test]
        public void TilledSoil_GivesDirt_AndToolsAndArrowsAreItems()
        {
            Assert.AreEqual((int)Blocks.Dirt, Items.DropOf(Blocks.TilledSoil));
            Assert.IsTrue(Items.IsValid(Items.Arrow));
            foreach (int tool in Items.Tools)
            {
                Assert.IsTrue(Items.IsTool(tool));
                Assert.IsTrue(Items.IsValid(tool));
                Assert.AreNotEqual("Unknown item", Items.NameOf(tool));
            }

            Assert.IsFalse(Items.IsTool(Items.Arrow));
        }
    }
}

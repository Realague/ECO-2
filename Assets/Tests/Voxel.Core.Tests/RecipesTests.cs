using NUnit.Framework;

namespace Voxel.Core.Tests
{
    public class RecipesTests
    {
        private const int PlanksRecipe = 0;
        private const int BricksRecipe = 1;
        private const int TimberedRecipe = 2;

        [Test]
        public void TryCraft_WithTheSkillAndTheInputs_ConsumesInputsAndAddsTheOutput()
        {
            var inventory = new Inventory();
            inventory.Add(Items.Wood, 3);

            Assert.IsTrue(Recipes.TryCraft(inventory, Speciality.Carpenter, PlanksRecipe));

            Assert.AreEqual(2, inventory.Count(Items.Wood));
            Assert.AreEqual(2, inventory.Count(Blocks.Planks));
        }

        [TestCase(Speciality.None)]
        [TestCase(Speciality.Mason)]
        [TestCase(Speciality.Gardener)]
        public void TryCraft_WithoutTheSkill_FailsAndKeepsTheInputs(Speciality speciality)
        {
            var inventory = new Inventory();
            inventory.Add(Items.Wood, 3);

            Assert.IsFalse(Recipes.TryCraft(inventory, speciality, PlanksRecipe));

            Assert.AreEqual(3, inventory.Count(Items.Wood));
            Assert.AreEqual(0, inventory.Count(Blocks.Planks));
        }

        [Test]
        public void TryCraft_WithoutEnoughInputs_FailsAndKeepsTheInputs()
        {
            var inventory = new Inventory();
            inventory.Add(Blocks.Stone, 1);

            Assert.IsFalse(Recipes.TryCraft(inventory, Speciality.Mason, BricksRecipe));

            Assert.AreEqual(1, inventory.Count(Blocks.Stone));
            Assert.AreEqual(0, inventory.Count(Blocks.Bricks));
        }

        [Test]
        public void TryCraft_WithOnlyOneOfTwoInputs_FailsAndKeepsTheInputs()
        {
            var inventory = new Inventory();
            inventory.Add(Blocks.Planks, 4);

            Assert.IsFalse(Recipes.TryCraft(inventory, Speciality.Carpenter, TimberedRecipe));

            Assert.AreEqual(4, inventory.Count(Blocks.Planks));
        }

        [TestCase(-1)]
        [TestCase(99)]
        public void TryCraft_UnknownRecipe_Fails(int recipeIndex)
        {
            var inventory = new Inventory();
            inventory.Add(Items.Wood, 3);

            Assert.IsFalse(Recipes.TryCraft(inventory, Speciality.Carpenter, recipeIndex));

            Assert.AreEqual(3, inventory.Count(Items.Wood));
        }

        // The heart of specialisation: timbered blocks need the work of two different players.
        [Test]
        public void Timbered_NeedsTheWorkOfTwoSpecialities()
        {
            var carpenter = new Inventory();
            var mason = new Inventory();
            carpenter.Add(Items.Wood, 1);
            mason.Add(Blocks.Stone, 2);

            Assert.IsTrue(Recipes.TryCraft(carpenter, Speciality.Carpenter, PlanksRecipe));
            Assert.IsTrue(Recipes.TryCraft(mason, Speciality.Mason, BricksRecipe));
            Assert.IsFalse(Recipes.TryCraft(carpenter, Speciality.Carpenter, TimberedRecipe));
            Assert.IsFalse(Recipes.TryCraft(mason, Speciality.Mason, TimberedRecipe));

            Assert.IsTrue(Inventory.Transfer(mason, carpenter, Blocks.Bricks, 1));
            Assert.IsTrue(Recipes.TryCraft(carpenter, Speciality.Carpenter, TimberedRecipe));

            Assert.AreEqual(2, carpenter.Count(Blocks.Timbered));
            Assert.AreEqual(1, carpenter.Count(Blocks.Planks));
            Assert.AreEqual(0, carpenter.Count(Blocks.Bricks));
        }

        [Test]
        public void NoSpecialityCanMakeEveryInputOfTimbered()
        {
            foreach (Speciality speciality in Specialities.Choices)
            {
                bool planks = Recipes.HasSkill(Recipes.All[PlanksRecipe], speciality);
                bool bricks = Recipes.HasSkill(Recipes.All[BricksRecipe], speciality);

                Assert.IsFalse(planks && bricks, $"{speciality} makes both planks and bricks.");
            }
        }

        [Test]
        public void EveryRecipe_UsesAndProducesValidItems()
        {
            foreach (Recipe recipe in Recipes.All)
            {
                Assert.IsTrue(Items.IsValid(recipe.Output));
                Assert.Greater(recipe.OutputCount, 0);
                foreach ((int item, int count) in recipe.Inputs)
                {
                    Assert.IsTrue(Items.IsValid(item));
                    Assert.Greater(count, 0);
                }
            }
        }
    }
}

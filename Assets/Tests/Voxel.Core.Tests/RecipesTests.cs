using System.Collections.Generic;
using NUnit.Framework;

namespace Voxel.Core.Tests
{
    public class RecipesTests
    {
        private static readonly ItemWeights Weights = new ItemWeights(new[] { 0.2f, 1f });

        private static readonly int Oak = Items.WoodOf(0);
        private static readonly int Birch = Items.WoodOf(2);

        private static Recipe RecipeFor(int output)
        {
            foreach (Recipe recipe in Recipes.All)
            {
                if (recipe.Output == output) return recipe;
            }

            Assert.Fail($"No recipe for {Items.NameOf(output)}");
            return null;
        }

        [Test]
        public void Groups_KnowTheirItems()
        {
            var wood = new Ingredient(IngredientGroup.Wood, 1);
            var stone = new Ingredient(IngredientGroup.Stone, 1);

            Assert.IsTrue(wood.Matches(Oak));
            Assert.IsTrue(wood.Matches(Birch));
            Assert.IsFalse(wood.Matches(Blocks.Granite));
            Assert.IsTrue(stone.Matches(Blocks.Granite));
            Assert.IsTrue(stone.Matches(Blocks.Limestone));
            Assert.IsFalse(stone.Matches(Blocks.Dirt));
            Assert.IsFalse(stone.Matches(Blocks.CoalOre), "ores are not stone");
            Assert.IsFalse(stone.Matches(Items.None));
        }

        [Test]
        public void EveryRecipe_MakesAValidItem_FromIngredientsThatExist()
        {
            foreach (Recipe recipe in Recipes.All)
            {
                Assert.IsTrue(Items.IsValid(recipe.Output), Items.NameOf(recipe.Output));
                Assert.Greater(recipe.OutputCount, 0);
                Assert.Greater(recipe.Seconds, 0f);
                Assert.Greater(recipe.Calories, 0f);
                Assert.Greater(recipe.Ingredients.Count, 0);
            }

            Assert.Less(Recipes.All.Count, 255, "an index travels as a byte, 255 meaning none");
        }

        [Test]
        public void Check_SaysWhatIsMissing_ThenAllows()
        {
            Recipe axe = RecipeFor(Items.Axe);
            var bag = new Inventory();
            bag.Add(Oak, 6);
            bag.Add(Blocks.Granite, 5);

            Assert.AreEqual(CraftRefusal.Missing, Recipes.Check(bag, axe, null, Weights));
            bag.Add(Blocks.Limestone, 1);
            Assert.AreEqual(CraftRefusal.None, Recipes.Check(bag, axe, null, Weights), "rocks of any kind add up");
            Assert.AreEqual(6, bag.Count(Oak), "a check changes nothing");

            Assert.AreEqual(CraftRefusal.None, Recipes.TryCraft(bag, axe, null, Weights));
            Assert.AreEqual(1, bag.Count(Items.Axe));
            Assert.AreEqual(0, bag.Count(Oak));
            Assert.AreEqual(0, bag.Count(Blocks.Granite) + bag.Count(Blocks.Limestone));
        }

        [Test]
        public void Auto_TakesFromTheWoodHeldMost()
        {
            Recipe bow = RecipeFor(Items.Bow);
            var bag = new Inventory();
            bag.Add(Oak, 5);
            bag.Add(Birch, 9);

            Assert.AreEqual(14, Recipes.Have(bag, bow.Ingredients[0], Items.None));
            CollectionAssert.AreEqual(new List<int> { Birch, Oak }, Recipes.Options(bag, bow.Ingredients[0]));
            Assert.AreEqual(CraftRefusal.None, Recipes.TryCraft(bag, bow, null, Weights));
            Assert.AreEqual(0, bag.Count(Birch), "the most held first");
            Assert.AreEqual(2, bag.Count(Oak));
        }

        [Test]
        public void AChosenWood_IsTheOneUsed_AndMustCoverTheCountAlone()
        {
            Recipe chest = RecipeFor(Items.Chest);
            var bag = new Inventory();
            bag.Add(Oak, 12);
            bag.Add(Birch, 8);

            Assert.AreEqual(CraftRefusal.Missing, Recipes.Check(bag, chest, new[] { Birch }, Weights), "8 birch for 10");
            Assert.AreEqual(CraftRefusal.None, Recipes.TryCraft(bag, chest, new[] { Oak }, Weights));
            Assert.AreEqual(2, bag.Count(Oak));
            Assert.AreEqual(8, bag.Count(Birch));
            Assert.AreEqual(CraftRefusal.BadChoice, Recipes.Check(bag, chest, new[] { (int)Blocks.Granite }, Weights));
        }

        [Test]
        public void NoRoom_WhenTheBagIsTooHeavyForWhatItMakes()
        {
            Recipe torches = RecipeFor(Blocks.Torch);
            var bag = new Inventory();
            bag.Add(Oak, 4);
            bag.Add(Items.Produce(1), 30);

            Assert.AreEqual(CraftRefusal.NoRoom, Recipes.Check(bag, torches, null, Weights), "30 kg carried already");
            Assert.AreEqual(CraftRefusal.NoRoom, Recipes.TryCraft(bag, torches, null, Weights));
            Assert.AreEqual(4, bag.Count(Oak), "a refused craft takes nothing");
            bag.TryRemove(Items.Produce(1), 1);
            Assert.AreEqual(CraftRefusal.None, Recipes.Check(bag, torches, null, Weights));
        }
    }
}

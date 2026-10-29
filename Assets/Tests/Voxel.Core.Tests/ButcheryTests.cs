using NUnit.Framework;

namespace Voxel.Core.Tests
{
    public class ButcheryTests
    {
        private static int CountOf(Recipe recipe, int item)
        {
            foreach ((int output, int count) in recipe.Outputs)
            {
                if (output == item) return count;
            }

            return 0;
        }

        [Test]
        public void Animals_AreInSpeciesOrder_WithOneRecipeEach()
        {
            Assert.AreEqual(Butchery.Animals.Count, Recipes.Butchery.Count);
            for (int i = 0; i < Butchery.Animals.Count; i++)
            {
                Assert.AreEqual(i, Butchery.Animals[i].Index);
                Recipe recipe = Recipes.Butchery[i];
                Assert.AreEqual(1, recipe.Ingredients.Count);
                Assert.AreEqual(Items.Carcass(i), recipe.Ingredients[0].Item);
                Assert.AreEqual(i, Butchery.RecipeOf(Items.Carcass(i)));
                Assert.Greater(recipe.Seconds, 0f);
                Assert.Greater(recipe.Calories, 0f);
                foreach ((int item, int count) in recipe.Outputs)
                {
                    Assert.IsTrue(Items.IsValid(item), Items.NameOf(item));
                    Assert.Greater(count, 0);
                    Assert.IsTrue(StorageRules.AcceptsInChest(item));
                }
            }

            Assert.AreEqual(-1, Butchery.RecipeOf(Items.Carcass(Butchery.Animals.Count)));
            Assert.AreEqual(-1, Butchery.RecipeOf(Items.Chest));
        }

        [Test]
        public void ATable_GivesMore_TheBiggerTheAnimal()
        {
            Recipe rabbit = Recipes.Butchery[0];
            Recipe deer = Recipes.Butchery[2];
            Recipe bear = Recipes.Butchery[5];

            Assert.AreEqual(1, CountOf(rabbit, Items.Meat(MeatKind.SmallGame)));
            Assert.AreEqual(1, CountOf(rabbit, Items.Hide));
            Assert.AreEqual(0, CountOf(rabbit, Items.Fat));
            Assert.AreEqual(4, CountOf(deer, Items.Meat(MeatKind.LargeGame)));
            Assert.AreEqual(2, CountOf(deer, Items.Hide));
            Assert.AreEqual(2, CountOf(deer, Items.Bones));
            Assert.AreEqual(1, CountOf(deer, Items.Fat));
            Assert.AreEqual(6, CountOf(bear, Items.Meat(MeatKind.LargeGame)));
            Assert.AreEqual(3, CountOf(bear, Items.Hide));
            Assert.AreEqual(Items.Meat(MeatKind.LargeGame), deer.Output, "the meat first");
        }

        [Test]
        public void BirdsAndFish_GiveNoHide()
        {
            Assert.AreEqual(0, CountOf(Recipes.Butchery[10], Items.Hide), "fish");
            Assert.AreEqual(0, CountOf(Recipes.Butchery[11], Items.Hide), "duck");
            Assert.AreEqual(1, CountOf(Recipes.Butchery[10], Items.Meat(MeatKind.Fish)));
            Assert.AreEqual(1, CountOf(Recipes.Butchery[11], Items.Meat(MeatKind.Poultry)));
        }

        [Test]
        public void AKnife_TakesHalfTheMeat_RoundedUp()
        {
            Assert.AreEqual(1, Butchery.FieldMeat(Butchery.Animals[0]), "rabbit: 1 of 1");
            Assert.AreEqual(1, Butchery.FieldMeat(Butchery.Animals[1]), "fox: 1 of 2");
            Assert.AreEqual(2, Butchery.FieldMeat(Butchery.Animals[2]), "deer: 2 of 4");
            Assert.AreEqual(3, Butchery.FieldMeat(Butchery.Animals[5]), "bear: 3 of 6");
            Assert.AreEqual(Items.Knife, ToolRules.ToButcher);
        }

        [Test]
        public void SizeFollowsHealth_AsTheCarcassWeightDoes()
        {
            Assert.AreEqual(CarcassSize.Small, Butchery.SizeFor(30f));
            Assert.AreEqual(CarcassSize.Medium, Butchery.SizeFor(50f));
            Assert.AreEqual(CarcassSize.Large, Butchery.SizeFor(60f));
            Assert.AreEqual(CarcassSize.Huge, Butchery.SizeFor(100f));
        }

        [Test]
        public void Meat_IsAnItem_ThatIsNotFoodUntilCooked()
        {
            for (int kind = 0; kind < Butchery.MeatKindCount; kind++)
            {
                int meat = Items.Meat((MeatKind)kind);
                Assert.IsTrue(Items.IsMeat(meat));
                Assert.IsTrue(Items.IsValid(meat));
                Assert.AreEqual((MeatKind)kind, Items.MeatKindOf(meat));
                Assert.IsFalse(Dishes.FoodOf(meat).IsFood, "raw meat is not eaten");
                Assert.Greater(new ItemWeights(new float[0]).Of(meat), 0f);
            }

            Assert.IsFalse(Items.IsValid(Items.Meat((MeatKind)Butchery.MeatKindCount)));
            Assert.AreEqual("Large game meat", Items.NameOf(Items.Meat(MeatKind.LargeGame)));
            Assert.IsTrue(Items.IsTool(Items.Knife));
            Assert.AreEqual("Knife", Items.NameOf(Items.Knife));
        }

        [Test]
        public void MeatDishes_AreCookedFromMeat()
        {
            bool found = false;
            foreach (Recipe recipe in Recipes.Cooking)
            {
                foreach (Ingredient ingredient in recipe.Ingredients)
                {
                    if (Items.IsMeat(ingredient.Item)) found = true;
                }
            }

            Assert.IsTrue(found);
        }
    }
}

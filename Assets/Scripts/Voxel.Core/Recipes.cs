using System;
using System.Collections.Generic;

namespace Voxel.Core
{
    // What an ingredient may be: one item, or any item of a kind (any wood, any rock).
    public enum IngredientGroup : byte
    {
        None,
        Wood,
        Stone
    }

    // Why a player cannot craft a recipe, or why an order waits (Busy, TooHungry and QueueFull are
    // only CraftingSync's).
    public enum CraftRefusal : byte
    {
        None,
        Missing,
        NoRoom,
        BadChoice,
        Busy,
        TooHungry,
        Unknown,
        QueueFull
    }

    public readonly struct Ingredient
    {
        public readonly int Item;
        public readonly IngredientGroup Group;
        public readonly int Count;

        public Ingredient(int item, int count)
        {
            Item = item;
            Group = IngredientGroup.None;
            Count = count;
        }

        public Ingredient(IngredientGroup group, int count)
        {
            Item = Items.None;
            Group = group;
            Count = count;
        }

        public bool IsGroup => Group != IngredientGroup.None;

        // Whether an item may go in for this ingredient.
        public bool Matches(int item)
        {
            switch (Group)
            {
                case IngredientGroup.Wood: return Items.IsWood(item);
                case IngredientGroup.Stone: return Recipes.IsStone(item);
                default: return item == Item && item != Items.None;
            }
        }
    }

    public sealed class Recipe
    {
        public Recipe(int output, int outputCount, float seconds, float calories, params Ingredient[] ingredients)
            : this(new[] { (output, outputCount) }, seconds, calories, ingredients)
        {
        }

        // A recipe that makes several things at once (a butcher's table: meat, hide, bones, fat).
        public Recipe((int Item, int Count)[] outputs, float seconds, float calories, params Ingredient[] ingredients)
        {
            Outputs = outputs;
            Seconds = seconds;
            Calories = calories;
            Ingredients = ingredients;
        }

        // What it makes; Output and OutputCount are the first (the only one but at a butcher's table).
        public IReadOnlyList<(int Item, int Count)> Outputs { get; }
        public int Output => Outputs[0].Item;
        public int OutputCount => Outputs[0].Count;

        // How long it takes, in real seconds, and what it burns once made.
        public float Seconds { get; }
        public float Calories { get; }
        public IReadOnlyList<Ingredient> Ingredients { get; }
    }

    // What players craft from their bag (CraftingSync): the basic tools, arrows, torches, storage and
    // the stations, from wood and rock; what campfires cook (Cooking) and what butcher's tables cut up
    // (Butchery). For an ingredient of a group the player chooses the item that goes in (a
    // choice per ingredient: Items.None for Auto, which takes from what they have most of first, the
    // lowest item first when even, so it is the same on every machine and rare woods are kept; a
    // chosen item must cover the whole count by itself). A recipe's index travels on the network.
    public static class Recipes
    {
        public static readonly IReadOnlyList<Recipe> All = new[]
        {
            new Recipe(Items.Shovel, 1, 3f, 125f, new Ingredient(IngredientGroup.Wood, 6), new Ingredient(IngredientGroup.Stone, 3)),
            new Recipe(Items.Pickaxe, 1, 4f, 150f, new Ingredient(IngredientGroup.Wood, 6), new Ingredient(IngredientGroup.Stone, 9)),
            new Recipe(Items.Axe, 1, 4f, 150f, new Ingredient(IngredientGroup.Wood, 6), new Ingredient(IngredientGroup.Stone, 6)),
            new Recipe(Items.Scythe, 1, 3f, 125f, new Ingredient(IngredientGroup.Wood, 6), new Ingredient(IngredientGroup.Stone, 6)),
            new Recipe(Items.Hoe, 1, 3f, 125f, new Ingredient(IngredientGroup.Wood, 6), new Ingredient(IngredientGroup.Stone, 3)),
            new Recipe(Items.Bow, 1, 4f, 125f, new Ingredient(IngredientGroup.Wood, 12)),
            new Recipe(Items.Arrow, 5, 2f, 50f, new Ingredient(IngredientGroup.Wood, 3), new Ingredient(IngredientGroup.Stone, 3)),
            new Recipe(Blocks.Torch, 4, 2f, 50f, new Ingredient(IngredientGroup.Wood, 4)),
            new Recipe(Items.SmallStockpile, 1, 5f, 200f, new Ingredient(IngredientGroup.Wood, 10)),
            new Recipe(Items.Chest, 1, 6f, 250f, new Ingredient(IngredientGroup.Wood, 10)),
            new Recipe(Items.Campfire, 1, 5f, 200f, new Ingredient(IngredientGroup.Stone, 12), new Ingredient(IngredientGroup.Wood, 6)),
            new Recipe(Items.Knife, 1, 2f, 50f, new Ingredient(IngredientGroup.Wood, 2), new Ingredient(IngredientGroup.Stone, 2)),
            new Recipe(Items.ButcherTable, 1, 6f, 250f, new Ingredient(IngredientGroup.Wood, 10), new Ingredient(IngredientGroup.Stone, 4))
        };

        // What a campfire cooks (Station): one recipe per dish, in Dishes.All's order; there Calories is
        // the work players put in and Seconds the time it then cooks.
        public static readonly IReadOnlyList<Recipe> Cooking = CookingRecipes();

        // What a butcher's table cuts up: one recipe per animal species, in Butchery.Animals' order,
        // each making several things.
        public static readonly IReadOnlyList<Recipe> Butchery = Voxel.Core.Butchery.Recipes();

        private static Recipe[] CookingRecipes()
        {
            var recipes = new Recipe[Dishes.All.Count];
            for (int i = 0; i < recipes.Length; i++)
            {
                Dish dish = Dishes.All[i];
                var ingredients = new Ingredient[dish.Ingredients.Count];
                for (int n = 0; n < ingredients.Length; n++)
                {
                    ingredients[n] = dish.Ingredients[n];
                }

                recipes[i] = new Recipe(Items.Dish(i), 1, dish.Seconds, dish.Labour, ingredients);
            }

            return recipes;
        }

        // The rocks that count as stone: the cover rocks and granite, not ores, flint or salt.
        private static readonly ushort[] Stones = { Blocks.Granite, Blocks.Basalt, Blocks.Limestone, Blocks.Marble, Blocks.Sandstone, Blocks.Slate };

        public static bool IsStone(int item) => item > 0 && item <= ushort.MaxValue && Array.IndexOf(Stones, (ushort)item) >= 0;

        public static bool TryGet(int index, out Recipe recipe)
        {
            recipe = index >= 0 && index < All.Count ? All[index] : null;
            return recipe != null;
        }

        // A choice per ingredient, all Auto.
        public static int[] AutoChoices(Recipe recipe) => new int[recipe.Ingredients.Count];

        // The items of an ingredient's group the player has, most first (Auto's order); for a single
        // item, that item if they have it.
        public static List<int> Options(Inventory inventory, Ingredient ingredient)
        {
            var options = new List<int>();
            foreach (KeyValuePair<int, int> entry in inventory.Entries)
            {
                if (entry.Value > 0 && ingredient.Matches(entry.Key)) options.Add(entry.Key);
            }

            options.Sort((a, b) =>
            {
                int byCount = inventory.Count(b).CompareTo(inventory.Count(a));
                return byCount != 0 ? byCount : a.CompareTo(b);
            });
            return options;
        }

        // How many of what an ingredient takes the player has: all of its group for Auto, else the
        // chosen item (0 if it does not belong).
        public static int Have(Inventory inventory, Ingredient ingredient, int choice)
        {
            if (choice != Items.None) return ingredient.IsGroup && ingredient.Matches(choice) ? inventory.Count(choice) : 0;
            if (!ingredient.IsGroup) return inventory.Count(ingredient.Item);

            long total = 0;
            foreach (int item in Options(inventory, ingredient))
            {
                total += inventory.Count(item);
            }

            return (int)Math.Min(int.MaxValue, total);
        }

        // Whether the player could craft a recipe now, with those choices (null: all Auto): everything
        // there, and room in the bag for what it makes once the ingredients are out.
        public static CraftRefusal Check(Inventory inventory, Recipe recipe, IReadOnlyList<int> choices, ItemWeights weights)
        {
            var copy = new Inventory();
            copy.CopyFrom(inventory);
            return Consume(copy, recipe, choices, weights);
        }

        // Takes the ingredients and adds what the recipe makes; else changes nothing and says why.
        public static CraftRefusal TryCraft(Inventory inventory, Recipe recipe, IReadOnlyList<int> choices, ItemWeights weights)
        {
            CraftRefusal refusal = Check(inventory, recipe, choices, weights);
            if (refusal != CraftRefusal.None) return refusal;

            Consume(inventory, recipe, choices, weights);
            inventory.Add(recipe.Output, recipe.OutputCount);
            return CraftRefusal.None;
        }

        // How many times over the player could craft a recipe now, one after another (each taking its
        // ingredients and adding what it makes), up to limit.
        public static int MaxCraftable(Inventory inventory, Recipe recipe, IReadOnlyList<int> choices, ItemWeights weights, int limit)
        {
            var copy = new Inventory();
            copy.CopyFrom(inventory);
            int made = 0;
            while (made < limit && Consume(copy, recipe, choices, weights) == CraftRefusal.None)
            {
                copy.Add(recipe.Output, recipe.OutputCount);
                made++;
            }

            return made;
        }

        // Takes the ingredients out of an inventory and checks the room left; a refusal may leave it
        // half emptied, so callers pass a copy unless it was checked.
        private static CraftRefusal Consume(Inventory inventory, Recipe recipe, IReadOnlyList<int> choices, ItemWeights weights)
        {
            for (int i = 0; i < recipe.Ingredients.Count; i++)
            {
                Ingredient ingredient = recipe.Ingredients[i];
                int choice = choices != null && i < choices.Count ? choices[i] : Items.None;
                if (choice != Items.None && (!ingredient.IsGroup || !ingredient.Matches(choice))) return CraftRefusal.BadChoice;
                if (Have(inventory, ingredient, choice) < ingredient.Count) return CraftRefusal.Missing;

                if (choice != Items.None)
                {
                    inventory.TryRemove(choice, ingredient.Count);
                    continue;
                }

                if (!ingredient.IsGroup)
                {
                    inventory.TryRemove(ingredient.Item, ingredient.Count);
                    continue;
                }

                int left = ingredient.Count;
                foreach (int item in Options(inventory, ingredient))
                {
                    int take = Math.Min(left, inventory.Count(item));
                    inventory.TryRemove(item, take);
                    left -= take;
                    if (left == 0) break;
                }
            }

            return CarryLimits.Room(inventory, recipe.Output, weights) >= recipe.OutputCount ? CraftRefusal.None : CraftRefusal.NoRoom;
        }
    }
}

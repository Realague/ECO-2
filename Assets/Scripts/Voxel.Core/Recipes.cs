using System.Collections.Generic;

namespace Voxel.Core
{
    // A player has a single speciality, chosen once and for all: this is what makes players
    // useful to one another.
    public enum Speciality : byte
    {
        None = 0,
        Carpenter = 1,
        Mason = 2,
        Gardener = 3
    }

    public static class Specialities
    {
        public static readonly IReadOnlyList<Speciality> Choices = new[] { Speciality.Carpenter, Speciality.Mason, Speciality.Gardener };

        public static bool IsChoice(Speciality speciality) =>
            speciality == Speciality.Carpenter || speciality == Speciality.Mason || speciality == Speciality.Gardener;

        public static string NameOf(Speciality speciality)
        {
            switch (speciality)
            {
                case Speciality.Carpenter: return "Carpenter";
                case Speciality.Mason: return "Mason";
                case Speciality.Gardener: return "Gardener";
                default: return "None";
            }
        }

        public static string DescriptionOf(Speciality speciality)
        {
            switch (speciality)
            {
                case Speciality.Carpenter: return "turns wood into planks";
                case Speciality.Mason: return "turns stone into bricks";
                case Speciality.Gardener: return "replants the seeds of trees and bushes";
                default: return "";
            }
        }
    }

    public sealed class Recipe
    {
        public Recipe(Speciality requires, int output, int outputCount, params (int Item, int Count)[] inputs)
        {
            Requires = requires;
            Output = output;
            OutputCount = outputCount;
            Inputs = inputs;
        }

        // Speciality.None: anyone can craft it.
        public Speciality Requires { get; }
        public int Output { get; }
        public int OutputCount { get; }
        public IReadOnlyList<(int Item, int Count)> Inputs { get; }
    }

    public static class Recipes
    {
        // Timbered blocks need planks and bricks: no player can produce both,
        // so one has to come to terms with someone of another speciality.
        public static readonly IReadOnlyList<Recipe> All = new[]
        {
            new Recipe(Speciality.Carpenter, Blocks.Planks, 2, (Items.Wood, 1)),
            new Recipe(Speciality.Mason, Blocks.Bricks, 1, (Blocks.Stone, 2)),
            new Recipe(Speciality.None, Blocks.Timbered, 2, (Blocks.Planks, 1), (Blocks.Bricks, 1))
        };

        public static bool HasSkill(Recipe recipe, Speciality speciality) =>
            recipe.Requires == Speciality.None || recipe.Requires == speciality;

        public static bool HasInputs(Recipe recipe, Inventory inventory)
        {
            foreach ((int item, int count) in recipe.Inputs)
            {
                if (!inventory.Has(item, count)) return false;
            }

            return true;
        }

        // Crafts the recipe once: removes the inputs and adds the output. Returns false, changing
        // nothing, if the recipe does not exist, if the speciality is missing or if an input is missing.
        public static bool TryCraft(Inventory inventory, Speciality speciality, int recipeIndex)
        {
            if (recipeIndex < 0 || recipeIndex >= All.Count) return false;

            Recipe recipe = All[recipeIndex];
            if (!HasSkill(recipe, speciality) || !HasInputs(recipe, inventory)) return false;

            foreach ((int item, int count) in recipe.Inputs)
            {
                inventory.TryRemove(item, count);
            }

            inventory.Add(recipe.Output, recipe.OutputCount);
            return true;
        }
    }
}

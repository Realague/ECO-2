using System.Collections.Generic;

namespace Voxel.Core
{
    // Un joueur n'a qu'une spécialité, choisie une fois pour toutes : c'est ce qui rend les joueurs
    // utiles les uns aux autres.
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
                case Speciality.Carpenter: return "Menuisier";
                case Speciality.Mason: return "Maçon";
                case Speciality.Gardener: return "Jardinier";
                default: return "Aucune";
            }
        }

        public static string DescriptionOf(Speciality speciality)
        {
            switch (speciality)
            {
                case Speciality.Carpenter: return "transforme le bois en planches";
                case Speciality.Mason: return "transforme la pierre en briques";
                case Speciality.Gardener: return "replante les graines d'arbres et de buissons";
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

        // Speciality.None : tout le monde peut fabriquer.
        public Speciality Requires { get; }
        public int Output { get; }
        public int OutputCount { get; }
        public IReadOnlyList<(int Item, int Count)> Inputs { get; }
    }

    public static class Recipes
    {
        // Les colombages demandent des planches et des briques : aucun joueur ne peut produire les deux,
        // il faut donc s'entendre avec quelqu'un d'une autre spécialité.
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

        // Fabrique une fois la recette : retire les ingrédients et ajoute le produit. Renvoie faux,
        // sans rien changer, si la recette n'existe pas, si la spécialité manque ou s'il manque un ingrédient.
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

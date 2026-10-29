using System.Collections.Generic;

namespace Voxel.Core
{
    // The kinds of raw meat, one item each (Items.Meat).
    public enum MeatKind : byte
    {
        SmallGame,
        Game,
        LargeGame,
        Poultry,
        Fish
    }

    // How big an animal is, by its health (AnimalSpeciesAsset.health, which also sets its carcass's
    // weight): 30, 50, 60, 100.
    public enum CarcassSize : byte
    {
        Small,
        Medium,
        Large,
        Huge
    }

    // An animal species as butchered: its index in EcosystemSync.animalAssets, the asset name that
    // index must have (EcosystemSync checks them at start), its meat and its size.
    public readonly struct ButcheredAnimal
    {
        public readonly int Index;
        public readonly string Asset;
        public readonly MeatKind Meat;
        public readonly CarcassSize Size;

        public ButcheredAnimal(int index, string asset, MeatKind meat, CarcassSize size)
        {
            Index = index;
            Asset = asset;
            Meat = meat;
            Size = size;
        }

        // Birds and fish have no hide worth taking.
        public bool HasHide => Meat != MeatKind.Poultry && Meat != MeatKind.Fish;
    }

    // What a carcass gives. On a butcher's table (Recipes.Butchery, one recipe per animal species, in
    // this order): its meat, hide, bones and fat by its size; with a knife in the field only half its
    // meat (rounded up) and nothing else.
    public static class Butchery
    {
        public const int MeatKindCount = 5;

        public static readonly IReadOnlyList<ButcheredAnimal> Animals = new[]
        {
            new ButcheredAnimal(0, "Rabbit", MeatKind.SmallGame, CarcassSize.Small),
            new ButcheredAnimal(1, "Fox", MeatKind.Game, CarcassSize.Medium),
            new ButcheredAnimal(2, "Deer", MeatKind.LargeGame, CarcassSize.Large),
            new ButcheredAnimal(3, "Reindeer", MeatKind.LargeGame, CarcassSize.Large),
            new ButcheredAnimal(4, "Wolf", MeatKind.Game, CarcassSize.Large),
            new ButcheredAnimal(5, "Bear", MeatKind.LargeGame, CarcassSize.Huge),
            new ButcheredAnimal(6, "Gazelle", MeatKind.Game, CarcassSize.Medium),
            new ButcheredAnimal(7, "Jackal", MeatKind.Game, CarcassSize.Medium),
            new ButcheredAnimal(8, "SnowHare", MeatKind.SmallGame, CarcassSize.Small),
            new ButcheredAnimal(9, "Jerboa", MeatKind.SmallGame, CarcassSize.Small),
            new ButcheredAnimal(10, "Fish", MeatKind.Fish, CarcassSize.Small),
            new ButcheredAnimal(11, "Duck", MeatKind.Poultry, CarcassSize.Small),
            new ButcheredAnimal(12, "Heron", MeatKind.Poultry, CarcassSize.Medium),
            new ButcheredAnimal(13, "Otter", MeatKind.Game, CarcassSize.Medium),
            new ButcheredAnimal(14, "Crow", MeatKind.Poultry, CarcassSize.Small),
            new ButcheredAnimal(15, "Vulture", MeatKind.Poultry, CarcassSize.Medium),
            new ButcheredAnimal(16, "Sparrow", MeatKind.Poultry, CarcassSize.Small),
            new ButcheredAnimal(17, "Tapir", MeatKind.LargeGame, CarcassSize.Large),
            new ButcheredAnimal(18, "Jaguar", MeatKind.Game, CarcassSize.Large),
            new ButcheredAnimal(19, "Crocodile", MeatKind.LargeGame, CarcassSize.Huge),
            new ButcheredAnimal(20, "Frog", MeatKind.SmallGame, CarcassSize.Small),
            new ButcheredAnimal(21, "Parrot", MeatKind.Poultry, CarcassSize.Small)
        };

        // By size: meat, hide, bones, fat, then the table's seconds and work (calories).
        private static readonly int[] MeatBySize = { 1, 2, 4, 6 };
        private static readonly int[] HideBySize = { 1, 1, 2, 3 };
        private static readonly int[] BonesBySize = { 1, 1, 2, 3 };
        private static readonly int[] FatBySize = { 0, 1, 1, 2 };
        private static readonly float[] SecondsBySize = { 8f, 15f, 20f, 30f };
        private static readonly float[] LabourBySize = { 50f, 100f, 150f, 250f };

        public static CarcassSize SizeFor(float health)
        {
            if (health <= 30f) return CarcassSize.Small;
            if (health <= 50f) return CarcassSize.Medium;
            return health <= 60f ? CarcassSize.Large : CarcassSize.Huge;
        }

        public static bool TryGetAnimal(int animalSpecies, out ButcheredAnimal animal)
        {
            bool known = animalSpecies >= 0 && animalSpecies < Animals.Count;
            animal = known ? Animals[animalSpecies] : default;
            return known;
        }

        public static string MeatName(MeatKind kind)
        {
            switch (kind)
            {
                case MeatKind.SmallGame: return "Small game meat";
                case MeatKind.Game: return "Game meat";
                case MeatKind.LargeGame: return "Large game meat";
                case MeatKind.Poultry: return "Poultry";
                default: return "Fish fillet";
            }
        }

        // What a butcher's table makes of an animal's carcass, the meat first.
        public static (int Item, int Count)[] Yield(ButcheredAnimal animal)
        {
            int size = (int)animal.Size;
            var outputs = new List<(int, int)> { (Items.Meat(animal.Meat), MeatBySize[size]) };
            if (animal.HasHide) outputs.Add((Items.Hide, HideBySize[size]));
            outputs.Add((Items.Bones, BonesBySize[size]));
            if (FatBySize[size] > 0) outputs.Add((Items.Fat, FatBySize[size]));
            return outputs.ToArray();
        }

        // The meat a knife takes from it in the field: half, rounded up.
        public static int FieldMeat(ButcheredAnimal animal) => (MeatBySize[(int)animal.Size] + 1) / 2;

        public static float SecondsOf(ButcheredAnimal animal) => SecondsBySize[(int)animal.Size];

        public static float LabourOf(ButcheredAnimal animal) => LabourBySize[(int)animal.Size];

        // A butcher's table's recipes: one per animal species, its carcass in, what Yield gives out.
        internal static Recipe[] Recipes()
        {
            var recipes = new Recipe[Animals.Count];
            for (int i = 0; i < recipes.Length; i++)
            {
                ButcheredAnimal animal = Animals[i];
                recipes[i] = new Recipe(Yield(animal), SecondsOf(animal), LabourOf(animal), new Ingredient(Items.Carcass(animal.Index), 1));
            }

            return recipes;
        }

        // The recipe that butchers a carcass item, if any.
        public static int RecipeOf(int carcass)
        {
            int species = Items.IsCarcass(carcass) ? Items.CarcassSpecies(carcass) : -1;
            return species >= 0 && species < Animals.Count ? species : -1;
        }
    }
}

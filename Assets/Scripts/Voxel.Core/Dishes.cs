using System.Collections.Generic;

namespace Voxel.Core
{
    // The plant species the dishes are cooked from, by their index in EcosystemSync.speciesAssets
    // (crops are indices 16-41; reordering species already breaks saves, and EcosystemSync checks
    // these against the asset names at start).
    public static class CropSpecies
    {
        public const int Oat = 16;
        public const int Wheat = 17;
        public const int Beet = 18;
        public const int Carrot = 19;
        public const int Potato = 20;
        public const int Cabbage = 21;
        public const int Maize = 22;
        public const int Sunflower = 23;
        public const int Bean = 24;
        public const int Butternut = 25;
        public const int Rice = 26;
        public const int Mushroom = 27;

        // The asset name each index must have.
        public static readonly IReadOnlyList<(int Index, string Asset)> Expected = new[]
        {
            (Oat, "Oat"), (Wheat, "Wheat"), (Beet, "Beet"), (Carrot, "Carrot"), (Potato, "Potato"), (Cabbage, "Cabbage"),
            (Maize, "Maize"), (Sunflower, "Sunflower"), (Bean, "Bean"), (Butternut, "Butternut"), (Rice, "Rice"), (Mushroom, "Mushroom")
        };
    }

    // How a dish's icon is drawn (ItemIconBuilder): pieces of its main crop on a plate, a flat bread,
    // a bowl filled with a colour, a roast of that colour on a plate, or chunks of it on a skewer.
    public enum DishLook : byte
    {
        Plate,
        Bread,
        Bowl,
        Roast,
        Skewer
    }

    // A dish: what it is called, weighs and feeds, what it is cooked from on a campfire, the work it
    // takes (calories players put in) and how long it cooks once that work is done.
    public sealed class Dish
    {
        public Dish(string name, float weight, Nutrition food, int labour, float seconds, DishLook look, int mainSpecies, uint bowlColour, params Ingredient[] ingredients)
        {
            Name = name;
            Weight = weight;
            Food = food;
            Labour = labour;
            Seconds = seconds;
            Look = look;
            MainSpecies = mainSpecies;
            BowlColour = bowlColour;
            Ingredients = ingredients;
        }

        public string Name { get; }
        public float Weight { get; }
        public Nutrition Food { get; }
        public int Labour { get; }
        public float Seconds { get; }
        public DishLook Look { get; }
        public int MainSpecies { get; }

        // A bowl's filling, or the meat of a roast or skewer, as 0xRRGGBB.
        public uint BowlColour { get; }
        public IReadOnlyList<Ingredient> Ingredients { get; }
    }

    // The dishes cooked on a campfire (Recipes.Cooking), each one item (Items.Dish(index)). Cooked food
    // feeds more than the crops it is made from, and in a better balance.
    public static class Dishes
    {
        public static readonly IReadOnlyList<Dish> All = new[]
        {
            new Dish("Baked potato", 0.4f, new Nutrition(300, 13, 3, 1, 4), 150, 15f, DishLook.Plate, CropSpecies.Potato, 0,
                Crop(CropSpecies.Potato, 2)),
            new Dish("Grilled maize", 0.5f, new Nutrition(340, 11, 4, 3, 3), 150, 15f, DishLook.Plate, CropSpecies.Maize, 0,
                Crop(CropSpecies.Maize, 2)),
            new Dish("Flatbread", 0.3f, new Nutrition(600, 13, 4, 2, 0), 250, 20f, DishLook.Bread, CropSpecies.Wheat, 0xC9944Bu,
                Crop(CropSpecies.Wheat, 4)),
            new Dish("Oat porridge", 0.5f, new Nutrition(680, 12, 5, 3, 0), 250, 20f, DishLook.Bowl, CropSpecies.Oat, 0xD8C49Au,
                Crop(CropSpecies.Oat, 4)),
            new Dish("Steamed rice", 0.4f, new Nutrition(520, 14, 3, 0, 1), 200, 20f, DishLook.Bowl, CropSpecies.Rice, 0xF2EEE2u,
                Crop(CropSpecies.Rice, 3)),
            new Dish("Bean stew", 0.6f, new Nutrition(800, 7, 13, 2, 6), 300, 25f, DishLook.Bowl, CropSpecies.Bean, 0x8A4A2Cu,
                Crop(CropSpecies.Bean, 4), Crop(CropSpecies.Carrot, 1)),
            new Dish("Vegetable soup", 0.6f, new Nutrition(260, 7, 3, 0, 14), 200, 20f, DishLook.Bowl, CropSpecies.Cabbage, 0x9C4F5Au,
                Crop(CropSpecies.Cabbage, 1), Crop(CropSpecies.Carrot, 1), Crop(CropSpecies.Beet, 1)),
            new Dish("Roasted squash", 0.8f, new Nutrition(600, 6, 6, 8, 8), 250, 25f, DishLook.Plate, CropSpecies.Butternut, 0,
                Crop(CropSpecies.Butternut, 1), Crop(CropSpecies.Sunflower, 2)),
            new Dish("Mushroom skewer", 0.2f, new Nutrition(160, 2, 9, 0, 8), 100, 10f, DishLook.Plate, CropSpecies.Mushroom, 0,
                Crop(CropSpecies.Mushroom, 3)),

            // Meat (raw meat is not eaten): rich in protein and fat, what the crops lack.
            new Dish("Small game skewer", 0.3f, new Nutrition(450, 0, 13, 5, 1), 150, 15f, DishLook.Skewer, -1, 0x9A5A3Au,
                Meat(MeatKind.SmallGame, 2)),
            new Dish("Roast game", 0.6f, new Nutrition(900, 0, 12, 9, 1), 300, 25f, DishLook.Roast, -1, 0x7E4027u,
                Meat(MeatKind.Game, 2), new Ingredient(Items.Fat, 1)),
            new Dish("Large roast", 0.8f, new Nutrition(1100, 0, 14, 8, 0), 350, 30f, DishLook.Roast, -1, 0x6A3320u,
                Meat(MeatKind.LargeGame, 2)),
            new Dish("Grilled poultry", 0.4f, new Nutrition(600, 0, 13, 6, 1), 200, 20f, DishLook.Roast, -1, 0xC08A4Eu,
                Meat(MeatKind.Poultry, 2)),
            new Dish("Grilled fish", 0.4f, new Nutrition(500, 0, 12, 5, 3), 150, 15f, DishLook.Roast, -1, 0xD8B48Au,
                Meat(MeatKind.Fish, 2)),
            new Dish("Hunter's stew", 0.7f, new Nutrition(900, 6, 10, 5, 6), 300, 25f, DishLook.Bowl, CropSpecies.Potato, 0x6E3B22u,
                Meat(MeatKind.LargeGame, 1), Crop(CropSpecies.Potato, 1), Crop(CropSpecies.Carrot, 1)),
            new Dish("Bone broth", 0.5f, new Nutrition(300, 1, 8, 3, 8), 200, 30f, DishLook.Bowl, CropSpecies.Cabbage, 0xC9A66Bu,
                new Ingredient(Items.Bones, 2), Crop(CropSpecies.Cabbage, 1))
        };

        public static bool TryGet(int item, out Dish dish)
        {
            dish = null;
            if (!Items.IsDish(item)) return false;

            int index = Items.DishIndex(item);
            if (index >= All.Count) return false;

            dish = All[index];
            return true;
        }

        public static Nutrition FoodOf(int item) => TryGet(item, out Dish dish) ? dish.Food : default;

        private static Ingredient Crop(int species, int count) => new Ingredient(Items.Produce(species), count);

        private static Ingredient Meat(MeatKind kind, int count) => new Ingredient(Items.Meat(kind), count);
    }
}

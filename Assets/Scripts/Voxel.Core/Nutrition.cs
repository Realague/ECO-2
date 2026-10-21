namespace Voxel.Core
{
    // What a food gives: its calories and how rich it is in each nutrient (points from 0 to about 15,
    // as in Eco). Something without calories is not food.
    public readonly struct Nutrition
    {
        public const int NutrientCount = 4;

        public readonly float Calories;
        public readonly float Carbs;
        public readonly float Protein;
        public readonly float Fat;
        public readonly float Vitamins;

        public Nutrition(float calories, float carbs, float protein, float fat, float vitamins)
        {
            Calories = calories;
            Carbs = carbs;
            Protein = protein;
            Fat = fat;
            Vitamins = vitamins;
        }

        public bool IsFood => Calories > 0f;

        // The nutrients in a fixed order: carbs, protein, fat, vitamins.
        public float Nutrient(int index)
        {
            switch (index)
            {
                case 0: return Carbs;
                case 1: return Protein;
                case 2: return Fat;
                default: return Vitamins;
            }
        }
    }
}

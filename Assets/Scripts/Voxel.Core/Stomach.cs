using System;
using System.IO;

namespace Voxel.Core
{
    // What a player has eaten: the calories in their stomach and its four nutrient levels (carbs,
    // protein, fat, vitamins, in Nutrition's order), each the mean of the foods eaten weighted by their
    // calories. Calories burn away evenly, so burning never changes the balance; an empty stomach has
    // no balance left.
    public sealed class Stomach
    {
        public const float Capacity = 3000f;

        // A new player, or one saved before hunger, starts half full with an even balance.
        public const float StartingCalories = 1500f;
        public const float StartingNutrient = 5f;

        private readonly float[] nutrients = new float[Nutrition.NutrientCount];

        public Stomach()
        {
            Reset();
        }

        public float Calories { get; private set; }

        public float Fullness => Calories / Capacity;

        public bool IsStarving => Calories <= 0f;

        public float Nutrient(int index) => nutrients[index];

        public bool CanEat(Nutrition food) => food.IsFood && Calories + food.Calories <= Capacity;

        // Returns false, changing nothing, for something that is not food or does not fit.
        public bool Eat(Nutrition food)
        {
            if (!CanEat(food)) return false;

            float total = Calories + food.Calories;
            for (int i = 0; i < nutrients.Length; i++)
            {
                nutrients[i] = (nutrients[i] * Calories + food.Nutrient(i) * food.Calories) / total;
            }

            Calories = total;
            return true;
        }

        public void Burn(float calories)
        {
            if (calories <= 0f || Calories <= 0f) return;

            Calories = Math.Max(0f, Calories - calories);
            if (Calories > 0f) return;

            for (int i = 0; i < nutrients.Length; i++)
            {
                nutrients[i] = 0f;
            }
        }

        // Each nutrient's share of the four together (the pie's slices); all zero when there is none.
        public float Share(int index)
        {
            float sum = 0f;
            for (int i = 0; i < nutrients.Length; i++)
            {
                sum += nutrients[i];
            }

            return sum > 0f ? nutrients[index] / sum : 0f;
        }

        public void Reset()
        {
            Calories = StartingCalories;
            for (int i = 0; i < nutrients.Length; i++)
            {
                nutrients[i] = StartingNutrient;
            }
        }

        // A full stomach (a player entering creative mode): the balance kept, or even if it was empty.
        public void Fill()
        {
            if (Calories <= 0f)
            {
                for (int i = 0; i < nutrients.Length; i++)
                {
                    nutrients[i] = StartingNutrient;
                }
            }

            Calories = Capacity;
        }

        public void CopyFrom(Stomach other)
        {
            Calories = other.Calories;
            Array.Copy(other.nutrients, nutrients, nutrients.Length);
        }

        public void Write(BinaryWriter writer)
        {
            writer.Write(Calories);
            for (int i = 0; i < nutrients.Length; i++)
            {
                writer.Write(nutrients[i]);
            }
        }

        // The data may come from a damaged file or another machine: a negative, infinite or oversized value is refused.
        public void Read(BinaryReader reader)
        {
            float calories = reader.ReadSingle();
            if (!(calories >= 0f && calories <= Capacity)) throw new InvalidDataException($"Invalid stomach: {calories} calories.");

            var read = new float[nutrients.Length];
            for (int i = 0; i < read.Length; i++)
            {
                read[i] = reader.ReadSingle();
                if (!(read[i] >= 0f && read[i] < 1000f)) throw new InvalidDataException($"Invalid stomach: nutrient {i} at {read[i]}.");
            }

            Calories = calories;
            Array.Copy(read, nutrients, nutrients.Length);
        }
    }
}

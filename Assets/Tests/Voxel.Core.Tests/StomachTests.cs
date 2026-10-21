using System.IO;
using NUnit.Framework;

namespace Voxel.Core.Tests
{
    public class StomachTests
    {
        private static readonly Nutrition Apple = new Nutrition(80f, 10f, 0f, 0f, 6f);
        private static readonly Nutrition Bean = new Nutrition(160f, 6f, 10f, 1f, 2f);

        private static Stomach Empty()
        {
            var stomach = new Stomach();
            stomach.Burn(Stomach.Capacity);
            return stomach;
        }

        [Test]
        public void ANewStomach_IsHalfFull_WithAnEvenBalance()
        {
            var stomach = new Stomach();

            Assert.AreEqual(Stomach.StartingCalories, stomach.Calories);
            Assert.AreEqual(0.5f, stomach.Fullness);
            for (int i = 0; i < Nutrition.NutrientCount; i++)
            {
                Assert.AreEqual(0.25f, stomach.Share(i), 1e-6f);
            }
        }

        [Test]
        public void Eating_AddsCalories_AndWeighsEachNutrientByCalories()
        {
            Stomach stomach = Empty();

            Assert.IsTrue(stomach.Eat(Apple));
            Assert.IsTrue(stomach.Eat(Bean));

            Assert.AreEqual(240f, stomach.Calories);
            Assert.AreEqual((10f * 80f + 6f * 160f) / 240f, stomach.Nutrient(0), 1e-4f, "carbs");
            Assert.AreEqual(10f * 160f / 240f, stomach.Nutrient(1), 1e-4f, "protein");
            Assert.AreEqual((6f * 80f + 2f * 160f) / 240f, stomach.Nutrient(3), 1e-4f, "vitamins");
        }

        [Test]
        public void Burning_KeepsTheBalance_UntilTheStomachIsEmpty()
        {
            Stomach stomach = Empty();
            stomach.Eat(Bean);
            float protein = stomach.Share(1);

            stomach.Burn(100f);

            Assert.AreEqual(60f, stomach.Calories);
            Assert.AreEqual(protein, stomach.Share(1), 1e-6f);
            Assert.IsFalse(stomach.IsStarving);

            stomach.Burn(100f);

            Assert.AreEqual(0f, stomach.Calories);
            Assert.IsTrue(stomach.IsStarving);
            Assert.AreEqual(0f, stomach.Share(1), "an empty stomach has no balance left");
            Assert.AreEqual(0f, stomach.Nutrient(1));
        }

        [Test]
        public void Eat_RefusesWhatDoesNotFit_OrIsNotFood()
        {
            var stomach = new Stomach();
            while (stomach.CanEat(Bean)) stomach.Eat(Bean);
            float calories = stomach.Calories;

            Assert.IsFalse(stomach.Eat(Bean), "too full");
            Assert.IsFalse(stomach.Eat(new Nutrition(0f, 5f, 5f, 5f, 5f)), "no calories, not food");
            Assert.AreEqual(calories, stomach.Calories);
            Assert.LessOrEqual(stomach.Calories, Stomach.Capacity);
        }

        [Test]
        public void WriteThenRead_RestoresTheStomach()
        {
            Stomach stomach = Empty();
            stomach.Eat(Apple);
            var stream = new MemoryStream();
            stomach.Write(new BinaryWriter(stream));
            stream.Position = 0;

            var loaded = new Stomach();
            loaded.Read(new BinaryReader(stream));

            Assert.AreEqual(stomach.Calories, loaded.Calories);
            for (int i = 0; i < Nutrition.NutrientCount; i++)
            {
                Assert.AreEqual(stomach.Nutrient(i), loaded.Nutrient(i));
            }
        }

        [Test]
        public void Read_RefusesImpossibleValues()
        {
            Assert.Throws<InvalidDataException>(() => ReadValues(-1f, 1f, 1f, 1f, 1f));
            Assert.Throws<InvalidDataException>(() => ReadValues(Stomach.Capacity + 1f, 1f, 1f, 1f, 1f));
            Assert.Throws<InvalidDataException>(() => ReadValues(100f, float.NaN, 1f, 1f, 1f));
            Assert.Throws<InvalidDataException>(() => ReadValues(100f, 1f, float.PositiveInfinity, 1f, 1f));
        }

        private static void ReadValues(params float[] values)
        {
            var stream = new MemoryStream();
            var writer = new BinaryWriter(stream);
            foreach (float value in values) writer.Write(value);
            stream.Position = 0;
            new Stomach().Read(new BinaryReader(stream));
        }
    }
}

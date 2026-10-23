using System.Globalization;
using UnityEngine;
using UnityEngine.UIElements;
using Voxel.Core;

namespace Voxel.Client
{
    // The stomach beside the hotbar (Assets/UI/Hotbar.uxml, the "stomach" box): its pie, the calories
    // and each nutrient's share; "Starving: eat now" in place of the shares when it is empty (a starving player can do nothing that takes effort). Shown
    // with the hotbar, whose document it shares; redrawn only when a figure shown changes.
    public class StomachHud : MonoBehaviour
    {
        private const string LowClass = "stomach-kcal--low";
        private const string StarvingClass = "stomach--starving";

        // Under this share of a full stomach, the calories turn red.
        private const float LowFullness = 0.1f;

        private static readonly string[] NutrientNames = { "Carbs", "Protein", "Fat", "Vitamins" };

        [SerializeField] private UIDocument document;
        [SerializeField] private HungerSync hungerSync;

        private VisualElement box;
        private StomachPie pie;
        private Label calories;
        private readonly Label[] nutrients = new Label[Nutrition.NutrientCount];

        // What is shown, in whole calories and whole percents, so that the pie is not redrawn for nothing.
        private int shownCalories = -1;
        private readonly int[] shownPercents = new int[Nutrition.NutrientCount];

        private void Start()
        {
            if (Application.isBatchMode)
            {
                enabled = false;
                return;
            }

            VisualElement root = document.rootVisualElement;
            box = root.Q("stomach");
            pie = root.Q<StomachPie>("stomach-pie");
            calories = root.Q<Label>("stomach-kcal");
            for (int i = 0; i < nutrients.Length; i++)
            {
                nutrients[i] = root.Q<Label>($"stomach-nutrient-{i}");
            }
        }

        private void Update()
        {
            Stomach stomach = hungerSync.Stomach;
            int kcal = Mathf.CeilToInt(stomach.Calories);
            bool changed = kcal != shownCalories;
            for (int i = 0; i < nutrients.Length; i++)
            {
                int percent = Mathf.RoundToInt(stomach.Share(i) * 100f);
                if (percent == shownPercents[i]) continue;

                shownPercents[i] = percent;
                nutrients[i].text = $"{NutrientNames[i]} {percent} %";
                changed = true;
            }

            if (!changed) return;

            shownCalories = kcal;
            calories.text = kcal.ToString("N0", CultureInfo.InvariantCulture) + " kcal";
            calories.EnableInClassList(LowClass, stomach.Fullness < LowFullness);
            box.EnableInClassList(StarvingClass, stomach.IsStarving);
            pie.Show(stomach);
        }
    }
}

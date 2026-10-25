using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using Voxel.Core;

namespace Voxel.Client
{
    // The inventory's crafting tab (Inventory.uxml: crafting-page, recipe-sheet), run by
    // InventoryScreen: every recipe in a list, with what it takes (red where the bag lacks it), its
    // time and calories, greyed when it cannot be crafted; the chosen one on the sheet, each
    // ingredient with what the bag holds of it and, for wood or stone, which one goes in (Auto, or one
    // the bag holds: kept per group for the session, back to Auto once the bag has none of it), then
    // Craft, or the craft under way with its progress and Cancel.
    internal sealed class CraftingPage
    {
        private const string SelectedRowClass = "recipe-row--selected";

        private readonly CraftingSync craftingSync;
        private readonly InventorySync inventorySync;
        private readonly HungerSync hungerSync;
        private readonly EcosystemSync ecosystemSync;

        private readonly VisualElement page;
        private readonly ScrollView list;
        private readonly Label calories;
        private readonly VisualElement sheet;
        private readonly VisualElement icon;
        private readonly Label name;
        private readonly Label kind;
        private readonly Label description;
        private readonly VisualElement ingredients;
        private readonly Label duration;
        private readonly Label cost;
        private readonly Label reason;
        private readonly VisualElement progress;
        private readonly VisualElement progressFill;
        private readonly Label progressText;
        private readonly Button craft;
        private readonly Button cancel;

        // A row per recipe, in Recipes.All's order.
        private readonly List<Row> rows = new List<Row>();

        // The item chosen for each group of ingredient (Items.None: Auto).
        private readonly Dictionary<IngredientGroup, int> chosen = new Dictionary<IngredientGroup, int>();

        private int selected;

        // What the list and sheet were last drawn from: changed, they are drawn again.
        private long shownKey = long.MinValue;

        private sealed class Row
        {
            public VisualElement Root;
            public Label Name;
            public readonly List<Label> Counts = new List<Label>();
            public Label Meta;
        }

        public CraftingPage(VisualElement root, CraftingSync craftingSync, InventorySync inventorySync, HungerSync hungerSync, EcosystemSync ecosystemSync)
        {
            this.craftingSync = craftingSync;
            this.inventorySync = inventorySync;
            this.hungerSync = hungerSync;
            this.ecosystemSync = ecosystemSync;

            page = root.Q("crafting-page");
            list = root.Q<ScrollView>("recipe-list");
            calories = root.Q<Label>("crafting-calories");
            sheet = root.Q("recipe-sheet");
            icon = root.Q("recipe-icon");
            name = root.Q<Label>("recipe-name");
            kind = root.Q<Label>("recipe-kind");
            description = root.Q<Label>("recipe-description");
            ingredients = root.Q("recipe-ingredients");
            duration = root.Q<Label>("recipe-duration");
            cost = root.Q<Label>("recipe-calories");
            reason = root.Q<Label>("recipe-reason");
            progress = root.Q("recipe-progress");
            progressFill = root.Q("recipe-progress-fill");
            progressText = root.Q<Label>("recipe-progress-text");
            craft = root.Q<Button>("recipe-craft");
            cancel = root.Q<Button>("recipe-cancel");
            craft.focusable = false;
            cancel.focusable = false;
            craft.clicked += () => craftingSync.RequestCraft(selected, ChoicesFor(Recipes.All[selected]));
            cancel.clicked += craftingSync.RequestCancel;

            list.verticalScrollerVisibility = ScrollerVisibility.AlwaysVisible;
            for (int r = 0; r < Recipes.All.Count; r++)
            {
                AddRow(r);
            }

            Show(false);
        }

        public void Show(bool shown)
        {
            page.style.display = shown ? DisplayStyle.Flex : DisplayStyle.None;
            sheet.style.display = shown ? DisplayStyle.Flex : DisplayStyle.None;
            shownKey = long.MinValue;
        }

        // Another language: everything is written again.
        public void Retranslate() => shownKey = long.MinValue;

        // Every frame while the tab is shown: the progress, and the rest when what it shows changed.
        public void Refresh()
        {
            Inventory bag = inventorySync.Inventory;
            calories.text = $"{hungerSync.Stomach.Calories:0} kcal";
            ShowProgress();

            long key = KeyOf(bag);
            if (key == shownKey) return;

            shownKey = key;
            for (int r = 0; r < rows.Count; r++)
            {
                ShowRow(r, bag);
            }

            ShowSheet(bag);
        }

        private void AddRow(int index)
        {
            Recipe recipe = Recipes.All[index];
            var row = new Row { Root = new VisualElement() };
            row.Root.AddToClassList("recipe-row");
            var picture = new VisualElement { pickingMode = PickingMode.Ignore };
            picture.AddToClassList("recipe-row-icon");
            picture.style.backgroundImage = new StyleBackground(ItemCatalog.IconOf(recipe.Output, ecosystemSync));
            row.Root.Add(picture);

            var text = new VisualElement { pickingMode = PickingMode.Ignore };
            text.AddToClassList("recipe-row-text");
            row.Name = new Label { pickingMode = PickingMode.Ignore };
            row.Name.AddToClassList("recipe-row-name");
            text.Add(row.Name);
            var costs = new VisualElement { pickingMode = PickingMode.Ignore };
            costs.AddToClassList("recipe-row-cost");
            foreach (Ingredient ingredient in recipe.Ingredients)
            {
                var chip = new VisualElement { pickingMode = PickingMode.Ignore };
                chip.AddToClassList("recipe-chip");
                var chipIcon = new VisualElement { pickingMode = PickingMode.Ignore };
                chipIcon.AddToClassList("recipe-chip-icon");
                chipIcon.style.backgroundImage = new StyleBackground(IconOf(ingredient));
                var count = new Label { pickingMode = PickingMode.Ignore };
                count.AddToClassList("recipe-chip-text");
                chip.Add(chipIcon);
                chip.Add(count);
                costs.Add(chip);
                row.Counts.Add(count);
            }

            text.Add(costs);
            row.Root.Add(text);
            row.Meta = new Label { pickingMode = PickingMode.Ignore };
            row.Meta.AddToClassList("recipe-row-meta-text");
            var meta = new VisualElement { pickingMode = PickingMode.Ignore };
            meta.AddToClassList("recipe-row-meta");
            meta.Add(row.Meta);
            row.Root.Add(meta);

            row.Root.RegisterCallback<PointerDownEvent>(e =>
            {
                if (e.button != 0) return;

                selected = index;
                shownKey = long.MinValue;
            });
            list.Add(row.Root);
            rows.Add(row);
        }

        private void ShowRow(int index, Inventory bag)
        {
            Recipe recipe = Recipes.All[index];
            Row row = rows[index];
            int[] choices = ChoicesFor(recipe);
            row.Name.text = OutputName(recipe);
            for (int i = 0; i < recipe.Ingredients.Count; i++)
            {
                Ingredient ingredient = recipe.Ingredients[i];
                int have = Recipes.Have(bag, ingredient, choices[i]);
                row.Counts[i].text = $"{Mathf.Min(have, 9999)} / {ingredient.Count}";
                row.Counts[i].EnableInClassList("recipe-chip-text--missing", have < ingredient.Count);
            }

            row.Meta.text = $"{Loc.T("{0} s", Loc.Number(recipe.Seconds, "0.#"))} · {recipe.Calories:0} kcal";
            row.Root.EnableInClassList(SelectedRowClass, index == selected);
            row.Root.EnableInClassList("recipe-row--unavailable", Recipes.Check(bag, recipe, choices, ecosystemSync.ItemWeights) != CraftRefusal.None);
        }

        private void ShowSheet(Inventory bag)
        {
            Recipe recipe = Recipes.All[selected];
            int[] choices = ChoicesFor(recipe);
            icon.style.backgroundImage = new StyleBackground(ItemCatalog.IconOf(recipe.Output, ecosystemSync));
            name.text = OutputName(recipe);
            kind.text = ItemCatalog.KindName(recipe.Output);
            description.text = ItemCatalog.DescriptionOf(recipe.Output, ecosystemSync);
            duration.text = Loc.T("{0} s", Loc.Number(recipe.Seconds, "0.#"));
            cost.text = $"{recipe.Calories:0} kcal";

            ingredients.Clear();
            for (int i = 0; i < recipe.Ingredients.Count; i++)
            {
                Ingredient ingredient = recipe.Ingredients[i];
                int have = Recipes.Have(bag, ingredient, choices[i]);
                var line = new VisualElement();
                line.AddToClassList("details-line");
                var picture = new VisualElement();
                picture.AddToClassList("ingredient-icon");
                picture.style.backgroundImage = new StyleBackground(choices[i] != Items.None ? ItemCatalog.IconOf(choices[i], ecosystemSync) : IconOf(ingredient));
                var label = new Label(IngredientName(ingredient, choices[i]));
                label.AddToClassList("details-line-label");
                var value = new Label($"{have} / {ingredient.Count}");
                value.AddToClassList("details-line-value");
                value.EnableInClassList("ingredient-value--missing", have < ingredient.Count);
                line.Add(picture);
                line.Add(label);
                line.Add(value);
                ingredients.Add(line);

                if (ingredient.IsGroup) ingredients.Add(Choices(ingredient, choices[i], bag));
            }

            // Craft, or why not; while crafting, the progress and Cancel (ShowProgress).
            CraftRefusal refusal = craftingSync.LocalCheck(selected, choices);
            bool crafting = craftingSync.ActiveRecipe >= 0;
            craft.SetEnabled(refusal == CraftRefusal.None);
            craft.style.display = crafting ? DisplayStyle.None : DisplayStyle.Flex;
            cancel.style.display = crafting ? DisplayStyle.Flex : DisplayStyle.None;
            bool tell = refusal != CraftRefusal.None && refusal != CraftRefusal.Busy;
            reason.text = tell ? Loc.T(CraftingSync.TextOf(refusal)) : "";
            reason.style.display = tell ? DisplayStyle.Flex : DisplayStyle.None;
        }

        // The wood or rock that goes in for an ingredient: Auto, then each one the bag holds.
        private VisualElement Choices(Ingredient ingredient, int choice, Inventory bag)
        {
            var row = new VisualElement();
            row.AddToClassList("choices");
            row.Add(Choice(ingredient.Group, Items.None, Loc.T("Auto"), null, choice == Items.None, false));
            foreach (int item in Recipes.Options(bag, ingredient))
            {
                int count = bag.Count(item);
                VisualElement option = Choice(ingredient.Group, item, count.ToString(), ItemCatalog.IconOf(item, ecosystemSync), choice == item, count < ingredient.Count);
                option.tooltip = ItemCatalog.NameOf(item, ecosystemSync);
                row.Add(option);
            }

            return row;
        }

        private VisualElement Choice(IngredientGroup group, int item, string text, Texture2D picture, bool isChosen, bool tooFew)
        {
            var option = new VisualElement();
            option.AddToClassList("choice");
            option.EnableInClassList("choice--selected", isChosen);
            option.EnableInClassList("choice--short", tooFew);
            if (picture != null)
            {
                var image = new VisualElement { pickingMode = PickingMode.Ignore };
                image.AddToClassList("choice-icon");
                image.style.backgroundImage = new StyleBackground(picture);
                option.Add(image);
            }

            var label = new Label(text) { pickingMode = PickingMode.Ignore };
            label.AddToClassList("choice-text");
            option.Add(label);
            option.RegisterCallback<PointerDownEvent>(e =>
            {
                if (e.button != 0) return;

                chosen[group] = item;
                shownKey = long.MinValue;
            });
            return option;
        }

        private void ShowProgress()
        {
            int active = craftingSync.ActiveRecipe;
            progress.style.display = active >= 0 ? DisplayStyle.Flex : DisplayStyle.None;
            if (active < 0) return;

            progressFill.style.width = Length.Percent(craftingSync.Progress * 100f);
            progressText.text = Loc.T("Crafting {0}… {1} s", OutputName(Recipes.All[active]), Mathf.CeilToInt(craftingSync.SecondsLeft));
        }

        // The choice for each of a recipe's ingredients: the item chosen for its group, while the bag
        // holds any of it.
        private int[] ChoicesFor(Recipe recipe)
        {
            int[] choices = Recipes.AutoChoices(recipe);
            Inventory bag = inventorySync.Inventory;
            for (int i = 0; i < choices.Length; i++)
            {
                Ingredient ingredient = recipe.Ingredients[i];
                if (!ingredient.IsGroup || !chosen.TryGetValue(ingredient.Group, out int item)) continue;

                if (bag.Has(item) && ingredient.Matches(item)) choices[i] = item;
                else chosen.Remove(ingredient.Group);
            }

            return choices;
        }

        // What the list and sheet show depends on: the bag, the stomach running out, the craft under
        // way, the recipe and choices chosen.
        private long KeyOf(Inventory bag)
        {
            long key = 17;
            foreach (KeyValuePair<int, int> entry in bag.Entries)
            {
                // Added up, so that the order of the entries does not matter.
                key += (entry.Key * 7919L + entry.Value) * 2654435761L;
            }

            key = key * 31 + selected;
            key = key * 31 + craftingSync.ActiveRecipe;
            key = key * 31 + (hungerSync.Stomach.IsStarving ? 1 : 0);
            foreach (KeyValuePair<IngredientGroup, int> choice in chosen)
            {
                key = key * 31 + (int)choice.Key * 1000 + choice.Value;
            }

            return key;
        }

        private string OutputName(Recipe recipe)
        {
            string output = ItemCatalog.NameOf(recipe.Output, ecosystemSync);
            return recipe.OutputCount > 1 ? Loc.T("{0} ×{1}", output, recipe.OutputCount) : output;
        }

        private string IngredientName(Ingredient ingredient, int choice)
        {
            if (choice != Items.None) return ItemCatalog.NameOf(choice, ecosystemSync);
            switch (ingredient.Group)
            {
                case IngredientGroup.Wood: return Loc.T("Wood");
                case IngredientGroup.Stone: return Loc.T("Stone");
                default: return ItemCatalog.NameOf(ingredient.Item, ecosystemSync);
            }
        }

        // A group's picture: oak wood for wood, granite for stone.
        private Texture2D IconOf(Ingredient ingredient)
        {
            switch (ingredient.Group)
            {
                case IngredientGroup.Wood: return ItemCatalog.IconOf(Items.WoodOf(Items.OakSpecies), ecosystemSync);
                case IngredientGroup.Stone: return ItemCatalog.IconOf(Blocks.Granite, ecosystemSync);
                default: return ItemCatalog.IconOf(ingredient.Item, ecosystemSync);
            }
        }
    }
}

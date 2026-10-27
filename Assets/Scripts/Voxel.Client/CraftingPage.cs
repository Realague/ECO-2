using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using Voxel.Core;

namespace Voxel.Client
{
    // The inventory's crafting tab (Inventory.uxml: crafting-page, recipe-sheet), run by
    // InventoryScreen: every recipe in a list, with what it takes (red where the bag lacks it), its
    // time and calories, greyed when it cannot be crafted now; under it the queue (CraftingSync), each
    // order with how many are left, the item under way or what it waits for, and a cross cancelling
    // it; the chosen recipe on the sheet, each ingredient with what the bag holds of it for as many as
    // are asked and, for wood or stone, which one goes in (Auto, or one the bag holds: kept per group
    // for the session, back to Auto once the bag has none of it), how many to make, and Add to the
    // queue, which also takes an order whose ingredients are missing (it waits for them).
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
        private readonly Label queueTitle;
        private readonly QuantityStepper quantity;
        private readonly OrderRows queue;

        // A row per recipe, in Recipes.All's order.
        private readonly List<Row> rows = new List<Row>();

        // The item chosen for each group of ingredient (Items.None: Auto).
        private readonly Dictionary<IngredientGroup, int> chosen = new Dictionary<IngredientGroup, int>();

        private int selected;

        // What the list and sheet were last drawn from: changed, they are drawn again.
        private long shownKey = long.MinValue;
        private long queueKey = long.MinValue;

        private sealed class Row
        {
            public VisualElement Root;
            public Label Name;
            public readonly List<Label> Counts = new List<Label>();
            public Label Seconds;
            public Label Calories;
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
            queueTitle = root.Q<Label>("craft-queue-title");
            craft.focusable = false;
            craft.clicked += () => craftingSync.RequestQueue(selected, ChoicesFor(Recipes.All[selected]), quantity.Value);

            quantity = new QuantityStepper(root.Q("recipe-quantity"), CraftQueue.MaxCount, () =>
            {
                Recipe recipe = Recipes.All[selected];
                return Recipes.MaxCraftable(inventorySync.Inventory, recipe, ChoicesFor(recipe), ecosystemSync.ItemWeights, CraftQueue.MaxCount);
            });
            quantity.Changed += () => shownKey = long.MinValue;
            queue = new OrderRows(root.Q<ScrollView>("craft-queue"), root.Q<Label>("craft-queue-empty"), craftingSync.RequestCancel, null);

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
            queueKey = long.MinValue;
        }

        // Whether the count is being typed, and leaving its field.
        public bool IsTyping => quantity.IsTyping;

        public void Blur() => quantity.Blur();

        // Another language: everything is written again.
        public void Retranslate()
        {
            shownKey = long.MinValue;
            queueKey = long.MinValue;
            quantity.Retranslate();
        }

        // Every frame while the tab is shown: the progress and the queue's bars, and the rest when what
        // it shows changed.
        public void Refresh()
        {
            Inventory bag = inventorySync.Inventory;
            calories.text = $"{hungerSync.Stomach.Calories:0} kcal";
            ShowProgress();
            ShowQueue();

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
            row.Root.Add(MetaOf(out row.Seconds, out row.Calories));

            row.Root.RegisterCallback<PointerDownEvent>(e =>
            {
                if (e.button != 0) return;

                selected = index;
                shownKey = long.MinValue;
            });
            list.Add(row.Root);
            rows.Add(row);
        }

        // A recipe row's time (an hourglass) over its calories (a flame).
        internal static VisualElement MetaOf(out Label seconds, out Label calories)
        {
            var meta = new VisualElement { pickingMode = PickingMode.Ignore };
            meta.AddToClassList("recipe-row-meta");
            seconds = MetaLine(meta, "icon--hourglass");
            calories = MetaLine(meta, "icon--fire");
            return meta;
        }

        private static Label MetaLine(VisualElement meta, string glyph)
        {
            var line = new VisualElement { pickingMode = PickingMode.Ignore };
            line.AddToClassList("recipe-row-meta-line");
            var picture = new VisualElement { pickingMode = PickingMode.Ignore };
            picture.AddToClassList("icon");
            picture.AddToClassList(glyph);
            picture.AddToClassList("recipe-row-meta-icon");
            var text = new Label { pickingMode = PickingMode.Ignore };
            text.AddToClassList("recipe-row-meta-text");
            line.Add(picture);
            line.Add(text);
            meta.Add(line);
            return text;
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

            row.Seconds.text = Loc.T("{0} s", Loc.Number(recipe.Seconds, "0.#"));
            row.Calories.text = $"{recipe.Calories:0} kcal";
            row.Root.EnableInClassList(SelectedRowClass, index == selected);
            row.Root.EnableInClassList("recipe-row--unavailable", Recipes.Check(bag, recipe, choices, ecosystemSync.ItemWeights) != CraftRefusal.None);
        }

        private void ShowSheet(Inventory bag)
        {
            Recipe recipe = Recipes.All[selected];
            int[] choices = ChoicesFor(recipe);
            int count = quantity.Value;
            icon.style.backgroundImage = new StyleBackground(ItemCatalog.IconOf(recipe.Output, ecosystemSync));
            name.text = OutputName(recipe);
            kind.text = ItemCatalog.KindName(recipe.Output);
            description.text = ItemCatalog.DescriptionOf(recipe.Output, ecosystemSync);
            duration.text = Times(Loc.T("{0} s", Loc.Number(recipe.Seconds, "0.#")), Loc.T("{0} s", Loc.Number(recipe.Seconds * count, "0.#")), count);
            cost.text = Times($"{recipe.Calories:0} kcal", $"{recipe.Calories * count:0} kcal", count);

            ingredients.Clear();
            for (int i = 0; i < recipe.Ingredients.Count; i++)
            {
                Ingredient ingredient = recipe.Ingredients[i];
                int have = Recipes.Have(bag, ingredient, choices[i]);
                int need = ingredient.Count * count;
                var line = new VisualElement();
                line.AddToClassList("details-line");
                var picture = new VisualElement();
                picture.AddToClassList("ingredient-icon");
                picture.style.backgroundImage = new StyleBackground(choices[i] != Items.None ? ItemCatalog.IconOf(choices[i], ecosystemSync) : IconOf(ingredient));
                var label = new Label(IngredientName(ingredient, choices[i]));
                label.AddToClassList("details-line-label");
                var value = new Label($"{have} / {need}");
                value.AddToClassList("details-line-value");
                value.EnableInClassList("ingredient-value--missing", have < need);
                line.Add(picture);
                line.Add(label);
                line.Add(value);
                ingredients.Add(line);

                if (ingredient.IsGroup) ingredients.Add(Choices(ingredient, choices[i], bag));
            }

            // Why it cannot be queued, or, in amber, that it will wait for its ingredients.
            CraftRefusal refusal = craftingSync.LocalCheck(selected, choices, count);
            craft.SetEnabled(refusal == CraftRefusal.None);
            bool waits = refusal == CraftRefusal.None && Recipes.MaxCraftable(bag, recipe, choices, ecosystemSync.ItemWeights, count) < count;
            reason.text = refusal != CraftRefusal.None ? Loc.T(CraftingSync.TextOf(refusal))
                : waits ? Loc.T("Missing ingredients: the order will wait until they are in your bag") : "";
            reason.EnableInClassList("recipe-reason--warning", refusal == CraftRefusal.None);
            reason.style.display = reason.text.Length > 0 ? DisplayStyle.Flex : DisplayStyle.None;
        }

        // "15 s", or for several "15 s × 3 = 45 s".
        internal static string Times(string one, string all, int count)
        {
            return count <= 1 ? one : Loc.T("{0} × {1} = {2}", one, count, all);
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
                VisualElement option = Choice(ingredient.Group, item, count.ToString(), ItemCatalog.IconOf(item, ecosystemSync), choice == item, count < ingredient.Count * quantity.Value);
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

        // The item under way, whatever its recipe.
        private void ShowProgress()
        {
            CraftOrderView active = craftingSync.Active;
            progress.style.display = active != null ? DisplayStyle.Flex : DisplayStyle.None;
            if (active == null) return;

            progressFill.style.width = Length.Percent(craftingSync.ProgressOf(active) * 100f);
            progressText.text = Loc.T("Crafting {0}… {1} s", OutputName(Recipes.All[active.Recipe]), Mathf.CeilToInt(craftingSync.SecondsLeftOf(active)));
        }

        // The queue's rows, laid out again when the orders change; their bars every frame.
        private void ShowQueue()
        {
            IReadOnlyList<CraftOrderView> orders = craftingSync.Orders;
            long key = 17;
            foreach (CraftOrderView order in orders)
            {
                key = key * 31 + order.Id;
                key = key * 31 + order.Count;
            }

            if (key != queueKey)
            {
                queueKey = key;
                queueTitle.text = Loc.T("Queue ({0} / {1})", orders.Count, CraftQueue.MaxOrders);
                var lines = new List<OrderRows.Line>();
                foreach (CraftOrderView order in orders)
                {
                    Recipe recipe = Recipes.All[order.Recipe];
                    lines.Add(new OrderRows.Line
                    {
                        Id = order.Id,
                        Icon = ItemCatalog.IconOf(recipe.Output, ecosystemSync),
                        Name = OutputName(recipe),
                        Under = order.Count > 1 ? Loc.T("{0} to make", order.Count) : Loc.T("1 to make")
                    });
                }

                queue.Build(lines);
            }

            foreach (CraftOrderView order in orders)
            {
                if (order.Running)
                {
                    queue.Set(order.Id, craftingSync.ProgressOf(order), false, Loc.T("Crafting… {0} s", Mathf.CeilToInt(craftingSync.SecondsLeftOf(order))), false, false, true);
                    continue;
                }

                string status = order.Waiting switch
                {
                    CraftRefusal.None => Loc.T("Waits its turn"),
                    CraftRefusal.Missing => Loc.T("Missing ingredients: waits until they are in your bag"),
                    _ => Loc.T(CraftingSync.TextOf(order.Waiting))
                };
                queue.Set(order.Id, 0f, false, status, order.Waiting != CraftRefusal.None, false, false);
            }
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

        // What the list and sheet show depends on: the bag, the stomach running out, the queue being
        // full, the recipe, choices and quantity chosen.
        private long KeyOf(Inventory bag)
        {
            long key = 17;
            foreach (KeyValuePair<int, int> entry in bag.Entries)
            {
                // Added up, so that the order of the entries does not matter.
                key += (entry.Key * 7919L + entry.Value) * 2654435761L;
            }

            key = key * 31 + selected;
            key = key * 31 + quantity.Value;
            key = key * 31 + (craftingSync.IsFull ? 1 : 0);
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

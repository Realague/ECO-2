using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using Voxel.Core;

namespace Voxel.Client
{
    // A campfire's cooking tab (Storage.uxml: cooking-page), run by StorageScreen and laid out as the
    // inventory's crafting tab: the wood by the fire and what is left of the log alight, how many chests
    // are linked and the player's calories; the dishes it cooks (what each takes, as the bag and the
    // linked chests hold it, its time and work; a click chooses one); under them the queue, each order
    // with who queued it, how many are left, the calories put in or how far its dish has cooked, what it
    // waits for, a button putting 100 kcal into it (Shift: all it needs) and one cancelling it; and the
    // chosen dish's sheet: its ingredients for as many as asked and where they are, its time and work,
    // how many to make and Add to the queue (taken without its ingredients: it waits for them).
    internal sealed class CampfirePage
    {
        private readonly StorageSync storageSync;
        private readonly InventorySync inventorySync;
        private readonly HungerSync hungerSync;
        private readonly EcosystemSync ecosystemSync;

        private readonly Label linked;
        private readonly Label fuel;
        private readonly VisualElement burnFill;
        private readonly Label calories;
        private readonly Label queueTitle;
        private readonly VisualElement sheetIcon;
        private readonly Label sheetName;
        private readonly Label sheetKind;
        private readonly Label sheetDescription;
        private readonly VisualElement sheetIngredients;
        private readonly Label sheetDuration;
        private readonly Label sheetWork;
        private readonly Label sheetWeight;
        private readonly Label sheetCalories;
        private readonly Label[] sheetNutrients = new Label[Nutrition.NutrientCount];
        private readonly VisualElement[] sheetNutrientBars = new VisualElement[Nutrition.NutrientCount];
        private readonly Label sheetReason;
        private readonly Button queueButton;
        private readonly QuantityStepper quantity;
        private readonly OrderRows queue;

        private readonly List<Row> rows = new List<Row>();
        private int selected;
        private long shownKey = long.MinValue;
        private long queueKey = long.MinValue;

        private sealed class Row
        {
            public VisualElement Root;
            public Label Name;
            public readonly List<Label> Counts = new List<Label>();
            public Label Seconds;
            public Label Work;
        }

        public CampfirePage(VisualElement root, StorageSync storageSync, InventorySync inventorySync, HungerSync hungerSync, EcosystemSync ecosystemSync)
        {
            this.storageSync = storageSync;
            this.inventorySync = inventorySync;
            this.hungerSync = hungerSync;
            this.ecosystemSync = ecosystemSync;
            linked = root.Q<Label>("fire-linked");
            fuel = root.Q<Label>("fire-fuel");
            burnFill = root.Q("fire-burn-fill");
            calories = root.Q<Label>("fire-calories");
            queueTitle = root.Q<Label>("fire-queue-title");
            sheetIcon = root.Q("fire-sheet-icon");
            sheetName = root.Q<Label>("fire-sheet-name");
            sheetKind = root.Q<Label>("fire-sheet-kind");
            sheetDescription = root.Q<Label>("fire-sheet-description");
            sheetIngredients = root.Q("fire-sheet-ingredients");
            sheetDuration = root.Q<Label>("fire-sheet-duration");
            sheetWork = root.Q<Label>("fire-sheet-work");
            sheetWeight = root.Q<Label>("fire-sheet-weight");
            sheetCalories = root.Q<Label>("fire-sheet-calories");
            for (int i = 0; i < Nutrition.NutrientCount; i++)
            {
                sheetNutrients[i] = root.Q<Label>($"fire-sheet-nutrient-{i}");
                sheetNutrientBars[i] = root.Q($"fire-sheet-nutrient-bar-{i}");
            }
            sheetReason = root.Q<Label>("fire-sheet-reason");
            queueButton = root.Q<Button>("fire-sheet-queue");
            queueButton.focusable = false;
            queueButton.clicked += () => storageSync.RequestQueue(selected, quantity.Value);

            quantity = new QuantityStepper(root.Q("fire-sheet-quantity"), Campfire.MaxCount, () => Makeable(Recipes.Cooking[selected]));
            quantity.Changed += () => shownKey = long.MinValue;
            queue = new OrderRows(root.Q<ScrollView>("fire-queue"), root.Q<Label>("fire-queue-empty"), storageSync.RequestCancelJob, storageSync.RequestLabour);

            ScrollView recipes = root.Q<ScrollView>("fire-recipes");
            recipes.verticalScrollerVisibility = ScrollerVisibility.AlwaysVisible;
            for (int r = 0; r < Recipes.Cooking.Count; r++)
            {
                recipes.Add(MakeRow(r));
            }
        }

        // Whether the count is being typed, and leaving its field.
        public bool IsTyping => quantity.IsTyping;

        public void Blur() => quantity.Blur();

        public void Retranslate()
        {
            shownKey = long.MinValue;
            queueKey = long.MinValue;
            quantity.Retranslate();
        }

        // Every frame while the tab is shown: the bars, and the rest when what it shows changed.
        public void Refresh()
        {
            fuel.text = $"{storageSync.OpenedFuel} / {Campfire.MaxFuel}";
            burnFill.style.width = Length.Percent(Mathf.Clamp01(storageSync.OpenedBurning / storageSync.OpenedLogSeconds) * 100f);
            calories.text = $"{hungerSync.Stomach.Calories:0} kcal";
            ShowQueue();

            long key = KeyOf();
            if (key == shownKey) return;

            shownKey = key;
            int linkedChests = storageSync.OpenedLinkedChests;
            linked.text = linkedChests == 0 ? Loc.T("No linked chest")
                : linkedChests == 1 ? Loc.T("1 linked chest") : Loc.T("{0} linked chests", linkedChests);
            linked.EnableInClassList("fire-linked--none", linkedChests == 0);
            for (int r = 0; r < rows.Count; r++)
            {
                ShowRow(r);
            }

            ShowSheet();
        }

        private VisualElement MakeRow(int index)
        {
            Recipe recipe = Recipes.Cooking[index];
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
                chipIcon.style.backgroundImage = new StyleBackground(ItemCatalog.IconOf(ingredient.Item, ecosystemSync));
                var count = new Label { pickingMode = PickingMode.Ignore };
                count.AddToClassList("recipe-chip-text");
                chip.Add(chipIcon);
                chip.Add(count);
                costs.Add(chip);
                row.Counts.Add(count);
            }

            text.Add(costs);
            row.Root.Add(text);
            row.Root.Add(CraftingPage.MetaOf(out row.Seconds, out row.Work));
            row.Root.RegisterCallback<PointerDownEvent>(e =>
            {
                if (e.button != 0) return;

                selected = index;
                shownKey = long.MinValue;
            });
            rows.Add(row);
            return row.Root;
        }

        // What a dish takes, as the bag and the linked chests hold it.
        private void ShowRow(int index)
        {
            Recipe recipe = Recipes.Cooking[index];
            Row row = rows[index];
            row.Name.text = ItemCatalog.NameOf(recipe.Output, ecosystemSync);
            for (int i = 0; i < recipe.Ingredients.Count; i++)
            {
                Ingredient ingredient = recipe.Ingredients[i];
                int have = Have(ingredient.Item);
                row.Counts[i].text = $"{Mathf.Min(have, 9999)} / {ingredient.Count}";
                row.Counts[i].EnableInClassList("recipe-chip-text--missing", have < ingredient.Count);
                row.Counts[i].parent.tooltip = ItemCatalog.NameOf(ingredient.Item, ecosystemSync);
            }

            row.Seconds.text = Loc.T("{0} s", Loc.Number(recipe.Seconds, "0.#"));
            row.Work.text = $"{recipe.Calories:0} kcal";
            row.Root.EnableInClassList("recipe-row--selected", index == selected);
            row.Root.EnableInClassList("recipe-row--unavailable", Makeable(recipe) == 0);
        }

        private void ShowSheet()
        {
            Recipe recipe = Recipes.Cooking[selected];
            int count = quantity.Value;
            sheetIcon.style.backgroundImage = new StyleBackground(ItemCatalog.IconOf(recipe.Output, ecosystemSync));
            sheetName.text = ItemCatalog.NameOf(recipe.Output, ecosystemSync);
            sheetKind.text = ItemCatalog.KindName(recipe.Output);
            sheetDescription.text = ItemCatalog.DescriptionOf(recipe.Output, ecosystemSync);
            sheetDuration.text = CraftingPage.Times(Loc.T("{0} s", Loc.Number(recipe.Seconds, "0.#")), Loc.T("{0} s", Loc.Number(recipe.Seconds * count, "0.#")), count);
            sheetWork.text = CraftingPage.Times($"{recipe.Calories:0} kcal", $"{recipe.Calories * count:0} kcal", count);

            // What one dish gives whoever eats it.
            Nutrition food = ecosystemSync.FoodOf(recipe.Output);
            sheetWeight.text = Loc.T("{0} kg each", Loc.Number(ecosystemSync.ItemWeights.Of(recipe.Output), "0.###"));
            sheetCalories.text = $"{food.Calories:0} kcal";
            for (int i = 0; i < Nutrition.NutrientCount; i++)
            {
                float points = food.Nutrient(i);
                sheetNutrients[i].text = points.ToString("0");
                sheetNutrientBars[i].style.width = Length.Percent(Mathf.Clamp01(points / ItemCatalog.FullNutrient) * 100f);
            }

            // Each ingredient for as many as asked, and where it is: the bag, the linked chests.
            sheetIngredients.Clear();
            foreach (Ingredient ingredient in recipe.Ingredients)
            {
                int bag = inventorySync.Inventory.Count(ingredient.Item);
                int chests = storageSync.OpenedLinkedContents.Count(ingredient.Item);
                int need = ingredient.Count * count;
                var line = new VisualElement();
                line.AddToClassList("details-line");
                var picture = new VisualElement();
                picture.AddToClassList("ingredient-icon");
                picture.style.backgroundImage = new StyleBackground(ItemCatalog.IconOf(ingredient.Item, ecosystemSync));
                var label = new Label(ItemCatalog.NameOf(ingredient.Item, ecosystemSync));
                label.AddToClassList("details-line-label");
                var value = new Label($"{bag + chests} / {need}");
                value.AddToClassList("details-line-value");
                value.EnableInClassList("ingredient-value--missing", bag + chests < need);
                line.Add(picture);
                line.Add(label);
                line.Add(value);
                sheetIngredients.Add(line);
                var source = new Label(Loc.T("bag {0} · linked chests {1}", bag, chests));
                source.AddToClassList("ingredient-source");
                sheetIngredients.Add(source);
            }

            // Why it cannot be queued; in amber, what it will wait for.
            bool full = storageSync.OpenedJobs.Count >= Campfire.MaxJobs;
            queueButton.SetEnabled(!full);
            string warning = storageSync.OpenedLinkedChests == 0 ? Loc.T("No linked chest: dishes need one to go into")
                : Makeable(recipe) < count ? Loc.T("Missing ingredients: the order will wait for them in the linked chests") : "";
            sheetReason.text = full ? Loc.T("The queue is full ({0})", Campfire.MaxJobs) : warning;
            sheetReason.EnableInClassList("recipe-reason--warning", !full);
            sheetReason.style.display = sheetReason.text.Length > 0 ? DisplayStyle.Flex : DisplayStyle.None;
        }

        // How many of a dish the bag and the linked chests hold the ingredients for.
        private int Makeable(Recipe recipe)
        {
            int count = Campfire.MaxCount;
            foreach (Ingredient ingredient in recipe.Ingredients)
            {
                count = Mathf.Min(count, Have(ingredient.Item) / ingredient.Count);
            }

            return count;
        }

        private int Have(int item) => inventorySync.Inventory.Count(item) + storageSync.OpenedLinkedContents.Count(item);

        // The queue's rows, laid out again when the orders change; their bars and states every frame.
        private void ShowQueue()
        {
            IReadOnlyList<CookJobView> jobs = storageSync.OpenedJobs;
            long key = 17;
            foreach (CookJobView job in jobs)
            {
                key = key * 31 + job.Id;
                key = key * 31 + job.Count;
                key = key * 31 + job.Stocked;
            }

            if (key != queueKey)
            {
                queueKey = key;
                queueTitle.text = Loc.T("Queue ({0} / {1})", jobs.Count, Campfire.MaxJobs);
                var lines = new List<OrderRows.Line>();
                foreach (CookJobView job in jobs)
                {
                    Recipe recipe = Recipes.Cooking[job.Recipe];
                    string name = ItemCatalog.NameOf(recipe.Output, ecosystemSync);
                    string by = string.IsNullOrEmpty(job.Creator) ? "" : Loc.T("by {0}", job.Creator);
                    if (job.Count > 1 || job.Stocked < job.Count)
                    {
                        string stocked = Loc.T("ingredients for {0} / {1}", job.Stocked, job.Count);
                        by = by.Length > 0 ? $"{by} · {stocked}" : stocked;
                    }
                    lines.Add(new OrderRows.Line
                    {
                        Id = job.Id,
                        Icon = ItemCatalog.IconOf(recipe.Output, ecosystemSync),
                        Name = job.Count > 1 ? Loc.T("{0} ×{1}", name, job.Count) : name,
                        Under = by
                    });
                }

                queue.Build(lines);
            }

            CookJobView cooking = storageSync.CookingJob;
            foreach (CookJobView job in jobs)
            {
                ShowJob(job, cooking);
            }
        }

        // An order's two bars, both always shown: the calories put into it, and how far its dish has
        // cooked; then what it waits for, or the cooking.
        private void ShowJob(CookJobView job, CookJobView cooking)
        {
            Recipe recipe = Recipes.Cooking[job.Recipe];
            float total = recipe.Calories * job.Count;
            float put = total - job.LabourLeft;
            queue.SetLabour(job.Id, put / total, Loc.T("{0} / {1} kcal", put.ToString("0"), total.ToString("0")));

            float left = storageSync.SecondsLeftOf(job);
            float done = 1f - left / recipe.Seconds;
            bool more = job.LabourLeft > 0f;
            if (!job.UnitPaid)
            {
                queue.Set(job.Id, done, true, Loc.T("Needs calories to cook"), true, true, false);
                return;
            }

            if (job.Stocked == 0)
            {
                queue.Set(job.Id, done, true, Loc.T("Missing ingredients: put them in a linked chest"), true, more, false);
                return;
            }

            if (job != cooking)
            {
                queue.Set(job.Id, done, true, Loc.T("Ready: waits its turn"), true, more, false);
                return;
            }

            switch (storageSync.OpenedStatus)
            {
                case CampfireStatus.NoFuel:
                    queue.Set(job.Id, done, true, Loc.T("Needs firewood"), true, more, false);
                    break;
                case CampfireStatus.NoRoom:
                    queue.Set(job.Id, done, true, Loc.T(storageSync.OpenedLinkedChests == 0 ? "No linked chest to put it in" : "No room in the linked chests"), true, more, false);
                    break;
                default:
                    queue.Set(job.Id, done, true, Loc.T("Cooking… {0} s", Mathf.CeilToInt(left)), false, more, storageSync.OpenedStatus == CampfireStatus.Cooking);
                    break;
            }
        }

        // What the list and sheet show depends on: the bag, the linked chests, the queue's length, the
        // dish and quantity chosen.
        private long KeyOf()
        {
            long key = 17;
            foreach (KeyValuePair<int, int> entry in inventorySync.Inventory.Entries)
            {
                key += (entry.Key * 7919L + entry.Value) * 2654435761L;
            }

            foreach (KeyValuePair<int, int> entry in storageSync.OpenedLinkedContents.Entries)
            {
                key += (entry.Key * 6271L + entry.Value) * 40503L;
            }

            key = key * 31 + storageSync.OpenedLinkedChests;
            key = key * 31 + storageSync.OpenedJobs.Count;
            key = key * 31 + selected;
            key = key * 31 + quantity.Value;
            return key;
        }
    }
}

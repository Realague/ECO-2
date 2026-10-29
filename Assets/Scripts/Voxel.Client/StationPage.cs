using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using Voxel.Core;

namespace Voxel.Client
{
    // A station's work tab (Storage.uxml: cooking-page), run by StorageScreen and laid out as the
    // inventory's crafting tab, for a campfire (dishes) or a butcher's table (carcasses): how many chests
    // are linked, a campfire's wood and what is left of the log alight, and the player's calories; what
    // it makes (what each takes, as the bag and the linked chests hold it, its time and work; a click
    // chooses one; at a butcher's table the carcasses at hand first); under them the queue, each order
    // with who queued it, how many are left, the calories put in and how far the one being made is, what
    // it waits for, a button putting 100 kcal into it (Shift: all it needs) and one cancelling it; and
    // the chosen recipe's sheet: its ingredients for as many as asked and where they are, its time and
    // work, what one dish gives whoever eats it or what a carcass gives, how many to make and Add to the
    // queue (taken without its ingredients: it waits for them).
    internal sealed class StationPage
    {
        private readonly StorageSync storageSync;
        private readonly InventorySync inventorySync;
        private readonly HungerSync hungerSync;
        private readonly EcosystemSync ecosystemSync;

        private readonly Label linked;
        private readonly Label fuel;
        private readonly VisualElement fuelBox;
        private readonly VisualElement burn;
        private readonly VisualElement burnFill;
        private readonly Label calories;
        private readonly Label recipesTitle;
        private readonly ScrollView recipeList;
        private readonly Label queueTitle;
        private readonly Label queueEmpty;
        private readonly VisualElement sheetIcon;
        private readonly Label sheetName;
        private readonly Label sheetKind;
        private readonly Label sheetDescription;
        private readonly VisualElement sheetIngredients;
        private readonly Label sheetDuration;
        private readonly Label sheetWork;
        private readonly VisualElement sheetOutputsBox;
        private readonly VisualElement sheetOutputs;
        private readonly VisualElement sheetFood;
        private readonly Label sheetWeight;
        private readonly Label sheetCalories;
        private readonly Label[] sheetNutrients = new Label[Nutrition.NutrientCount];
        private readonly VisualElement[] sheetNutrientBars = new VisualElement[Nutrition.NutrientCount];
        private readonly Label sheetReason;
        private readonly Button queueButton;
        private readonly QuantityStepper quantity;
        private readonly OrderRows queue;

        private readonly List<Row> rows = new List<Row>();
        private readonly Dictionary<StorageKind, int> chosen = new Dictionary<StorageKind, int>();
        private StorageKind kind = StorageKind.Campfire;
        private IReadOnlyList<Recipe> recipes = Recipes.Cooking;
        private int selected;
        private long shownKey = long.MinValue;
        private long queueKey = long.MinValue;

        private sealed class Row
        {
            public int Index;
            public VisualElement Root;
            public Label Name;
            public readonly List<Label> Counts = new List<Label>();
            public Label Seconds;
            public Label Work;
        }

        public StationPage(VisualElement root, StorageSync storageSync, InventorySync inventorySync, HungerSync hungerSync, EcosystemSync ecosystemSync)
        {
            this.storageSync = storageSync;
            this.inventorySync = inventorySync;
            this.hungerSync = hungerSync;
            this.ecosystemSync = ecosystemSync;
            linked = root.Q<Label>("fire-linked");
            fuel = root.Q<Label>("fire-fuel");
            fuelBox = root.Q("fire-fuel-box");
            burn = root.Q("fire-burn");
            burnFill = root.Q("fire-burn-fill");
            calories = root.Q<Label>("fire-calories");
            recipesTitle = root.Q<Label>("fire-recipes-title");
            queueTitle = root.Q<Label>("fire-queue-title");
            queueEmpty = root.Q<Label>("fire-queue-empty");
            sheetIcon = root.Q("fire-sheet-icon");
            sheetName = root.Q<Label>("fire-sheet-name");
            sheetKind = root.Q<Label>("fire-sheet-kind");
            sheetDescription = root.Q<Label>("fire-sheet-description");
            sheetIngredients = root.Q("fire-sheet-ingredients");
            sheetDuration = root.Q<Label>("fire-sheet-duration");
            sheetWork = root.Q<Label>("fire-sheet-work");
            sheetOutputsBox = root.Q("fire-sheet-outputs-box");
            sheetOutputs = root.Q("fire-sheet-outputs");
            sheetFood = root.Q("fire-sheet-food");
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

            quantity = new QuantityStepper(root.Q("fire-sheet-quantity"), Station.MaxCount, () => Makeable(recipes[selected]));
            quantity.Changed += () => shownKey = long.MinValue;
            queue = new OrderRows(root.Q<ScrollView>("fire-queue"), queueEmpty, storageSync.RequestCancelJob, storageSync.RequestLabour);

            recipeList = root.Q<ScrollView>("fire-recipes");
            recipeList.verticalScrollerVisibility = ScrollerVisibility.AlwaysVisible;
            BuildRows();
        }

        private bool IsButchery => kind == StorageKind.ButcherTable;

        // Whether the count is being typed, and leaving its field.
        public bool IsTyping => quantity.IsTyping;

        public void Blur() => quantity.Blur();

        public void Retranslate()
        {
            shownKey = long.MinValue;
            queueKey = long.MinValue;
            quantity.Retranslate();
        }

        // Which station the window shows: its recipes, the choice kept for it, and what it does not have.
        public void ShowKind(StorageKind shownKind)
        {
            if (shownKind == kind) return;

            chosen[kind] = selected;
            kind = shownKind;
            recipes = StorageSync.RecipesOf(kind);
            selected = chosen.TryGetValue(kind, out int kept) && kept < recipes.Count ? kept : 0;
            BuildRows();
            shownKey = long.MinValue;
            queueKey = long.MinValue;
        }

        // Every frame while the tab is shown: the bars, and the rest when what it shows changed.
        public void Refresh()
        {
            bool fire = !IsButchery;
            fuelBox.style.display = fire ? DisplayStyle.Flex : DisplayStyle.None;
            burn.style.display = fire ? DisplayStyle.Flex : DisplayStyle.None;
            if (fire)
            {
                fuel.text = $"{storageSync.OpenedFuel} / {Station.MaxFuel}";
                burnFill.style.width = Length.Percent(Mathf.Clamp01(storageSync.OpenedBurning / storageSync.OpenedLogSeconds) * 100f);
            }

            calories.text = $"{hungerSync.Stomach.Calories:0} kcal";
            ShowQueue();

            long key = KeyOf();
            if (key == shownKey) return;

            shownKey = key;
            recipesTitle.text = IsButchery ? Loc.T("Carcasses") : Loc.T("Dishes");
            queueEmpty.text = IsButchery ? Loc.T("Nothing queued: choose a carcass, then Add to the queue.") : Loc.T("Nothing queued: choose a dish, then Add to the queue.");
            int linkedChests = storageSync.OpenedLinkedChests;
            linked.text = linkedChests == 0 ? Loc.T("No linked chest")
                : linkedChests == 1 ? Loc.T("1 linked chest") : Loc.T("{0} linked chests", linkedChests);
            linked.EnableInClassList("fire-linked--none", linkedChests == 0);
            foreach (Row row in rows)
            {
                ShowRow(row);
            }

            if (IsButchery) SortRows();
            ShowSheet();
        }

        // What the list shows: a dish (what it makes) or a carcass (what it takes).
        private int ShownItem(Recipe recipe) => IsButchery ? recipe.Ingredients[0].Item : recipe.Output;

        private void BuildRows()
        {
            recipeList.Clear();
            rows.Clear();
            for (int r = 0; r < recipes.Count; r++)
            {
                recipeList.Add(MakeRow(r));
            }
        }

        // At a butcher's table, the carcasses at hand (bag and linked chests) first, then the others, each
        // in species order.
        private void SortRows()
        {
            var order = new List<Row>(rows);
            order.Sort((a, b) =>
            {
                bool aHave = Makeable(recipes[a.Index]) > 0;
                bool bHave = Makeable(recipes[b.Index]) > 0;
                return aHave != bHave ? (aHave ? -1 : 1) : a.Index.CompareTo(b.Index);
            });

            for (int i = 0; i < order.Count; i++)
            {
                if (recipeList.contentContainer.IndexOf(order[i].Root) != i) recipeList.Insert(i, order[i].Root);
            }
        }

        private VisualElement MakeRow(int index)
        {
            Recipe recipe = recipes[index];
            var row = new Row { Index = index, Root = new VisualElement() };
            row.Root.AddToClassList("recipe-row");
            var picture = new VisualElement { pickingMode = PickingMode.Ignore };
            picture.AddToClassList("recipe-row-icon");
            picture.style.backgroundImage = new StyleBackground(ItemCatalog.IconOf(ShownItem(recipe), ecosystemSync));
            row.Root.Add(picture);

            var text = new VisualElement { pickingMode = PickingMode.Ignore };
            text.AddToClassList("recipe-row-text");
            row.Name = new Label { pickingMode = PickingMode.Ignore };
            row.Name.AddToClassList("recipe-row-name");
            text.Add(row.Name);
            var costs = new VisualElement { pickingMode = PickingMode.Ignore };
            costs.AddToClassList("recipe-row-cost");

            // A dish shows what it takes; a carcass what it gives (and how many are at hand, in its name).
            if (IsButchery)
            {
                foreach ((int item, int count) in recipe.Outputs)
                {
                    costs.Add(Chip(item, $"×{count}", out _));
                }
            }
            else
            {
                foreach (Ingredient ingredient in recipe.Ingredients)
                {
                    costs.Add(Chip(ingredient.Item, "", out Label count));
                    row.Counts.Add(count);
                }
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

        private VisualElement Chip(int item, string text, out Label count)
        {
            var chip = new VisualElement { pickingMode = PickingMode.Ignore, tooltip = ItemCatalog.NameOf(item, ecosystemSync) };
            chip.AddToClassList("recipe-chip");
            var chipIcon = new VisualElement { pickingMode = PickingMode.Ignore };
            chipIcon.AddToClassList("recipe-chip-icon");
            chipIcon.style.backgroundImage = new StyleBackground(ItemCatalog.IconOf(item, ecosystemSync));
            count = new Label(text) { pickingMode = PickingMode.Ignore };
            count.AddToClassList("recipe-chip-text");
            chip.Add(chipIcon);
            chip.Add(count);
            return chip;
        }

        // What a dish takes, as the bag and the linked chests hold it; how many of a carcass are at hand.
        private void ShowRow(Row row)
        {
            Recipe recipe = recipes[row.Index];
            string name = ItemCatalog.NameOf(ShownItem(recipe), ecosystemSync);
            if (IsButchery)
            {
                int have = Have(recipe.Ingredients[0].Item);
                row.Name.text = have > 0 ? Loc.T("{0} ({1})", name, have) : name;
            }
            else
            {
                row.Name.text = name;
                for (int i = 0; i < recipe.Ingredients.Count; i++)
                {
                    Ingredient ingredient = recipe.Ingredients[i];
                    int have = Have(ingredient.Item);
                    row.Counts[i].text = $"{Mathf.Min(have, 9999)} / {ingredient.Count}";
                    row.Counts[i].EnableInClassList("recipe-chip-text--missing", have < ingredient.Count);
                    row.Counts[i].parent.tooltip = ItemCatalog.NameOf(ingredient.Item, ecosystemSync);
                }
            }

            row.Seconds.text = Loc.T("{0} s", Loc.Number(recipe.Seconds, "0.#"));
            row.Work.text = $"{recipe.Calories:0} kcal";
            row.Root.EnableInClassList("recipe-row--selected", row.Index == selected);
            row.Root.EnableInClassList("recipe-row--unavailable", Makeable(recipe) == 0);
        }

        private void ShowSheet()
        {
            Recipe recipe = recipes[selected];
            int count = quantity.Value;
            int shown = ShownItem(recipe);
            sheetIcon.style.backgroundImage = new StyleBackground(ItemCatalog.IconOf(shown, ecosystemSync));
            sheetName.text = ItemCatalog.NameOf(shown, ecosystemSync);
            sheetKind.text = IsButchery ? Loc.T("Butchering") : ItemCatalog.KindName(shown);
            sheetDescription.text = IsButchery
                ? Loc.T("Cut up on the butcher's table: its meat, hide, bones and fat go into the linked chests. A knife in the field takes only half its meat.")
                : ItemCatalog.DescriptionOf(shown, ecosystemSync);
            sheetDuration.text = CraftingPage.Times(Loc.T("{0} s", Loc.Number(recipe.Seconds, "0.#")), Loc.T("{0} s", Loc.Number(recipe.Seconds * count, "0.#")), count);
            sheetWork.text = CraftingPage.Times($"{recipe.Calories:0} kcal", $"{recipe.Calories * count:0} kcal", count);

            // What a carcass gives, for as many as asked; or what one dish gives whoever eats it.
            sheetOutputsBox.style.display = IsButchery ? DisplayStyle.Flex : DisplayStyle.None;
            sheetFood.style.display = IsButchery ? DisplayStyle.None : DisplayStyle.Flex;
            sheetOutputs.Clear();
            if (IsButchery)
            {
                foreach ((int item, int each) in recipe.Outputs)
                {
                    sheetOutputs.Add(Line(item, $"{each * count}"));
                }
            }
            else
            {
                Nutrition food = ecosystemSync.FoodOf(recipe.Output);
                sheetWeight.text = Loc.T("{0} kg each", Loc.Number(ecosystemSync.ItemWeights.Of(recipe.Output), "0.###"));
                sheetCalories.text = $"{food.Calories:0} kcal";
                for (int i = 0; i < Nutrition.NutrientCount; i++)
                {
                    float points = food.Nutrient(i);
                    sheetNutrients[i].text = points.ToString("0");
                    sheetNutrientBars[i].style.width = Length.Percent(Mathf.Clamp01(points / ItemCatalog.FullNutrient) * 100f);
                }
            }

            // Each ingredient for as many as asked, and where it is: the bag, the linked chests.
            sheetIngredients.Clear();
            foreach (Ingredient ingredient in recipe.Ingredients)
            {
                int bag = inventorySync.Inventory.Count(ingredient.Item);
                int chests = storageSync.OpenedLinkedContents.Count(ingredient.Item);
                int need = ingredient.Count * count;
                VisualElement line = Line(ingredient.Item, $"{bag + chests} / {need}");
                line.Q<Label>(className: "details-line-value").EnableInClassList("ingredient-value--missing", bag + chests < need);
                sheetIngredients.Add(line);
                var source = new Label(Loc.T("bag {0} · linked chests {1}", bag, chests));
                source.AddToClassList("ingredient-source");
                sheetIngredients.Add(source);
            }

            // Why it cannot be queued; in amber, what it will wait for.
            bool full = storageSync.OpenedJobs.Count >= Station.MaxJobs;
            queueButton.SetEnabled(!full);
            string warning = storageSync.OpenedLinkedChests == 0
                ? (IsButchery ? Loc.T("No linked chest: what it cuts up needs one to go into") : Loc.T("No linked chest: dishes need one to go into"))
                : Makeable(recipe) < count ? Loc.T("Missing ingredients: the order will wait for them in the linked chests") : "";
            sheetReason.text = full ? Loc.T("The queue is full ({0})", Station.MaxJobs) : warning;
            sheetReason.EnableInClassList("recipe-reason--warning", !full);
            sheetReason.style.display = sheetReason.text.Length > 0 ? DisplayStyle.Flex : DisplayStyle.None;
        }

        // A line of the sheet: an item's icon, its name and a value.
        private VisualElement Line(int item, string value)
        {
            var line = new VisualElement();
            line.AddToClassList("details-line");
            var picture = new VisualElement();
            picture.AddToClassList("ingredient-icon");
            picture.style.backgroundImage = new StyleBackground(ItemCatalog.IconOf(item, ecosystemSync));
            var label = new Label(ItemCatalog.NameOf(item, ecosystemSync));
            label.AddToClassList("details-line-label");
            var text = new Label(value);
            text.AddToClassList("details-line-value");
            line.Add(picture);
            line.Add(label);
            line.Add(text);
            return line;
        }

        // How many of a recipe the bag and the linked chests hold the ingredients for.
        private int Makeable(Recipe recipe)
        {
            int count = Station.MaxCount;
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
            IReadOnlyList<StationJobView> jobs = storageSync.OpenedJobs;
            long key = 17 + (long)kind;
            foreach (StationJobView job in jobs)
            {
                key = key * 31 + job.Id;
                key = key * 31 + job.Count;
                key = key * 31 + job.Stocked;
            }

            if (key != queueKey)
            {
                queueKey = key;
                queueTitle.text = Loc.T("Queue ({0} / {1})", jobs.Count, Station.MaxJobs);
                var lines = new List<OrderRows.Line>();
                foreach (StationJobView job in jobs)
                {
                    int shown = ShownItem(job.Made);
                    string name = ItemCatalog.NameOf(shown, ecosystemSync);
                    string by = string.IsNullOrEmpty(job.Creator) ? "" : Loc.T("by {0}", job.Creator);
                    if (job.Count > 1 || job.Stocked < job.Count)
                    {
                        string stocked = Loc.T("ingredients for {0} / {1}", job.Stocked, job.Count);
                        by = by.Length > 0 ? $"{by} · {stocked}" : stocked;
                    }
                    lines.Add(new OrderRows.Line
                    {
                        Id = job.Id,
                        Icon = ItemCatalog.IconOf(shown, ecosystemSync),
                        Name = job.Count > 1 ? Loc.T("{0} ×{1}", name, job.Count) : name,
                        Under = by
                    });
                }

                queue.Build(lines);
            }

            StationJobView working = storageSync.CookingJob;
            foreach (StationJobView job in jobs)
            {
                ShowJob(job, working);
            }
        }

        // An order's two bars, both always shown: the calories put into it, and how far the one at its
        // front is; then what it waits for, or the work.
        private void ShowJob(StationJobView job, StationJobView working)
        {
            Recipe recipe = job.Made;
            float total = recipe.Calories * job.Count;
            float put = total - job.LabourLeft;
            queue.SetLabour(job.Id, put / total, Loc.T("{0} / {1} kcal", put.ToString("0"), total.ToString("0")));

            float left = storageSync.SecondsLeftOf(job);
            float done = 1f - left / recipe.Seconds;
            bool more = job.LabourLeft > 0f;
            if (!job.UnitPaid)
            {
                queue.Set(job.Id, done, true, IsButchery ? Loc.T("Needs calories to butcher") : Loc.T("Needs calories to cook"), true, true, false);
                return;
            }

            if (job.Stocked == 0)
            {
                queue.Set(job.Id, done, true, Loc.T("Missing ingredients: put them in a linked chest"), true, more, false);
                return;
            }

            if (job != working)
            {
                queue.Set(job.Id, done, true, Loc.T("Ready: waits its turn"), true, more, false);
                return;
            }

            switch (storageSync.OpenedStatus)
            {
                case StationStatus.NoFuel:
                    queue.Set(job.Id, done, true, Loc.T("Needs firewood"), true, more, false);
                    break;
                case StationStatus.NoRoom:
                    queue.Set(job.Id, done, true, Loc.T(storageSync.OpenedLinkedChests == 0 ? "No linked chest to put it in" : "No room in the linked chests"), true, more, false);
                    break;
                default:
                    string doing = IsButchery ? Loc.T("Butchering… {0} s", Mathf.CeilToInt(left)) : Loc.T("Cooking… {0} s", Mathf.CeilToInt(left));
                    queue.Set(job.Id, done, true, doing, false, more, storageSync.OpenedStatus == StationStatus.Working);
                    break;
            }
        }

        // What the list and sheet show depends on: the station, the bag, the linked chests, the queue's
        // length, the recipe and quantity chosen.
        private long KeyOf()
        {
            long key = 17 + (long)kind;
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

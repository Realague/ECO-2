using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using Voxel.Core;

namespace Voxel.Client
{
    // The storage window's page for a campfire (Storage.uxml: campfire-half), run by StorageScreen: the
    // wood by the fire and what is left of the log alight, how many chests are linked, the dishes it
    // cooks (what each takes, as the bag and the linked chests hold it; a click queues one) and its
    // queue: each dish with who queued it, the calories put in or how far it has cooked, what it waits
    // for, a button putting 100 kcal into it (Shift: all it needs) and one cancelling it.
    internal sealed class CampfirePage
    {
        private readonly StorageSync storageSync;
        private readonly InventorySync inventorySync;
        private readonly EcosystemSync ecosystemSync;

        private readonly Label linked;
        private readonly Label fuel;
        private readonly VisualElement burnFill;
        private readonly ScrollView recipes;
        private readonly ScrollView queue;
        private readonly Label queueTitle;
        private readonly Label queueEmpty;

        private readonly List<Row> rows = new List<Row>();
        private readonly List<JobRow> jobRows = new List<JobRow>();
        private long shownKey = long.MinValue;

        private sealed class Row
        {
            public VisualElement Root;
            public Label Name;
            public readonly List<Label> Counts = new List<Label>();
            public Label Meta;
        }

        private sealed class JobRow
        {
            // The dish's id: its view is looked up again each frame (each state from the server makes new ones).
            public int Id;
            public VisualElement Root;
            public VisualElement Fill;
            public Label Status;
            public Button Labour;
        }

        public CampfirePage(VisualElement root, StorageSync storageSync, InventorySync inventorySync, EcosystemSync ecosystemSync)
        {
            this.storageSync = storageSync;
            this.inventorySync = inventorySync;
            this.ecosystemSync = ecosystemSync;
            linked = root.Q<Label>("fire-linked");
            fuel = root.Q<Label>("fire-fuel");
            burnFill = root.Q("fire-burn-fill");
            recipes = root.Q<ScrollView>("fire-recipes");
            queue = root.Q<ScrollView>("fire-queue");
            queueTitle = root.Q<Label>("fire-queue-title");
            queueEmpty = root.Q<Label>("fire-queue-empty");
            recipes.verticalScrollerVisibility = ScrollerVisibility.AlwaysVisible;
            queue.verticalScrollerVisibility = ScrollerVisibility.Auto;
            for (int r = 0; r < Recipes.Cooking.Count; r++)
            {
                AddRow(r);
            }
        }

        public void Retranslate() => shownKey = long.MinValue;

        // Every frame while a campfire is open: the bars, and the rest when what it shows changed.
        public void Refresh()
        {
            int linkedChests = storageSync.OpenedLinkedChests;
            fuel.text = $"{storageSync.OpenedFuel} / {Campfire.MaxFuel}";
            burnFill.style.width = Length.Percent(Mathf.Clamp01(storageSync.OpenedBurning / Campfire.SecondsPerWood) * 100f);
            ShowBars();

            long key = KeyOf();
            if (key == shownKey) return;

            shownKey = key;
            linked.text = linkedChests == 0 ? Loc.T("No linked chest")
                : linkedChests == 1 ? Loc.T("1 linked chest") : Loc.T("{0} linked chests", linkedChests);
            linked.EnableInClassList("fire-linked--none", linkedChests == 0);
            for (int r = 0; r < rows.Count; r++)
            {
                ShowRow(r);
            }

            BuildQueue();
        }

        private void AddRow(int index)
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
            var meta = new VisualElement { pickingMode = PickingMode.Ignore };
            meta.AddToClassList("recipe-row-meta");
            row.Meta = new Label { pickingMode = PickingMode.Ignore };
            row.Meta.AddToClassList("recipe-row-meta-text");
            meta.Add(row.Meta);
            row.Root.Add(meta);
            row.Root.RegisterCallback<PointerDownEvent>(e =>
            {
                if (e.button == 0) storageSync.RequestQueue(index);
            });
            recipes.Add(row.Root);
            rows.Add(row);
        }

        // What a dish takes, as the bag and the linked chests hold it; greyed when it cannot be queued.
        private void ShowRow(int index)
        {
            Recipe recipe = Recipes.Cooking[index];
            Row row = rows[index];
            bool missing = false;
            row.Name.text = ItemCatalog.NameOf(recipe.Output, ecosystemSync);
            for (int i = 0; i < recipe.Ingredients.Count; i++)
            {
                Ingredient ingredient = recipe.Ingredients[i];
                int have = Have(ingredient.Item);
                row.Counts[i].text = $"{Mathf.Min(have, 9999)} / {ingredient.Count}";
                row.Counts[i].EnableInClassList("recipe-chip-text--missing", have < ingredient.Count);
                row.Counts[i].parent.tooltip = ItemCatalog.NameOf(ingredient.Item, ecosystemSync);
                missing |= have < ingredient.Count;
            }

            row.Meta.text = $"{recipe.Calories:0} kcal · {Loc.T("{0} s", Loc.Number(recipe.Seconds, "0.#"))}";
            bool full = storageSync.OpenedJobs.Count >= Campfire.MaxJobs;
            row.Root.EnableInClassList("recipe-row--unavailable", missing || full);
        }

        private int Have(int item) => inventorySync.Inventory.Count(item) + storageSync.OpenedLinkedContents.Count(item);

        // The queue, laid out again when it changed.
        private void BuildQueue()
        {
            queue.Clear();
            jobRows.Clear();
            IReadOnlyList<CookJobView> jobs = storageSync.OpenedJobs;
            queueTitle.text = Loc.T("Queue ({0} / {1})", jobs.Count, Campfire.MaxJobs);
            queueEmpty.style.display = jobs.Count == 0 ? DisplayStyle.Flex : DisplayStyle.None;
            foreach (CookJobView job in jobs)
            {
                queue.Add(MakeJobRow(job));
            }

            ShowBars();
        }

        private VisualElement MakeJobRow(CookJobView job)
        {
            Recipe recipe = Recipes.Cooking[job.Recipe];
            var row = new JobRow { Id = job.Id, Root = new VisualElement() };
            row.Root.AddToClassList("job-row");
            var picture = new VisualElement { pickingMode = PickingMode.Ignore };
            picture.AddToClassList("job-icon");
            picture.style.backgroundImage = new StyleBackground(ItemCatalog.IconOf(recipe.Output, ecosystemSync));
            row.Root.Add(picture);

            var text = new VisualElement { pickingMode = PickingMode.Ignore };
            text.AddToClassList("job-text");
            var name = new Label(ItemCatalog.NameOf(recipe.Output, ecosystemSync)) { pickingMode = PickingMode.Ignore };
            name.AddToClassList("job-name");
            var by = new Label(string.IsNullOrEmpty(job.Creator) ? "" : Loc.T("by {0}", job.Creator)) { pickingMode = PickingMode.Ignore };
            by.AddToClassList("job-by");
            var bar = new VisualElement { pickingMode = PickingMode.Ignore };
            bar.AddToClassList("job-bar");
            row.Fill = new VisualElement { pickingMode = PickingMode.Ignore };
            row.Fill.AddToClassList("job-bar-fill");
            bar.Add(row.Fill);
            row.Status = new Label { pickingMode = PickingMode.Ignore };
            row.Status.AddToClassList("job-status");
            text.Add(name);
            text.Add(by);
            text.Add(bar);
            text.Add(row.Status);
            row.Root.Add(text);

            // Calories go in 100 at a time; Shift puts in all it still needs (as far as the player has them).
            row.Labour = new Button { focusable = false, text = Loc.T("+{0} kcal", StorageSync.LabourStep.ToString("0")), tooltip = Loc.T("Shift + click: all it needs") };
            row.Labour.AddToClassList("button");
            row.Labour.AddToClassList("button--primary");
            row.Labour.AddToClassList("job-labour");
            row.Labour.RegisterCallback<ClickEvent>(e => storageSync.RequestLabour(job.Id, e.shiftKey));
            row.Root.Add(row.Labour);

            var cancel = new Button { focusable = false, text = "✕", tooltip = Loc.T("Cancel: the ingredients come back, the calories put in are lost") };
            cancel.AddToClassList("button");
            cancel.AddToClassList("button--secondary");
            cancel.AddToClassList("job-cancel");
            cancel.clicked += () => storageSync.RequestCancelJob(job.Id);
            row.Root.Add(cancel);

            jobRows.Add(row);
            return row.Root;
        }

        // Each dish's bar and what it waits for: the calories put in, else how far it has cooked.
        private void ShowBars()
        {
            CookJobView cooking = storageSync.CookingJob;
            foreach (JobRow row in jobRows)
            {
                CookJobView job = null;
                foreach (CookJobView view in storageSync.OpenedJobs)
                {
                    if (view.Id == row.Id) job = view;
                }

                if (job == null) continue;

                Recipe recipe = Recipes.Cooking[job.Recipe];
                bool laboured = job.LabourLeft <= 0f;
                row.Labour.style.display = laboured ? DisplayStyle.None : DisplayStyle.Flex;
                row.Root.EnableInClassList("job-row--cooking", job == cooking && storageSync.OpenedStatus == CampfireStatus.Cooking);
                row.Fill.EnableInClassList("job-bar-fill--cooking", laboured);
                if (!laboured)
                {
                    float put = recipe.Calories - job.LabourLeft;
                    row.Fill.style.width = Length.Percent(Mathf.Clamp01(put / recipe.Calories) * 100f);
                    SetStatus(row, Loc.T("{0} / {1} kcal: needs calories", put.ToString("0"), recipe.Calories.ToString("0")), true);
                    continue;
                }

                float left = storageSync.SecondsLeftOf(job);
                row.Fill.style.width = Length.Percent(Mathf.Clamp01(1f - left / recipe.Seconds) * 100f);
                if (job != cooking)
                {
                    SetStatus(row, Loc.T("Ready: waits its turn"), true);
                    continue;
                }

                switch (storageSync.OpenedStatus)
                {
                    case CampfireStatus.NoFuel:
                        SetStatus(row, Loc.T("Needs firewood"), true);
                        break;
                    case CampfireStatus.NoRoom:
                        SetStatus(row, Loc.T(storageSync.OpenedLinkedChests == 0 ? "No linked chest to put it in" : "No room in the linked chests"), true);
                        break;
                    default:
                        SetStatus(row, Loc.T("Cooking… {0} s", Mathf.CeilToInt(left)), false);
                        break;
                }
            }
        }

        private static void SetStatus(JobRow row, string text, bool waiting)
        {
            row.Status.text = text;
            row.Status.EnableInClassList("job-status--waiting", waiting);
        }

        // What the rows show depends on: the bag, the linked chests, the queue and the campfire's state.
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

            key = key * 31 + (int)storageSync.OpenedStatus;
            key = key * 31 + storageSync.OpenedLinkedChests;
            foreach (CookJobView job in storageSync.OpenedJobs)
            {
                key = key * 31 + job.Id;
                key = key * 31 + (long)job.LabourLeft;
                key = key * 31 + (long)(job.SecondsLeft * 10f);
            }

            return key;
        }
    }
}

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using Voxel.Core;

namespace Voxel.Client
{
    // The inventory's Creative tab (creative-page in Assets/UI/Inventory.uxml): a button switching the
    // local player's creative mode on or off (InventorySync.RequestCreative) with a reminder of what it
    // does, then categories, a search and every item of the game (ItemCatalog.AllItems) with how many
    // the player holds. In creative mode a click takes a stack (CreativeRules.StackOf), a right click
    // one (InventorySync.RequestCreativeGive); out of it the catalog is greyed. In creative mode a red
    // button empties the whole bag and hotbar after a second click (RequestCreativeClear). The item
    // under the pointer shows on the inventory's details sheet (InventoryScreen).
    internal sealed class CreativePage
    {
        private const string SelectedCategoryClass = "category--selected";

        private readonly InventorySync inventorySync;
        private readonly EcosystemSync ecosystemSync;
        private readonly VisualElement page;
        private readonly Button toggle;
        private readonly Label toggleText;
        private readonly Label hint;
        private readonly Button clear;
        private readonly Label clearText;
        private readonly TextField search;
        private readonly VisualElement searchHint;
        private readonly Label empty;
        private readonly ItemGrid grid;
        private readonly List<Button> categoryButtons = new List<Button>();
        private readonly List<ItemCategory?> categoryKinds = new List<ItemCategory?>();
        private readonly List<Label> categoryNames = new List<Label>();
        private readonly List<KeyValuePair<int, int>> entries = new List<KeyValuePair<int, int>>();
        private List<int> catalog;
        private ItemCategory? category;
        private bool? shownCreative;

        // Emptying the bag asks first: a click arms the button for ConfirmSeconds, a second one empties.
        private const float ConfirmSeconds = 3f;
        private float confirmUntil = -1f;
        private string shownClearText;

        public CreativePage(VisualElement root, InventorySync inventorySync, EcosystemSync ecosystemSync)
        {
            this.inventorySync = inventorySync;
            this.ecosystemSync = ecosystemSync;
            page = root.Q("creative-page");
            toggle = root.Q<Button>("creative-toggle");
            toggleText = root.Q<Label>("creative-toggle-text");
            hint = root.Q<Label>("creative-hint");
            search = root.Q<TextField>("creative-search");
            searchHint = root.Q("creative-search-hint");
            empty = root.Q<Label>("creative-empty");
            grid = new ItemGrid(root.Q<ScrollView>("creative-grid"), ecosystemSync, 5);
            grid.Pressed += OnPressed;

            toggle.focusable = false;
            toggle.clicked += () => inventorySync.RequestCreative(!inventorySync.Creative);

            clear = root.Q<Button>("creative-clear");
            clearText = root.Q<Label>("creative-clear-text");
            clear.focusable = false;
            clear.clicked += OnClear;

            VisualElement searchBox = root.Q("creative-search-box");
            search.RegisterValueChangedCallback(e => searchHint.style.display = string.IsNullOrEmpty(e.newValue) ? DisplayStyle.Flex : DisplayStyle.None);
            search.RegisterCallback<FocusInEvent>(_ => searchBox.AddToClassList("item-search-box--focused"));
            search.RegisterCallback<FocusOutEvent>(_ => searchBox.RemoveFromClassList("item-search-box--focused"));

            ScrollView categoryList = root.Q<ScrollView>("creative-category-list");
            AddCategory(categoryList, null);
            foreach (ItemCategory kind in ItemCatalog.Categories)
            {
                AddCategory(categoryList, kind);
            }

            Show(false);
        }

        // The catalog item under the pointer, or Items.None.
        public int HoveredItem => grid.HoveredItem;

        public bool IsTyping => search.focusController?.focusedElement == search;

        public void Blur() => search.Blur();

        public void Show(bool shown)
        {
            page.style.display = shown ? DisplayStyle.Flex : DisplayStyle.None;
            shownCreative = null;
            confirmUntil = -1f;
            grid.Invalidate();
        }

        public void Retranslate()
        {
            for (int c = 0; c < categoryNames.Count; c++)
            {
                categoryNames[c].text = InventoryScreen.CategoryTitle(categoryKinds[c]);
            }

            shownCreative = null;
            shownClearText = null;
            grid.Invalidate();
        }

        // Every frame while shown: the button, the reminder, and the catalog (redrawn only on a change).
        public void Refresh()
        {
            bool creative = inventorySync.Creative;
            if (shownCreative != creative)
            {
                shownCreative = creative;
                toggle.EnableInClassList("creative-toggle--on", creative);
                toggleText.text = Loc.T(creative ? "Creative mode: on" : "Creative mode: off");
                hint.text = Loc.T(creative
                    ? "Double Space: fly · one hit breaks anything · no hunger, no carrying limit. Click: a stack, right click: one."
                    : "Switch creative mode on to take any item for free, fly and break anything in one hit.");
            }

            if (catalog == null || catalog.Count == 0) catalog = new List<int>(ItemCatalog.AllItems(ecosystemSync));

            for (int c = 0; c < categoryButtons.Count; c++)
            {
                categoryButtons[c].EnableInClassList(SelectedCategoryClass, categoryKinds[c] == category);
            }

            string text = (search.value ?? "").Trim();
            entries.Clear();
            Inventory inventory = inventorySync.Inventory;
            foreach (int item in catalog)
            {
                if (category.HasValue && ItemCatalog.CategoryOf(item) != category.Value) continue;
                if (text.Length > 0 && ItemCatalog.NameOf(item, ecosystemSync).IndexOf(text, StringComparison.OrdinalIgnoreCase) < 0) continue;

                entries.Add(new KeyValuePair<int, int>(item, inventory.Count(item)));
            }

            grid.Show(entries, _ => !creative);
            empty.text = entries.Count == 0 ? Loc.T("No item matches.") : "";
            empty.style.display = entries.Count == 0 ? DisplayStyle.Flex : DisplayStyle.None;
            ShowClear(creative, inventory);
        }

        // The button that empties the bag: in creative mode with something in the bag; armed, it asks
        // for a second click, and disarms after ConfirmSeconds.
        private void ShowClear(bool creative, Inventory inventory)
        {
            bool any = false;
            foreach (KeyValuePair<int, int> entry in inventory.Entries)
            {
                if (entry.Value > 0)
                {
                    any = true;
                    break;
                }
            }

            bool shown = creative && any;
            clear.style.display = shown ? DisplayStyle.Flex : DisplayStyle.None;
            if (!shown) confirmUntil = -1f;

            bool armed = Time.unscaledTime < confirmUntil;
            clear.EnableInClassList("creative-clear--confirm", armed);
            string text = armed ? "Click again to empty it" : "Empty the inventory";
            if (text == shownClearText) return;

            shownClearText = text;
            clearText.text = Loc.T(text);
        }

        private void OnClear()
        {
            if (!inventorySync.Creative) return;

            if (Time.unscaledTime < confirmUntil)
            {
                confirmUntil = -1f;
                inventorySync.RequestCreativeClear();
            }
            else
            {
                confirmUntil = Time.unscaledTime + ConfirmSeconds;
            }
        }

        // A click takes a stack, a right click one; nothing out of creative mode (the slots are greyed).
        private void OnPressed(int item, VisualElement slot, PointerDownEvent e)
        {
            if (!inventorySync.Creative) return;

            if (e.button == 0) inventorySync.RequestCreativeGive(item, CreativeRules.StackOf(item));
            else if (e.button == 1) inventorySync.RequestCreativeGive(item, 1);
        }

        private void AddCategory(ScrollView list, ItemCategory? kind)
        {
            var button = new Button { focusable = false };
            button.AddToClassList("category");
            var icon = new VisualElement();
            icon.AddToClassList("icon");
            icon.AddToClassList(InventoryScreen.CategoryIcon(kind));
            var name = new Label(InventoryScreen.CategoryTitle(kind));
            name.AddToClassList("category-name");
            categoryNames.Add(name);
            button.Add(icon);
            button.Add(name);
            button.clicked += () => category = kind;

            list.Add(button);
            categoryButtons.Add(button);
            categoryKinds.Add(kind);
        }
    }
}

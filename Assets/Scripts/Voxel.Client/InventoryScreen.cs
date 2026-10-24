using System;
using System.Collections.Generic;
using System.Globalization;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;
using Voxel.Core;

namespace Voxel.Client
{
    // The inventory (Assets/UI/Inventory.uxml), opened and closed with Tab, closed with Escape:
    // categories, a grid of slots holding the items the player owns (searchable, sorted by kind then
    // name, empty slots after them), the details of the item under the pointer or else the chosen one
    // (what it is, how many, its hotbar slot, a food's nutrition) and the speciality (chosen once, here). Items go into the hotbar, which stays shown below, by dragging
    // them onto a slot, by clicking a slot with an item chosen, or with the digits 1-0; a right click
    // empties a slot. A right click on a food in the grid eats one. M goes to the full-screen map.
    // While it is open the mouse is free and the player stands still (HoldsInput).
    public class InventoryScreen : MonoBehaviour
    {
        private const string SelectedItemClass = "item-slot--selected";
        private const string SelectedCategoryClass = "category--selected";
        private const float DragThreshold = 8f;

        // The grid is filled out with empty slots to whole rows, and at least this many rows.
        private const int MinimumRows = 5;

        // Slots are about this wide, as many columns as fit, then widened to fill the well; the gap is
        // their margin (Inventory.uss, .item-slot).
        private const float TargetSlot = 124f;
        private const float SlotGap = 12f;
        private const float FitSlack = 2f;

        // Nutrient points a food bar shows full (foods have 0 to about 15, as in Eco).
        private const float FullNutrient = 15f;

        [SerializeField] private UIDocument document;
        [SerializeField] private NetworkManager networkManager;
        [SerializeField] private WorldSync worldSync;
        [SerializeField] private InventorySync inventorySync;
        [SerializeField] private HungerSync hungerSync;
        [SerializeField] private EcosystemSync ecosystemSync;
        [SerializeField] private AppearanceMenu appearanceMenu;
        [SerializeField] private MapScreen mapScreen;
        [SerializeField] private HotbarHud hotbar;

        private VisualElement screen;
        private ScrollView grid;
        private Label carryBlocks;
        private Label carryWeight;
        private Label empty;
        private TextField search;
        private VisualElement searchBox;
        private VisualElement searchHint;
        private int gridColumns;
        private float slotSize;
        private VisualElement details;
        private VisualElement detailsIcon;
        private Label detailsName;
        private Label detailsKind;
        private Label detailsDescription;
        private Label detailsCount;
        private Label detailsSlot;
        private VisualElement detailsWeightLine;
        private Label detailsWeight;
        private VisualElement detailsFood;
        private Label detailsCalories;
        private readonly Label[] detailsNutrients = new Label[Nutrition.NutrientCount];
        private readonly VisualElement[] detailsNutrientBars = new VisualElement[Nutrition.NutrientCount];
        private Label specialityName;
        private Button specialityChoose;
        private VisualElement ghost;

        // Categories down the left: null is "All".
        private readonly List<Button> categoryButtons = new List<Button>();
        private readonly List<ItemCategory?> categoryKinds = new List<ItemCategory?>();
        private readonly List<Label> categoryNames = new List<Label>();
        private ItemCategory? category;

        // The items shown and what they were made from (to notice a change).
        private readonly List<KeyValuePair<int, int>> shownEntries = new List<KeyValuePair<int, int>>();
        private readonly Dictionary<int, VisualElement> itemSlots = new Dictionary<int, VisualElement>();
        private readonly List<int> sorted = new List<int>();
        private string shownSearch;
        private int selectedItem = Items.None;
        private int hoveredItem = Items.None;
        private int closedFrame = -1;

        // Dragging an item toward the hotbar.
        private int pressedItem = Items.None;
        private Vector2 pressedAt;
        private bool dragging;

        public bool IsOpen { get; private set; }

        // Also true on the frame it closes, so that its key does nothing else.
        public bool HoldsInput => IsOpen || Time.frameCount == closedFrame;

        private void Start()
        {
            VisualElement root = document.rootVisualElement;
            screen = root.Q("screen");
            grid = root.Q<ScrollView>("item-grid");

            // Items flow in rows that wrap, inside the scrolled content; the well's width decides how
            // many columns and how wide the slots are, so the grid is laid out again when it changes.
            grid.contentContainer.style.flexDirection = FlexDirection.Row;
            grid.contentContainer.style.flexWrap = Wrap.Wrap;
            // A scroll bar always shown, as in the mockup: one coming and going would change the width.
            grid.verticalScrollerVisibility = ScrollerVisibility.AlwaysVisible;
            grid.contentViewport.RegisterCallback<GeometryChangedEvent>(_ =>
            {
                SlotLayout(out int columns, out float size);
                if (columns != gridColumns || !Mathf.Approximately(size, slotSize)) shownSearch = null;
            });
            carryBlocks = root.Q<Label>("carry-blocks");
            carryWeight = root.Q<Label>("carry-weight");
            empty = root.Q<Label>("item-empty");
            search = root.Q<TextField>("item-search");
            searchBox = root.Q(className: "item-search-box");
            searchHint = root.Q("item-search-hint");
            details = root.Q("details");
            detailsIcon = root.Q("details-icon");
            detailsName = root.Q<Label>("details-name");
            detailsKind = root.Q<Label>("details-kind");
            detailsDescription = root.Q<Label>("details-description");
            detailsCount = root.Q<Label>("details-count");
            detailsSlot = root.Q<Label>("details-slot");
            detailsWeightLine = root.Q("details-weight-line");
            detailsWeight = root.Q<Label>("details-weight");
            detailsFood = root.Q("details-food");
            detailsCalories = root.Q<Label>("details-calories");
            for (int i = 0; i < detailsNutrients.Length; i++)
            {
                detailsNutrients[i] = root.Q<Label>($"details-nutrient-{i}");
                detailsNutrientBars[i] = root.Q($"details-nutrient-bar-{i}");
            }
            specialityName = root.Q<Label>("speciality-name");
            specialityChoose = root.Q<Button>("speciality-choose");
            ghost = root.Q("drag-ghost");

            // The ghost moves to the hotbar's layer while dragging (drawn above this screen and the
            // bar), where this screen's style sheet does not reach: its look is set here.
            ghost.style.position = Position.Absolute;
            ghost.style.width = 72f;
            ghost.style.height = 72f;
            ghost.style.opacity = 0.85f;
            ghost.style.backgroundSize = new BackgroundSize(BackgroundSizeType.Contain);

            ScrollView categoryList = root.Q<ScrollView>("category-list");
            AddCategory(categoryList, null);
            foreach (ItemCategory kind in ItemCatalog.Categories)
            {
                AddCategory(categoryList, kind);
            }

            Connect(root.Q<Button>("tab-inventory"), () => { });
            Connect(specialityChoose, () => inventorySync.RequestSpeciality(Specialities.Choices[0]));

            search.RegisterValueChangedCallback(e =>
            {
                shownSearch = null;
                searchHint.style.display = string.IsNullOrEmpty(e.newValue) ? DisplayStyle.Flex : DisplayStyle.None;
            });
            search.RegisterCallback<FocusInEvent>(_ => searchBox.AddToClassList("item-search-box--focused"));
            search.RegisterCallback<FocusOutEvent>(_ => searchBox.RemoveFromClassList("item-search-box--focused"));
            hotbar.SlotClicked += OnHotbarSlotClicked;
            Loc.Changed += OnLanguageChanged;

            screen.style.display = DisplayStyle.None;
            if (Application.isBatchMode) enabled = false;
        }

        private void OnDestroy()
        {
            if (hotbar != null) hotbar.SlotClicked -= OnHotbarSlotClicked;
            Loc.Changed -= OnLanguageChanged;
        }

        private void Update()
        {
            NetworkObject player = networkManager.IsListening && worldSync.World != null && !appearanceMenu.IsOpen
                ? networkManager.SpawnManager?.GetLocalPlayerObject()
                : null;
            if (player == null)
            {
                if (IsOpen) Close(false);
                return;
            }

            Keyboard keyboard = Keyboard.current;
            if (keyboard != null && IsOpen && search.focusController?.focusedElement == search)
            {
                // Letters typed in the search are not keys; Escape leaves it first.
                if (keyboard.escapeKey.wasPressedThisFrame) search.Blur();
            }
            else if (keyboard != null && !CommandConsole.HoldsInput && !mapScreen.HoldsInput && !StorageScreen.HoldsInput)
            {
                if (keyboard.tabKey.wasPressedThisFrame)
                {
                    if (IsOpen) Close(true);
                    else Open();
                }
                else if (IsOpen && keyboard.escapeKey.wasPressedThisFrame)
                {
                    Close(true);
                }
                else if (IsOpen)
                {
                    int slot = HotbarHud.PressedSlot(keyboard);
                    int item = hoveredItem != Items.None ? hoveredItem : selectedItem;
                    if (slot >= 0 && item != Items.None) PutInHotbar(item, slot);
                }
            }

            if (!IsOpen) return;

            RefreshItems();
            ShowDetails();
            ShowSpeciality();
        }

        public void Open()
        {
            if (IsOpen) return;

            IsOpen = true;
            shownSearch = null;
            shownEntries.Clear();
            screen.style.display = DisplayStyle.Flex;
            UnityEngine.Cursor.lockState = CursorLockMode.None;
        }

        // relock: back to the game, the mouse steers the player again.
        public void Close(bool relock)
        {
            if (!IsOpen) return;

            IsOpen = false;
            closedFrame = Time.frameCount;
            EndDrag();
            search.Blur();
            screen.style.display = DisplayStyle.None;
            if (relock) UnityEngine.Cursor.lockState = CursorLockMode.Locked;
        }

        private void AddCategory(ScrollView list, ItemCategory? kind)
        {
            var button = new Button { focusable = false };
            button.AddToClassList("category");
            var icon = new VisualElement();
            icon.AddToClassList("icon");
            icon.AddToClassList(CategoryIcon(kind));
            var name = new Label(CategoryTitle(kind));
            name.AddToClassList("category-name");
            categoryNames.Add(name);
            button.Add(icon);
            button.Add(name);
            button.clicked += () =>
            {
                category = kind;
                shownSearch = null;
            };

            list.Add(button);
            categoryButtons.Add(button);
            categoryKinds.Add(kind);
        }

        private static string CategoryTitle(ItemCategory? kind) => kind.HasValue ? ItemCatalog.CategoryName(kind.Value) : Loc.T("All");

        // Another language: the categories' names, and the items' grid and details shown again.
        private void OnLanguageChanged()
        {
            for (int c = 0; c < categoryNames.Count; c++)
            {
                categoryNames[c].text = CategoryTitle(categoryKinds[c]);
            }

            shownSearch = null;
        }

        // The icon class (Common.uss) of a category; null is "All".
        private static string CategoryIcon(ItemCategory? kind)
        {
            switch (kind)
            {
                case ItemCategory.Tools: return "icon--category-tools";
                case ItemCategory.Blocks: return "icon--category-blocks";
                case ItemCategory.Seeds: return "icon--category-seeds";
                case ItemCategory.Food: return "icon--category-food";
                case ItemCategory.Materials: return "icon--category-materials";
                default: return "icon--category-all";
            }
        }

        // How many slots fit across the well and how wide they are, so that whole columns fill it
        // (five of the target size before it is laid out).
        private void SlotLayout(out int columns, out float size)
        {
            float width = grid.contentViewport.layout.width;
            if (float.IsNaN(width) || width < TargetSlot + SlotGap) width = 5f * (TargetSlot + SlotGap);
            columns = Mathf.Max(1, Mathf.FloorToInt(width / (TargetSlot + SlotGap)));
            // A little slack: slots filling the width exactly can wrap once the panel scales to the screen.
            size = Mathf.Floor((width - FitSlack) / columns - SlotGap);
        }

        private static void Connect(Button button, Action action)
        {
            button.focusable = false;
            button.clicked += action;
        }

        // Rebuilds the grid when the inventory, the category or the search changed.
        private void RefreshItems()
        {
            Inventory inventory = inventorySync.Inventory;
            string text = (search.value ?? "").Trim();
            bool changed = shownSearch != text + "|" + category || inventory.DistinctCount != shownEntries.Count;
            if (!changed)
            {
                int i = 0;
                foreach (KeyValuePair<int, int> entry in inventory.Entries)
                {
                    if (!shownEntries[i++].Equals(entry))
                    {
                        changed = true;
                        break;
                    }
                }
            }

            if (!changed) return;

            shownSearch = text + "|" + category;
            shownEntries.Clear();
            shownEntries.AddRange(inventory.Entries);

            for (int c = 0; c < categoryButtons.Count; c++)
            {
                categoryButtons[c].EnableInClassList(SelectedCategoryClass, categoryKinds[c] == category);
            }

            sorted.Clear();
            foreach (KeyValuePair<int, int> entry in shownEntries)
            {
                if (category.HasValue && ItemCatalog.CategoryOf(entry.Key) != category.Value) continue;
                if (text.Length > 0 && ItemCatalog.NameOf(entry.Key, ecosystemSync).IndexOf(text, StringComparison.OrdinalIgnoreCase) < 0) continue;

                sorted.Add(entry.Key);
            }

            sorted.Sort((a, b) =>
            {
                int byKind = ItemCatalog.CategoryOf(a).CompareTo(ItemCatalog.CategoryOf(b));
                return byKind != 0 ? byKind : string.Compare(ItemCatalog.NameOf(a, ecosystemSync), ItemCatalog.NameOf(b, ecosystemSync), StringComparison.OrdinalIgnoreCase);
            });

            SlotLayout(out gridColumns, out slotSize);
            grid.Clear();
            itemSlots.Clear();
            foreach (int item in sorted)
            {
                VisualElement slot = MakeItemSlot(item, inventory.Count(item));
                grid.Add(slot);
                itemSlots[item] = slot;
            }

            int slots = Mathf.Max(MinimumRows, (sorted.Count + gridColumns - 1) / gridColumns) * gridColumns;
            for (int i = sorted.Count; i < slots; i++)
            {
                var emptySlot = new VisualElement { pickingMode = PickingMode.Ignore };
                emptySlot.AddToClassList("item-slot");
                emptySlot.AddToClassList("item-slot--empty");
                Size(emptySlot);
                grid.Add(emptySlot);
            }

            if (!inventory.Has(selectedItem)) selectedItem = sorted.Count > 0 ? sorted[0] : Items.None;
            if (itemSlots.TryGetValue(selectedItem, out VisualElement selectedSlot)) selectedSlot.AddToClassList(SelectedItemClass);

            ShowCarry(inventory);
            empty.style.display = sorted.Count == 0 ? DisplayStyle.Flex : DisplayStyle.None;
            empty.text = Loc.T(shownEntries.Count == 0
                ? "Nothing here yet: break blocks, fell trees and harvest ripe crops."
                : "No item matches.");
        }

        // How much of the carrying limits is used, as counts: blocks out of 40, the rest's weight out of
        // 30 kg, gold when nearly full and red when full (CarryHud draws the same as gauges in game).
        private void ShowCarry(Inventory inventory)
        {
            int blocks = CarryLimits.BlockCount(inventory);
            float weight = CarryLimits.Weight(inventory, ecosystemSync.ItemWeights);
            carryBlocks.text = $"{blocks} / {CarryLimits.MaxBlocks}";
            carryWeight.text = $"{Loc.Number(weight, "0.0")} / {CarryLimits.MaxWeight:0} kg";
            ShowCarryShare(carryBlocks, (float)blocks / CarryLimits.MaxBlocks);
            ShowCarryShare(carryWeight, weight / CarryLimits.MaxWeight);
        }

        private static void ShowCarryShare(Label label, float share)
        {
            bool full = share >= 0.9999f;
            label.EnableInClassList("carry-count-text--high", share >= CarryHud.HighShare && !full);
            label.EnableInClassList("carry-count-text--full", full);
        }

        private void Size(VisualElement slot)
        {
            slot.style.width = slotSize;
            slot.style.height = slotSize;
        }

        private VisualElement MakeItemSlot(int item, int count)
        {
            var slot = new VisualElement { userData = item };
            slot.AddToClassList("item-slot");
            Size(slot);
            var icon = new VisualElement { pickingMode = PickingMode.Ignore };
            icon.AddToClassList("item-icon");
            icon.style.backgroundImage = new StyleBackground(ItemCatalog.IconOf(item, ecosystemSync));
            var amount = new Label(count.ToString()) { pickingMode = PickingMode.Ignore };
            amount.AddToClassList("item-count");
            slot.Add(icon);
            slot.Add(amount);

            slot.RegisterCallback<PointerEnterEvent>(_ => hoveredItem = item);
            slot.RegisterCallback<PointerLeaveEvent>(_ =>
            {
                if (hoveredItem == item) hoveredItem = Items.None;
            });
            slot.RegisterCallback<PointerDownEvent>(e =>
            {
                // A right click eats one, if it is food.
                if (e.button == 1)
                {
                    if (hungerSync.TryEat(item, out bool tooFull) && tooFull) hotbar.ShowMessage("Too full to eat");
                    return;
                }

                if (e.button != 0) return;

                Select(item);
                pressedItem = item;
                pressedAt = e.position;
                slot.CapturePointer(e.pointerId);
            });

            // The slot holds the pointer while it is pressed: the drag's moves and its end come to it.
            slot.RegisterCallback<PointerMoveEvent>(OnPointerMove);
            slot.RegisterCallback<PointerUpEvent>(OnPointerUp);
            return slot;
        }

        private void Select(int item)
        {
            if (itemSlots.TryGetValue(selectedItem, out VisualElement previous)) previous.RemoveFromClassList(SelectedItemClass);
            selectedItem = item;
            if (itemSlots.TryGetValue(selectedItem, out VisualElement current)) current.AddToClassList(SelectedItemClass);
        }

        // A press on an item becomes a drag once the pointer has moved a little; the ghost follows it.
        private void OnPointerMove(PointerMoveEvent e)
        {
            if (pressedItem == Items.None) return;

            if (!dragging && Vector2.Distance(e.position, pressedAt) >= DragThreshold)
            {
                dragging = true;
                if (ghost.parent != hotbar.Overlay) hotbar.Overlay.Add(ghost);
                ghost.style.backgroundImage = new StyleBackground(ItemCatalog.IconOf(pressedItem, ecosystemSync));
                ghost.RemoveFromClassList("hidden");
            }

            if (!dragging) return;

            Vector2 local = hotbar.Overlay.WorldToLocal(e.position);
            ghost.style.left = local.x - 36f;
            ghost.style.top = local.y - 36f;
        }

        // Dropped on a hotbar slot, the item goes there.
        private void OnPointerUp(PointerUpEvent e)
        {
            if (pressedItem == Items.None) return;

            if (dragging)
            {
                int slot = hotbar.SlotAt(e.position);
                if (slot >= 0) PutInHotbar(pressedItem, slot);
            }

            if (itemSlots.TryGetValue(pressedItem, out VisualElement pressed) && pressed.HasPointerCapture(e.pointerId)) pressed.ReleasePointer(e.pointerId);
            EndDrag();
        }

        private void EndDrag()
        {
            pressedItem = Items.None;
            dragging = false;
            ghost.AddToClassList("hidden");
        }

        // While the inventory is open: a click on a hotbar slot puts the chosen item there, a right click empties it.
        private void OnHotbarSlotClicked(int slot, int button)
        {
            if (!IsOpen) return;

            if (button == 1) inventorySync.RequestHotbarSlot(slot, Items.None);
            else if (button == 0 && selectedItem != Items.None) PutInHotbar(selectedItem, slot);
        }

        private void PutInHotbar(int item, int slot)
        {
            if (item == Items.None || slot < 0) return;

            inventorySync.RequestHotbarSlot(slot, item);
            hotbar.Select(slot);
        }

        // The item under the pointer, else the chosen one (the slots show no names).
        private void ShowDetails()
        {
            int item = hoveredItem != Items.None && !dragging && inventorySync.Inventory.Has(hoveredItem) ? hoveredItem : selectedItem;
            details.style.visibility = item == Items.None ? Visibility.Hidden : Visibility.Visible;
            if (item == Items.None) return;

            detailsIcon.style.backgroundImage = new StyleBackground(ItemCatalog.IconOf(item, ecosystemSync));
            detailsName.text = ItemCatalog.NameOf(item, ecosystemSync);
            detailsKind.text = ItemCatalog.KindName(item);
            detailsDescription.text = ItemCatalog.DescriptionOf(item, ecosystemSync);
            detailsCount.text = inventorySync.Inventory.Count(item).ToString();
            int slot = inventorySync.Hotbar.SlotOf(item);
            detailsSlot.text = slot >= 0 ? Loc.T("Hotbar slot {0}", SlotLabel(slot)) : "";
            detailsSlot.style.display = slot >= 0 ? DisplayStyle.Flex : DisplayStyle.None;

            float weight = CarryLimits.CountsAsBlock(item) ? 0f : ecosystemSync.ItemWeights.Of(item);
            detailsWeightLine.style.display = weight > 0f ? DisplayStyle.Flex : DisplayStyle.None;
            detailsWeight.text = Loc.T("{0} kg each", Loc.Number(weight, "0.###"));

            Nutrition food = ecosystemSync.FoodOf(item);
            detailsFood.style.display = food.IsFood ? DisplayStyle.Flex : DisplayStyle.None;
            if (!food.IsFood) return;

            detailsCalories.text = $"{food.Calories:0} kcal";
            for (int i = 0; i < Nutrition.NutrientCount; i++)
            {
                float points = food.Nutrient(i);
                detailsNutrients[i].text = points.ToString("0");
                detailsNutrientBars[i].style.width = Length.Percent(Mathf.Clamp01(points / FullNutrient) * 100f);
            }
        }

        private static string SlotLabel(int slot) => ((slot + 1) % Hotbar.SlotCount).ToString();

        private void ShowSpeciality()
        {
            Speciality speciality = inventorySync.Speciality;
            bool chosen = speciality != Speciality.None;
            specialityName.text = chosen ? Loc.Name(Specialities.NameOf(speciality)) : Loc.T("No speciality yet");
            specialityChoose.style.display = chosen ? DisplayStyle.None : DisplayStyle.Flex;
        }
    }
}

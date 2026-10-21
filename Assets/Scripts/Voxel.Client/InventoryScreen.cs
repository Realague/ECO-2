using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;
using Voxel.Core;

namespace Voxel.Client
{
    // The inventory (Assets/UI/Inventory.uxml), opened and closed with Tab, closed with Escape:
    // categories, a grid of the items the player owns (searchable, sorted by kind then name), the
    // chosen item's details (what it is, how many, its hotbar slot, a food's nutrition) and the
    // speciality (chosen once, here). Items go into the hotbar, which stays shown below, by dragging
    // them onto a slot, by clicking a slot with an item chosen, or with the digits 1-0; a right click
    // empties a slot. A right click on a food in the grid eats one. The Map tab opens the full-screen map, whose Inventory tab comes back here.
    // While it is open the mouse is free and the player stands still (HoldsInput).
    public class InventoryScreen : MonoBehaviour
    {
        private const string SelectedItemClass = "item-slot--selected";
        private const string SelectedCategoryClass = "category--selected";
        private const float DragThreshold = 8f;

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
        private VisualElement grid;
        private Label total;
        private Label empty;
        private TextField search;
        private VisualElement details;
        private VisualElement detailsIcon;
        private Label detailsName;
        private Label detailsKind;
        private Label detailsDescription;
        private Label detailsCount;
        private Label detailsSlot;
        private VisualElement detailsFood;
        private Label detailsCalories;
        private readonly Label[] detailsNutrients = new Label[Nutrition.NutrientCount];
        private Label specialityName;
        private Button specialityChoose;
        private VisualElement ghost;

        // Categories down the left: null is "All".
        private readonly List<Button> categoryButtons = new List<Button>();
        private readonly List<Label> categoryCounts = new List<Label>();
        private readonly List<ItemCategory?> categoryKinds = new List<ItemCategory?>();
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
            var gridView = root.Q<ScrollView>("item-grid");
            grid = gridView;

            // Items flow in rows that wrap, inside the scrolled content.
            gridView.contentContainer.style.flexDirection = FlexDirection.Row;
            gridView.contentContainer.style.flexWrap = Wrap.Wrap;
            total = root.Q<Label>("item-total");
            empty = root.Q<Label>("item-empty");
            search = root.Q<TextField>("item-search");
            details = root.Q("details");
            detailsIcon = root.Q("details-icon");
            detailsName = root.Q<Label>("details-name");
            detailsKind = root.Q<Label>("details-kind");
            detailsDescription = root.Q<Label>("details-description");
            detailsCount = root.Q<Label>("details-count");
            detailsSlot = root.Q<Label>("details-slot");
            detailsFood = root.Q("details-food");
            detailsCalories = root.Q<Label>("details-calories");
            for (int i = 0; i < detailsNutrients.Length; i++)
            {
                detailsNutrients[i] = root.Q<Label>($"details-nutrient-{i}");
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
            Connect(root.Q<Button>("tab-map"), OpenMap);
            Connect(specialityChoose, () => inventorySync.RequestSpeciality(Specialities.Choices[0]));

            search.RegisterValueChangedCallback(_ => shownSearch = null);
            hotbar.SlotClicked += OnHotbarSlotClicked;

            screen.style.display = DisplayStyle.None;
            if (Application.isBatchMode) enabled = false;
        }

        private void OnDestroy()
        {
            if (hotbar != null) hotbar.SlotClicked -= OnHotbarSlotClicked;
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
            else if (keyboard != null && !CommandConsole.HoldsInput && !mapScreen.HoldsInput)
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

        private void OpenMap()
        {
            Close(false);
            mapScreen.Open();
        }

        private void AddCategory(ScrollView list, ItemCategory? kind)
        {
            var button = new Button { focusable = false };
            button.AddToClassList("category");
            var name = new Label(kind.HasValue ? ItemCatalog.CategoryName(kind.Value) : "All");
            name.AddToClassList("category-name");
            var count = new Label();
            count.AddToClassList("category-count");
            button.Add(name);
            button.Add(count);
            button.clicked += () =>
            {
                category = kind;
                shownSearch = null;
            };

            list.Add(button);
            categoryButtons.Add(button);
            categoryCounts.Add(count);
            categoryKinds.Add(kind);
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
                int count = 0;
                foreach (KeyValuePair<int, int> entry in shownEntries)
                {
                    if (!categoryKinds[c].HasValue || ItemCatalog.CategoryOf(entry.Key) == categoryKinds[c].Value) count++;
                }

                categoryCounts[c].text = count.ToString();
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

            grid.Clear();
            itemSlots.Clear();
            foreach (int item in sorted)
            {
                VisualElement slot = MakeItemSlot(item, inventory.Count(item));
                grid.Add(slot);
                itemSlots[item] = slot;
            }

            if (!inventory.Has(selectedItem)) selectedItem = sorted.Count > 0 ? sorted[0] : Items.None;
            if (itemSlots.TryGetValue(selectedItem, out VisualElement selectedSlot)) selectedSlot.AddToClassList(SelectedItemClass);

            int owned = 0;
            foreach (KeyValuePair<int, int> entry in shownEntries) owned += entry.Value;
            total.text = shownEntries.Count == 0 ? "" : $"{shownEntries.Count} kinds, {owned} in all";
            empty.style.display = sorted.Count == 0 ? DisplayStyle.Flex : DisplayStyle.None;
            empty.text = shownEntries.Count == 0
                ? "Nothing here yet: break blocks, fell trees and harvest ripe crops."
                : "No item matches.";
        }

        private VisualElement MakeItemSlot(int item, int count)
        {
            var slot = new VisualElement { userData = item };
            slot.AddToClassList("item-slot");
            var icon = new VisualElement { pickingMode = PickingMode.Ignore };
            icon.AddToClassList("item-icon");
            icon.style.backgroundImage = new StyleBackground(ItemCatalog.IconOf(item, ecosystemSync));
            var name = new Label(ItemCatalog.NameOf(item, ecosystemSync)) { pickingMode = PickingMode.Ignore };
            name.AddToClassList("item-name");
            var amount = new Label(count.ToString()) { pickingMode = PickingMode.Ignore };
            amount.AddToClassList("item-count");
            slot.Add(icon);
            slot.Add(name);
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

        private void ShowDetails()
        {
            int item = selectedItem;
            details.style.visibility = item == Items.None ? Visibility.Hidden : Visibility.Visible;
            if (item == Items.None) return;

            detailsIcon.style.backgroundImage = new StyleBackground(ItemCatalog.IconOf(item, ecosystemSync));
            detailsName.text = ItemCatalog.NameOf(item, ecosystemSync);
            detailsKind.text = ItemCatalog.KindName(item);
            detailsDescription.text = ItemCatalog.DescriptionOf(item, ecosystemSync);
            detailsCount.text = inventorySync.Inventory.Count(item).ToString();
            int slot = inventorySync.Hotbar.SlotOf(item);
            detailsSlot.text = slot >= 0 ? $"slot {SlotLabel(slot)}" : "not in it";

            Nutrition food = ecosystemSync.FoodOf(item);
            detailsFood.style.display = food.IsFood ? DisplayStyle.Flex : DisplayStyle.None;
            if (!food.IsFood) return;

            detailsCalories.text = $"{food.Calories:0} kcal";
            for (int i = 0; i < Nutrition.NutrientCount; i++)
            {
                detailsNutrients[i].text = food.Nutrient(i).ToString("0");
            }
        }

        private static string SlotLabel(int slot) => ((slot + 1) % Hotbar.SlotCount).ToString();

        private void ShowSpeciality()
        {
            Speciality speciality = inventorySync.Speciality;
            bool chosen = speciality != Speciality.None;
            specialityName.text = chosen ? Specialities.NameOf(speciality) : "No speciality yet";
            specialityChoose.style.display = chosen ? DisplayStyle.None : DisplayStyle.Flex;
        }
    }
}

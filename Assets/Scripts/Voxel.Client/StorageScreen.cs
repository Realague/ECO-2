using System.Collections.Generic;
using System.Globalization;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;
using Voxel.Core;

namespace Voxel.Client
{
    // The window of an opened chest or stockpile (Assets/UI/Storage.uxml), in the inventory's look:
    // the player's bag on the left (what the storage does not take greyed), the storage on the right
    // (a chest's items and its weight out of 150 kg, or a stockpile's nine piles out of 27 each).
    // Items move by dragging them to the other side (the whole stack), Shift + click (the whole stack)
    // or a right click (one); the server decides (StorageSync). Its owner also sees an Access tab, to
    // allow the players with a name to open it, and can pick it up once empty. It opens when the server
    // lets the player open what they right clicked, and closes with Escape or Tab, when the player
    // walks away (the server closes it) or loses access. While it is open the mouse is free and the
    // player stands still (HoldsInput).
    public class StorageScreen : MonoBehaviour
    {
        private const float DragThreshold = 8f;
        private const int MinimumRows = 4;
        private const string SelectedTabClass = "inventory-tab--selected";

        [SerializeField] private UIDocument document;
        [SerializeField] private NetworkManager networkManager;
        [SerializeField] private WorldSync worldSync;
        [SerializeField] private InventorySync inventorySync;
        [SerializeField] private StorageSync storageSync;
        [SerializeField] private EcosystemSync ecosystemSync;
        [SerializeField] private AppearanceMenu appearanceMenu;
        [SerializeField] private HotbarHud hotbar;

        private static StorageScreen instance;

        private VisualElement screen;
        private VisualElement storagePage;
        private VisualElement accessPage;
        private Button storageTab;
        private Button accessTab;
        private Label storageTabText;
        private Label owner;
        private Label carryBlocks;
        private Label carryWeight;
        private Label bagTitle;
        private Label storeTitle;
        private Label storeFill;
        private VisualElement storeFillIcon;
        private VisualElement bagWell;
        private VisualElement storeWell;
        private ScrollView storeGridView;
        private VisualElement pileGrid;
        private Button pickup;
        private ScrollView accessList;
        private Label accessEmpty;
        private VisualElement ghost;
        private ItemGrid bagGrid;
        private ItemGrid storeGrid;
        private readonly VisualElement[] piles = new VisualElement[StorageRules.PileCount];
        private readonly VisualElement[] pileIcons = new VisualElement[StorageRules.PileCount];
        private readonly Label[] pileCounts = new Label[StorageRules.PileCount];
        private int hoveredPile = -1;

        private int pendingId;
        private int openId;
        private bool showingAccess;
        private int closedFrame = -1;

        // A press on an item or pile that may become a drag: from the bag (deposit) or the storage.
        private bool pressing;
        private bool fromBag;
        private int pressedItemOrPile;
        private VisualElement pressedSlot;
        private Vector2 pressedAt;
        private bool dragging;

        public static bool IsOpen => instance != null && instance.openId != 0;

        // Also true on the frame it closes, so that its key does nothing else.
        public static bool HoldsInput => instance != null && (instance.openId != 0 || Time.frameCount == instance.closedFrame);

        private void Awake()
        {
            instance = this;
        }

        private void Start()
        {
            VisualElement root = document.rootVisualElement;
            screen = root.Q("screen");
            storagePage = root.Q("storage-page");
            accessPage = root.Q("access-page");
            storageTab = root.Q<Button>("tab-storage");
            accessTab = root.Q<Button>("tab-access");
            storageTabText = root.Q<Label>("tab-storage-text");
            owner = root.Q<Label>("storage-owner");
            carryBlocks = root.Q<Label>("carry-blocks");
            carryWeight = root.Q<Label>("carry-weight");
            bagTitle = root.Q<Label>("bag-title");
            storeTitle = root.Q<Label>("store-title");
            storeFill = root.Q<Label>("store-fill");
            storeFillIcon = root.Q("store-fill-icon");
            bagWell = root.Q("bag-well");
            storeWell = root.Q("store-well");
            storeGridView = root.Q<ScrollView>("store-grid");
            pileGrid = root.Q("pile-grid");
            pickup = root.Q<Button>("pickup");
            accessList = root.Q<ScrollView>("access-list");
            accessEmpty = root.Q<Label>("access-empty");
            ghost = root.Q("drag-ghost");

            bagGrid = new ItemGrid(root.Q<ScrollView>("bag-grid"), ecosystemSync, MinimumRows);
            storeGrid = new ItemGrid(storeGridView, ecosystemSync, MinimumRows);
            bagGrid.Pressed += (item, slot, e) => Press(true, item, slot, e);
            storeGrid.Pressed += (item, slot, e) => Press(false, item, slot, e);
            bagGrid.Moved += OnPointerMove;
            storeGrid.Moved += OnPointerMove;
            bagGrid.Released += OnPointerUp;
            storeGrid.Released += OnPointerUp;
            BuildPiles();

            Connect(storageTab, () => ShowTab(false));
            Connect(accessTab, () => ShowTab(true));
            Connect(pickup, () => storageSync.RequestPickup());

            storageSync.OpenedChanged += OnOpenedChanged;
            storageSync.OpenedClosed += OnOpenedClosed;
            storageSync.Refused += OnRefused;

            screen.style.display = DisplayStyle.None;
            if (Application.isBatchMode) enabled = false;
        }

        private void OnDestroy()
        {
            if (instance == this) instance = null;
            if (storageSync == null) return;

            storageSync.OpenedChanged -= OnOpenedChanged;
            storageSync.OpenedClosed -= OnOpenedClosed;
            storageSync.Refused -= OnRefused;
        }

        // Asks to open a storage the player right clicked; the window shows once the server agrees.
        public void Open(int storageId)
        {
            if (openId != 0) return;

            pendingId = storageId;
            storageSync.RequestOpen(storageId);
        }

        private void Update()
        {
            bool playing = networkManager.IsListening && worldSync.World != null && !appearanceMenu.IsOpen
                && networkManager.SpawnManager?.GetLocalPlayerObject() != null;
            if (!playing)
            {
                if (openId != 0) Close(false);
                return;
            }

            if (openId == 0) return;

            Keyboard keyboard = Keyboard.current;
            if (keyboard != null && (keyboard.escapeKey.wasPressedThisFrame || keyboard.tabKey.wasPressedThisFrame))
            {
                Close(true);
                return;
            }

            Refresh();
        }

        private void OnOpenedChanged()
        {
            if (openId == 0)
            {
                if (storageSync.OpenedId == 0 || storageSync.OpenedId != pendingId) return;

                openId = pendingId;
                pendingId = 0;
                showingAccess = false;
                screen.style.display = DisplayStyle.Flex;
                UnityEngine.Cursor.lockState = CursorLockMode.None;
                bagGrid.Invalidate();
                storeGrid.Invalidate();
            }

            ShowAccess();
            ShowTab(showingAccess && storageSync.OpenedIsOwner);
        }

        private void OnOpenedClosed()
        {
            if (openId != 0) Close(true, false);
        }

        private void OnRefused(StorageRefusal refusal)
        {
            pendingId = 0;
            hotbar.ShowMessage(StorageSync.TextOf(refusal));
        }

        // relock: back to the game, the mouse steers the player again; tell: the server is told.
        private void Close(bool relock, bool tell = true)
        {
            openId = 0;
            pendingId = 0;
            closedFrame = Time.frameCount;
            EndDrag();
            screen.style.display = DisplayStyle.None;
            if (tell) storageSync.RequestClose();
            if (relock) UnityEngine.Cursor.lockState = CursorLockMode.Locked;
        }

        private void ShowTab(bool access)
        {
            showingAccess = access;
            storageTab.EnableInClassList(SelectedTabClass, !access);
            accessTab.EnableInClassList(SelectedTabClass, access);
            accessTab.style.display = storageSync.OpenedIsOwner ? DisplayStyle.Flex : DisplayStyle.None;
            storagePage.EnableInClassList("hidden", access);
            accessPage.EnableInClassList("hidden", !access);
        }

        // Both sides, as they are now (the grids rebuild only when what they show changed).
        private void Refresh()
        {
            bool chest = storageSync.OpenedKind == StorageKind.Chest;
            Inventory bag = inventorySync.Inventory;
            bagGrid.Show(bag.Entries, item => chest ? !StorageRules.AcceptsInChest(item) : !StorageRules.AcceptsInStockpile(item));
            ShowCarry(bag);

            storageTabText.text = chest ? "Chest" : "Stockpile";
            storeGridView.style.display = chest ? DisplayStyle.Flex : DisplayStyle.None;
            pileGrid.style.display = chest ? DisplayStyle.None : DisplayStyle.Flex;
            storeFillIcon.EnableInClassList("icon--scales", chest);
            storeFillIcon.EnableInClassList("icon--backpack", !chest);
            if (chest)
            {
                storeGrid.Show(storageSync.OpenedContents.Entries, _ => false);
                float weight = StorageRules.ChestWeight(storageSync.OpenedContents, ecosystemSync.ItemWeights);
                storeFill.text = $"{weight.ToString("0.0", CultureInfo.InvariantCulture)} / {StorageRules.ChestMaxWeight:0} kg";
                ShowShare(storeFill, weight / StorageRules.ChestMaxWeight);
            }
            else
            {
                ShowPiles();
                int total = storageSync.OpenedPiles.Total;
                storeFill.text = $"{total} / {StorageRules.StockpileCapacity}";
                ShowShare(storeFill, (float)total / StorageRules.StockpileCapacity);
            }

            // The item under the pointer is named in its side's title (the slots show no names).
            bagTitle.text = bagGrid.HoveredItem != Items.None ? ItemCatalog.NameOf(bagGrid.HoveredItem, ecosystemSync) : "Your bag";
            int storeItem = chest ? storeGrid.HoveredItem : hoveredPile >= 0 ? storageSync.OpenedPiles.ItemAt(hoveredPile) : Items.None;
            storeTitle.text = storeItem != Items.None ? ItemCatalog.NameOf(storeItem, ecosystemSync) : chest ? "In the chest" : "On the stockpile";

            storageSync.TryDescribe(openId, out _, out string ownerName, out bool yours, out _);
            owner.text = yours ? "Yours" : ownerName.Length > 0 ? $"{ownerName}'s" : "";
            bool empty = chest ? storageSync.OpenedContents.DistinctCount == 0 : storageSync.OpenedPiles.IsEmpty;
            pickup.style.display = storageSync.OpenedIsOwner ? DisplayStyle.Flex : DisplayStyle.None;
            pickup.SetEnabled(empty);
        }

        private void ShowCarry(Inventory bag)
        {
            int blocks = CarryLimits.BlockCount(bag);
            float weight = CarryLimits.Weight(bag, ecosystemSync.ItemWeights);
            carryBlocks.text = $"{blocks} / {CarryLimits.MaxBlocks}";
            carryWeight.text = $"{weight.ToString("0.0", CultureInfo.InvariantCulture)} / {CarryLimits.MaxWeight:0} kg";
            ShowShare(carryBlocks, (float)blocks / CarryLimits.MaxBlocks);
            ShowShare(carryWeight, weight / CarryLimits.MaxWeight);
        }

        private static void ShowShare(Label label, float share)
        {
            bool full = share >= 0.9999f;
            label.EnableInClassList("carry-count-text--high", share >= CarryHud.HighShare && !full);
            label.EnableInClassList("carry-count-text--full", full);
        }

        // The nine piles, made once, in the order they stand (StorageRules.PileCell: x, then z).
        private void BuildPiles()
        {
            for (int pile = 0; pile < piles.Length; pile++)
            {
                int index = pile;
                var slot = new VisualElement();
                slot.AddToClassList("item-slot");
                slot.AddToClassList("pile-slot");
                var icon = new VisualElement { pickingMode = PickingMode.Ignore };
                icon.AddToClassList("item-icon");
                var count = new Label { pickingMode = PickingMode.Ignore };
                count.AddToClassList("item-count");
                count.AddToClassList("pile-count");
                slot.Add(icon);
                slot.Add(count);
                slot.RegisterCallback<PointerEnterEvent>(_ => hoveredPile = index);
                slot.RegisterCallback<PointerLeaveEvent>(_ =>
                {
                    if (hoveredPile == index) hoveredPile = -1;
                });
                slot.RegisterCallback<PointerDownEvent>(e =>
                {
                    if (storageSync.OpenedPiles.CountAt(index) > 0) Press(false, index, slot, e);
                });
                slot.RegisterCallback<PointerMoveEvent>(OnPointerMove);
                slot.RegisterCallback<PointerUpEvent>(OnPointerUp);

                // A row per z, three piles each, as they stand on the ground: never wrapped by the width.
                if (pile % StorageRules.StockpileSide == 0)
                {
                    var row = new VisualElement { pickingMode = PickingMode.Ignore };
                    row.AddToClassList("pile-row");
                    pileGrid.Add(row);
                }

                pileGrid[pileGrid.childCount - 1].Add(slot);
                piles[pile] = slot;
                pileIcons[pile] = icon;
                pileCounts[pile] = count;
            }
        }

        private void ShowPiles()
        {
            StockpilePiles stock = storageSync.OpenedPiles;
            for (int pile = 0; pile < piles.Length; pile++)
            {
                int item = stock.ItemAt(pile);
                int count = stock.CountAt(pile);
                pileIcons[pile].style.backgroundImage = count > 0 ? new StyleBackground(ItemCatalog.IconOf(item, ecosystemSync)) : new StyleBackground();
                pileCounts[pile].text = count > 0 ? $"{count} / {StorageRules.PileSize}" : "";
                piles[pile].EnableInClassList("item-slot--empty", count == 0);
            }
        }

        // The owner's list: each named player connected, then those allowed who are not, with a button.
        private void ShowAccess()
        {
            accessList.Clear();
            IReadOnlyList<StorageSync.AccessEntry> entries = storageSync.OpenedAccess;
            accessEmpty.style.display = entries.Count == 0 ? DisplayStyle.Flex : DisplayStyle.None;
            foreach (StorageSync.AccessEntry entry in entries)
            {
                var row = new VisualElement();
                row.AddToClassList("access-row");
                var dot = new VisualElement();
                dot.AddToClassList("access-dot");
                dot.EnableInClassList("access-dot--online", entry.Online);
                var name = new Label(entry.Name);
                name.AddToClassList("access-name");
                var state = new Label(entry.Allowed ? "Allowed" : entry.Online ? "Not allowed" : "Allowed, away");
                state.AddToClassList("access-state");
                var toggle = new Button { focusable = false };
                toggle.AddToClassList("button");
                toggle.AddToClassList(entry.Allowed ? "button--danger" : "button--primary");
                toggle.AddToClassList("access-toggle");
                var text = new Label(entry.Allowed ? "Remove" : "Allow");
                text.AddToClassList("button-text");
                toggle.Add(text);
                string playerName = entry.Name;
                bool allow = !entry.Allowed;
                toggle.clicked += () => storageSync.RequestAccess(playerName, allow);
                row.Add(dot);
                row.Add(name);
                row.Add(state);
                row.Add(toggle);
                accessList.Add(row);
            }
        }

        // ---- Moving items ----

        private void Press(bool bag, int itemOrPile, VisualElement slot, PointerDownEvent e)
        {
            if (e.button == 1)
            {
                Move(bag, itemOrPile, false);
                return;
            }

            if (e.button != 0) return;

            if (e.shiftKey)
            {
                Move(bag, itemOrPile, true);
                return;
            }

            pressing = true;
            fromBag = bag;
            pressedItemOrPile = itemOrPile;
            pressedSlot = slot;
            pressedAt = e.position;
            slot.CapturePointer(e.pointerId);
        }

        // Moves the whole stack, or one, from one side to the other.
        private void Move(bool bag, int itemOrPile, bool all)
        {
            if (bag)
            {
                int count = all ? inventorySync.Inventory.Count(itemOrPile) : 1;
                storageSync.RequestMove(true, itemOrPile, count);
                return;
            }

            bool chest = storageSync.OpenedKind == StorageKind.Chest;
            int held = chest ? storageSync.OpenedContents.Count(itemOrPile) : storageSync.OpenedPiles.CountAt(itemOrPile);
            storageSync.RequestMove(false, itemOrPile, all ? held : 1);
        }

        private void OnPointerMove(PointerMoveEvent e)
        {
            if (!pressing) return;

            if (!dragging && Vector2.Distance(e.position, pressedAt) >= DragThreshold)
            {
                dragging = true;
                int item = fromBag || storageSync.OpenedKind == StorageKind.Chest ? pressedItemOrPile : storageSync.OpenedPiles.ItemAt(pressedItemOrPile);
                ghost.style.backgroundImage = new StyleBackground(ItemCatalog.IconOf(item, ecosystemSync));
                ghost.RemoveFromClassList("hidden");
                ghost.BringToFront();
            }

            if (!dragging) return;

            Vector2 local = screen.WorldToLocal(e.position);
            ghost.style.left = local.x - 36f;
            ghost.style.top = local.y - 36f;
        }

        // Dropped on the other side, the whole stack goes there.
        private void OnPointerUp(PointerUpEvent e)
        {
            if (!pressing) return;

            if (dragging)
            {
                VisualElement target = fromBag ? storeWell : bagWell;
                if (target.worldBound.Contains(e.position)) Move(fromBag, pressedItemOrPile, true);
            }

            if (pressedSlot != null && pressedSlot.HasPointerCapture(e.pointerId)) pressedSlot.ReleasePointer(e.pointerId);
            EndDrag();
        }

        private void EndDrag()
        {
            pressing = false;
            dragging = false;
            pressedSlot = null;
            ghost?.AddToClassList("hidden");
        }

        private static void Connect(Button button, System.Action action)
        {
            button.focusable = false;
            button.clicked += action;
        }
    }
}

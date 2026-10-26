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
    // the player's bag on the left (what the storage does not take greyed), the storage on the right:
    // what its pool holds (the chests or stockpiles of its claimed zone together, StoragePool), a
    // chest pool's items and their weight out of 150 kg a chest, or a stockpile pool's piles, nine a
    // stockpile, out of 27 each. Items move by dragging them to the other side (the whole stack),
    // Shift + click (the whole stack) or a right click (one); the server decides (StorageSync). Its
    // owner, or anyone while it is public, can pick it up (what it holds goes to the rest of its pool).
    // Who may open it follows the land (StorageSync.CanUse). It opens when the server lets the player
    // open what they right clicked, and closes with Escape or Tab, when the player walks away (the
    // server closes it) or loses access. While it is open the mouse is free and the player stands
    // still (HoldsInput). A campfire opens in the same window, its page (CampfirePage) in the storage's
    // place; wood moves from the bag to the fire the way items go into a chest.
    public class StorageScreen : MonoBehaviour
    {
        private const float DragThreshold = 8f;
        private const int MinimumRows = 4;

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
        private Button storageTab;
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
        private ScrollView pileGrid;
        private Button pickup;
        private VisualElement storeHalf;
        private VisualElement campfireHalf;
        private Button firePickup;
        private CampfirePage campfire;
        private VisualElement ghost;
        private ItemGrid bagGrid;
        private ItemGrid storeGrid;

        // The piles shown, nine a stockpile of the pool, numbered as StoragePool numbers them.
        private readonly List<VisualElement> piles = new List<VisualElement>();
        private readonly List<VisualElement> pileIcons = new List<VisualElement>();
        private readonly List<Label> pileCounts = new List<Label>();

        // How many piles each stockpile laid out has, in the pool's order.
        private readonly List<int> pileLayout = new List<int>();
        private static readonly StockpilePiles[] NoStockpiles = new StockpilePiles[0];
        private int hoveredPile = -1;

        private int pendingId;
        private int openId;
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
            storageTab = root.Q<Button>("tab-storage");
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
            pileGrid = root.Q<ScrollView>("pile-grid");
            pickup = root.Q<Button>("pickup");
            storeHalf = root.Q("store-half");
            campfireHalf = root.Q("campfire-half");
            firePickup = root.Q<Button>("fire-pickup");
            campfire = new CampfirePage(root, storageSync, inventorySync, ecosystemSync);
            ghost = root.Q("drag-ghost");

            bagGrid = new ItemGrid(root.Q<ScrollView>("bag-grid"), ecosystemSync, MinimumRows);
            storeGrid = new ItemGrid(storeGridView, ecosystemSync, MinimumRows);
            bagGrid.Pressed += (item, slot, e) => Press(true, item, slot, e);
            storeGrid.Pressed += (item, slot, e) => Press(false, item, slot, e);
            bagGrid.Moved += OnPointerMove;
            storeGrid.Moved += OnPointerMove;
            bagGrid.Released += OnPointerUp;
            storeGrid.Released += OnPointerUp;

            storageTab.focusable = false;
            Connect(pickup, () => storageSync.RequestPickup());
            Connect(firePickup, () => storageSync.RequestPickup());

            storageSync.OpenedChanged += OnOpenedChanged;
            storageSync.OpenedClosed += OnOpenedClosed;
            storageSync.Refused += OnRefused;
            Loc.Changed += campfire.Retranslate;

            screen.style.display = DisplayStyle.None;
            if (Application.isBatchMode) enabled = false;
        }

        private void OnDestroy()
        {
            if (instance == this) instance = null;
            if (campfire != null) Loc.Changed -= campfire.Retranslate;
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
                screen.style.display = DisplayStyle.Flex;
                UnityEngine.Cursor.lockState = CursorLockMode.None;
                bagGrid.Invalidate();
                storeGrid.Invalidate();
            }

            // A stockpile pool gained or lost a stockpile: its piles are laid out again.
            if (!SameLayout()) BuildPiles();
        }

        // Whether the piles shown are laid out for the open pool's stockpiles, each its own size.
        private bool SameLayout()
        {
            IReadOnlyList<StockpilePiles> stockpiles = StorageRules.IsStockpile(storageSync.OpenedKind) ? storageSync.OpenedStockpiles : NoStockpiles;
            if (stockpiles.Count != pileLayout.Count) return false;

            for (int i = 0; i < stockpiles.Count; i++)
            {
                if (stockpiles[i].Count != pileLayout[i]) return false;
            }

            return true;
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

        // Both sides, as they are now (the grids rebuild only when what they show changed).
        private void Refresh()
        {
            bool fire = storageSync.OpenedKind == StorageKind.Campfire;
            bool chest = storageSync.OpenedKind == StorageKind.Chest;
            Inventory bag = inventorySync.Inventory;
            bagGrid.Show(bag.Entries, item => fire ? !Items.IsWood(item) : chest ? !StorageRules.AcceptsInChest(item) : !StorageRules.AcceptsInStockpile(item));
            ShowCarry(bag);

            storageTabText.text = Loc.T(Items.NameOf(StorageRules.ItemOf(storageSync.OpenedKind)));
            storeHalf.style.display = fire ? DisplayStyle.None : DisplayStyle.Flex;
            campfireHalf.style.display = fire ? DisplayStyle.Flex : DisplayStyle.None;
            bagTitle.text = bagGrid.HoveredItem != Items.None ? ItemCatalog.NameOf(bagGrid.HoveredItem, ecosystemSync) : Loc.T("Your bag");
            ShowOwner();
            if (fire)
            {
                // Picked up with nothing queued; its wood goes back to the bag (the server checks).
                campfire.Refresh();
                firePickup.style.display = storageSync.OpenedIsOwner || storageSync.IsPublic(openId) ? DisplayStyle.Flex : DisplayStyle.None;
                firePickup.SetEnabled(storageSync.OpenedJobs.Count == 0);
                return;
            }

            storeGridView.style.display = chest ? DisplayStyle.Flex : DisplayStyle.None;
            pileGrid.style.display = chest ? DisplayStyle.None : DisplayStyle.Flex;
            storeFillIcon.EnableInClassList("icon--scales", chest);
            storeFillIcon.EnableInClassList("icon--backpack", !chest);
            int members = storageSync.OpenedMembers;
            if (chest)
            {
                storeGrid.Show(storageSync.OpenedContents.Entries, _ => false);
                float weight = StorageRules.ChestWeight(storageSync.OpenedContents, ecosystemSync.ItemWeights);
                float capacity = StorageRules.ChestMaxWeight * members;
                storeFill.text = $"{Loc.Number(weight, "0.0")} / {capacity:0} kg";
                ShowShare(storeFill, weight / capacity);
            }
            else
            {
                ShowPiles();
                int total = storageSync.OpenedPileTotal;
                int capacity = Mathf.Max(1, storageSync.OpenedPileCapacity);
                storeFill.text = $"{total} / {capacity}";
                ShowShare(storeFill, (float)total / capacity);
            }

            // The item under the pointer is named in its side's title (the slots show no names); else
            // what the side is, with how many chests or stockpiles share it.
            int storeItem = chest ? storeGrid.HoveredItem : hoveredPile >= 0 ? storageSync.OpenedPileItem(hoveredPile) : Items.None;
            string side = members > 1
                ? Loc.T(chest ? "In the chests ({0})" : "On the stockpiles ({0})", members)
                : Loc.T(chest ? "In the chest" : "On the stockpile");
            storeTitle.text = storeItem != Items.None ? ItemCatalog.NameOf(storeItem, ecosystemSync) : side;

            // Picked up empty, or with what it holds poured into the rest of its pool (the server checks).
            bool empty = chest ? storageSync.OpenedContents.DistinctCount == 0 : storageSync.OpenedPileTotal == 0;
            pickup.style.display = storageSync.OpenedIsOwner || storageSync.IsPublic(openId) ? DisplayStyle.Flex : DisplayStyle.None;
            pickup.SetEnabled(empty || members > 1);
        }

        // Whose it is (the land's owner, on claimed land), and whether anyone may use it.
        private void ShowOwner()
        {
            storageSync.TryDescribe(openId, out _, out string ownerName, out bool yours, out _);
            owner.text = yours ? Loc.T("Yours") : ownerName.Length > 0 ? Loc.T("{0}'s", ownerName) : "";
            if (storageSync.IsPublic(openId)) owner.text = Loc.T("{0} · public (unclaimed land)", owner.text);
        }

        private void ShowCarry(Inventory bag)
        {
            int blocks = CarryLimits.BlockCount(bag);
            float weight = CarryLimits.Weight(bag, ecosystemSync.ItemWeights);
            carryBlocks.text = $"{blocks} / {CarryLimits.MaxBlocks}";
            carryWeight.text = $"{Loc.Number(weight, "0.0")} / {CarryLimits.MaxWeight:0} kg";
            ShowShare(carryBlocks, (float)blocks / CarryLimits.MaxBlocks);
            ShowShare(carryWeight, weight / CarryLimits.MaxWeight);
        }

        private static void ShowShare(Label label, float share)
        {
            bool full = share >= 0.9999f;
            label.EnableInClassList("carry-count-text--high", share >= CarryHud.HighShare && !full);
            label.EnableInClassList("carry-count-text--full", full);
        }

        // Each stockpile of the pool a block of its piles (nine in three rows, or four in two for a
        // small one) in the order they stand (StorageRules.PileCell: x, then z), the blocks one under another.
        private void BuildPiles()
        {
            pileGrid.Clear();
            piles.Clear();
            pileIcons.Clear();
            pileCounts.Clear();
            pileLayout.Clear();
            hoveredPile = -1;
            IReadOnlyList<StockpilePiles> stockpiles = StorageRules.IsStockpile(storageSync.OpenedKind) ? storageSync.OpenedStockpiles : NoStockpiles;
            foreach (StockpilePiles stockpile in stockpiles)
            {
                pileLayout.Add(stockpile.Count);
                var block = new VisualElement { pickingMode = PickingMode.Ignore };
                block.AddToClassList("pile-block");
                pileGrid.Add(block);

                // Stockpiles are square: their side is the root of their piles.
                int side = Mathf.RoundToInt(Mathf.Sqrt(stockpile.Count));
                for (int i = 0; i < stockpile.Count; i++)
                {
                    // A row per z, a pile per x, as they stand on the ground: never wrapped by the width.
                    if (i % side == 0)
                    {
                        var row = new VisualElement { pickingMode = PickingMode.Ignore };
                        row.AddToClassList("pile-row");
                        block.Add(row);
                    }

                    block[block.childCount - 1].Add(AddPile(piles.Count));
                }
            }
        }

        // One pile's slot, numbered across the pool.
        private VisualElement AddPile(int index)
        {
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
                if (storageSync.OpenedPileCountAt(index) > 0) Press(false, index, slot, e);
            });
            slot.RegisterCallback<PointerMoveEvent>(OnPointerMove);
            slot.RegisterCallback<PointerUpEvent>(OnPointerUp);

            piles.Add(slot);
            pileIcons.Add(icon);
            pileCounts.Add(count);
            return slot;
        }

        private void ShowPiles()
        {
            for (int pile = 0; pile < piles.Count; pile++)
            {
                int item = storageSync.OpenedPileItem(pile);
                int count = storageSync.OpenedPileCountAt(pile);
                pileIcons[pile].style.backgroundImage = count > 0 ? new StyleBackground(ItemCatalog.IconOf(item, ecosystemSync)) : new StyleBackground();
                pileCounts[pile].text = count > 0 ? $"{count} / {storageSync.OpenedPileSizeAt(pile)}" : "";
                piles[pile].EnableInClassList("item-slot--empty", count == 0);
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
                if (storageSync.OpenedKind == StorageKind.Campfire) storageSync.RequestFuel(itemOrPile, count);
                else storageSync.RequestMove(true, itemOrPile, count);
                return;
            }

            if (storageSync.OpenedKind == StorageKind.Campfire) return;

            bool chest = storageSync.OpenedKind == StorageKind.Chest;
            int held = chest ? storageSync.OpenedContents.Count(itemOrPile) : storageSync.OpenedPileCountAt(itemOrPile);
            storageSync.RequestMove(false, itemOrPile, all ? held : 1);
        }

        private void OnPointerMove(PointerMoveEvent e)
        {
            if (!pressing) return;

            if (!dragging && Vector2.Distance(e.position, pressedAt) >= DragThreshold)
            {
                dragging = true;
                int item = fromBag || storageSync.OpenedKind == StorageKind.Chest ? pressedItemOrPile : storageSync.OpenedPileItem(pressedItemOrPile);
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
                VisualElement target = !fromBag ? bagWell : storageSync.OpenedKind == StorageKind.Campfire ? campfireHalf : storeWell;
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

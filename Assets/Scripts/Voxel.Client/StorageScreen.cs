using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;
using Voxel.Core;

namespace Voxel.Client
{
    // The window of an opened chest, stockpile or campfire (Assets/UI/Storage.uxml), in the inventory's
    // look. For a chest or stockpile: the player's bag on the left (what the storage does not take
    // greyed), the storage on the right, each on its own card: what its pool holds (the chests or
    // stockpiles of its claimed zone together, StoragePool), merged or each storage on its own card
    // (PoolView), with a chest pool's weight out of 150 kg a chest or a stockpile pool's fill. For a
    // campfire, three tabs: its cooking (CampfirePage), the bag beside its linked chests, and the bag
    // beside its wood. Items move by dragging them to the other side (the whole stack; dropped on a
    // storage's card, into that one), Shift + click (the whole stack) or a right click (one); the server
    // decides (StorageSync). Its owner, or anyone while it is public, can pick it up (what it holds goes
    // to the rest of its pool; a campfire, with nothing queued, its wood to the bag). Who may open it
    // follows the land (StorageSync.CanUse). It opens when the server lets the player open what they
    // right clicked, and closes with Escape or Tab, when the player walks away (the server closes it)
    // or loses access. While it is open the mouse is free and the player stands still (HoldsInput).
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
        [SerializeField] private HungerSync hungerSync;
        [SerializeField] private AppearanceMenu appearanceMenu;
        [SerializeField] private HotbarHud hotbar;

        private static StorageScreen instance;

        // What the window shows: a chest pool, a stockpile pool, or one of a campfire's three tabs.
        private enum Mode
        {
            Chests,
            Stockpiles,
            Cooking,
            Linked,
            Fuel
        }

        private VisualElement screen;
        private Button storageTab;
        private Label storageTabText;
        private Button cookingTab;
        private Button linkedTab;
        private Button fuelTab;
        private Label owner;
        private Button pickup;
        private VisualElement storagePage;
        private VisualElement cookingPage;
        private Label carryBlocks;
        private Label carryWeight;
        private Label bagTitle;
        private Label storeTitle;
        private VisualElement storeIcon;
        private Label storeFill;
        private VisualElement storeFillIcon;
        private VisualElement storeBurn;
        private VisualElement storeBurnFill;
        private Label storeEmpty;
        private VisualElement storeHalf;
        private VisualElement fuelInfo;
        private Label fuelTotal;
        private Label fuelHovered;
        private VisualElement bagWell;
        private VisualElement storeWell;
        private CampfirePage campfire;
        private PoolView pool;
        private VisualElement ghost;
        private ItemGrid bagGrid;

        // The campfire tab chosen (Cooking when a campfire opens).
        private Mode fireTab = Mode.Cooking;

        private int pendingId;
        private int openId;
        private int closedFrame = -1;

        // A press on an item or pile that may become a drag: from the bag (deposit) or the storage
        // (which storage of the pool, 0 for any, and whether it is a stockpile's item from every pile).
        private bool pressing;
        private bool fromBag;
        private int pressedItemOrPile;
        private int pressedMember;
        private bool pressedByItem;
        private VisualElement pressedSlot;
        private Vector2 pressedAt;
        private bool dragging;

        public static bool IsOpen => instance != null && instance.openId != 0;

        // Also true on the frame it closes, so that its key does nothing else.
        public static bool HoldsInput => instance != null && (instance.openId != 0 || Time.frameCount == instance.closedFrame);

        private Mode CurrentMode => storageSync.OpenedKind == StorageKind.Campfire ? fireTab
            : storageSync.OpenedKind == StorageKind.Chest ? Mode.Chests : Mode.Stockpiles;

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
            cookingTab = root.Q<Button>("tab-cooking");
            linkedTab = root.Q<Button>("tab-fire-inventory");
            fuelTab = root.Q<Button>("tab-fuel");
            owner = root.Q<Label>("storage-owner");
            pickup = root.Q<Button>("pickup");
            storagePage = root.Q("storage-page");
            cookingPage = root.Q("cooking-page");
            carryBlocks = root.Q<Label>("carry-blocks");
            carryWeight = root.Q<Label>("carry-weight");
            bagTitle = root.Q<Label>("bag-title");
            storeTitle = root.Q<Label>("store-title");
            storeIcon = root.Q("store-icon");
            storeFill = root.Q<Label>("store-fill");
            storeFillIcon = root.Q("store-fill-icon");
            storeBurn = root.Q("store-burn");
            storeBurnFill = root.Q("store-burn-fill");
            storeEmpty = root.Q<Label>("store-empty");
            storeHalf = root.Q("store-half");
            fuelInfo = root.Q("fuel-info");
            fuelTotal = root.Q<Label>("fuel-total");
            fuelHovered = root.Q<Label>("fuel-hovered");
            bagWell = root.Q("bag-well");
            storeWell = root.Q("store-well");
            campfire = new CampfirePage(root, storageSync, inventorySync, hungerSync, ecosystemSync);
            pool = new PoolView(root, storageSync, ecosystemSync, MinimumRows);
            ghost = root.Q("drag-ghost");

            bagGrid = new ItemGrid(root.Q<ScrollView>("bag-grid"), ecosystemSync, MinimumRows);
            bagGrid.Pressed += (item, slot, e) => Press(true, item, 0, false, slot, e);
            bagGrid.Moved += OnPointerMove;
            bagGrid.Released += OnPointerUp;
            pool.Pressed += (itemOrPile, member, byItem, slot, e) => Press(false, itemOrPile, member, byItem, slot, e);
            pool.Moved += OnPointerMove;
            pool.Released += OnPointerUp;

            storageTab.focusable = false;
            Connect(cookingTab, () => ShowFireTab(Mode.Cooking));
            Connect(linkedTab, () => ShowFireTab(Mode.Linked));
            Connect(fuelTab, () => ShowFireTab(Mode.Fuel));
            Connect(pickup, () => storageSync.RequestPickup());

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

            // A count typed on the campfire's sheet: the keys are its own, Escape leaves the field first.
            Keyboard keyboard = Keyboard.current;
            if (campfire.IsTyping)
            {
                if (keyboard != null && keyboard.escapeKey.wasPressedThisFrame) campfire.Blur();
            }
            else if (keyboard != null && (keyboard.escapeKey.wasPressedThisFrame || keyboard.tabKey.wasPressedThisFrame))
            {
                Close(true);
                return;
            }

            Refresh();
        }

        private void OnOpenedChanged()
        {
            if (openId != 0 || storageSync.OpenedId == 0 || storageSync.OpenedId != pendingId) return;

            openId = pendingId;
            pendingId = 0;
            fireTab = Mode.Cooking;
            screen.style.display = DisplayStyle.Flex;
            UnityEngine.Cursor.lockState = CursorLockMode.None;
            bagGrid.Invalidate();
            pool.Invalidate();
        }

        private void ShowFireTab(Mode tab)
        {
            if (fireTab == tab) return;

            fireTab = tab;
            EndDrag();
            pool.Invalidate();
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

        // Everything shown, as it is now (the grids rebuild only when what they show changed).
        private void Refresh()
        {
            Mode mode = CurrentMode;
            bool fire = storageSync.OpenedKind == StorageKind.Campfire;
            storageTab.style.display = fire ? DisplayStyle.None : DisplayStyle.Flex;
            storageTabText.text = Loc.T(Items.NameOf(StorageRules.ItemOf(storageSync.OpenedKind)));
            ShowTab(cookingTab, fire, mode == Mode.Cooking);
            ShowTab(linkedTab, fire, mode == Mode.Linked);
            ShowTab(fuelTab, fire, mode == Mode.Fuel);

            storagePage.style.display = mode == Mode.Cooking ? DisplayStyle.None : DisplayStyle.Flex;
            cookingPage.style.display = mode == Mode.Cooking ? DisplayStyle.Flex : DisplayStyle.None;
            ShowOwner();
            ShowPickup(mode);
            if (mode == Mode.Cooking)
            {
                campfire.Refresh();
                return;
            }

            Inventory bag = inventorySync.Inventory;
            bagGrid.Show(bag.Entries, item => !Takes(mode, item));
            ShowCarry(bag);
            bagTitle.text = bagGrid.HoveredItem != Items.None ? ItemCatalog.NameOf(bagGrid.HoveredItem, ecosystemSync) : Loc.T("Your bag");

            storeIcon.style.backgroundImage = new StyleBackground(ItemCatalog.IconOf(StorageRules.ItemOf(mode == Mode.Linked ? StorageKind.Chest : storageSync.OpenedKind), ecosystemSync));
            storeBurn.style.display = mode == Mode.Fuel ? DisplayStyle.Flex : DisplayStyle.None;
            storeFillIcon.EnableInClassList("icon--scales", mode == Mode.Chests || mode == Mode.Linked);
            storeFillIcon.EnableInClassList("icon--backpack", mode == Mode.Stockpiles);
            storeFillIcon.EnableInClassList("icon--category-wood", mode == Mode.Fuel);
            bool noChest = mode == Mode.Linked && storageSync.OpenedLinkedChests == 0;
            storeEmpty.text = noChest ? Loc.T("No linked chest: place a chest on the campfire's claimed land, or a public chest within 8 blocks off claimed land.") : "";
            storeEmpty.style.display = noChest ? DisplayStyle.Flex : DisplayStyle.None;

            string side;
            int members = storageSync.OpenedMembers;
            switch (mode)
            {
                case Mode.Chests:
                    pool.ShowChests(storageSync.OpenedMemberList, storageSync.OpenedContents, openId);
                    ShowWeight(storageSync.OpenedContents, members);
                    side = members > 1 ? Loc.T("In the chests ({0})", members) : Loc.T("In the chest");
                    break;
                case Mode.Stockpiles:
                    pool.ShowStockpiles(storageSync.OpenedMemberList, openId);
                    int total = storageSync.OpenedPileTotal;
                    int capacity = Mathf.Max(1, storageSync.OpenedPileCapacity);
                    storeFill.text = $"{total} / {capacity}";
                    ShowShare(storeFill, (float)total / capacity);
                    side = members > 1 ? Loc.T("On the stockpiles ({0})", members) : Loc.T("On the stockpile");
                    break;
                case Mode.Linked:
                    int chests = storageSync.OpenedLinkedChests;
                    pool.ShowChests(storageSync.OpenedLinkedMembers, storageSync.OpenedLinkedContents, 0);
                    ShowWeight(storageSync.OpenedLinkedContents, Mathf.Max(1, chests));
                    side = chests > 1 ? Loc.T("Linked chests ({0})", chests) : Loc.T("Linked chest");
                    break;
                default:
                    pool.ShowSingle(storageSync.OpenedFuelContents);
                    storeFill.text = $"{storageSync.OpenedFuel} / {Campfire.MaxFuel}";
                    ShowShare(storeFill, (float)storageSync.OpenedFuel / Campfire.MaxFuel);
                    storeBurnFill.style.width = Length.Percent(Mathf.Clamp01(storageSync.OpenedBurning / storageSync.OpenedLogSeconds) * 100f);
                    side = Loc.T("By the fire");
                    break;
            }

            // The item under the pointer is named in its side's title (the slots show no names).
            int storeItem = pool.HoveredItem;
            storeTitle.text = storeItem != Items.None ? ItemCatalog.NameOf(storeItem, ecosystemSync) : side;
            ShowFuelInfo(mode, storeItem != Items.None ? storeItem : bagGrid.HoveredItem);
        }

        // The wood side is narrow, as tall as its slots, with how long it all burns and how long the
        // wood under the pointer (on either side) would.
        private void ShowFuelInfo(Mode mode, int hovered)
        {
            bool fuel = mode == Mode.Fuel;
            storeHalf.EnableInClassList("storage-card--fuel", fuel);
            fuelInfo.style.display = fuel ? DisplayStyle.Flex : DisplayStyle.None;
            if (!fuel) return;

            int logs = storageSync.OpenedFuel;
            string cooking = ItemCatalog.DurationText(storageSync.OpenedFuelSeconds);
            fuelTotal.text = logs == 1 ? Loc.T("1 log · {0} of cooking", cooking) : Loc.T("{0} logs · {1} of cooking", logs, cooking);
            float burns = Fuels.SecondsOf(hovered);
            fuelHovered.text = burns > 0f
                ? Loc.T("{0} · burns {1}", ItemCatalog.NameOf(hovered, ecosystemSync), ItemCatalog.DurationText(burns))
                : Loc.T("Each log burns {0}; it burns only while a dish cooks", ItemCatalog.DurationText(Fuels.WoodSeconds));
        }

        private static void ShowTab(Button tab, bool shown, bool selected)
        {
            tab.style.display = shown ? DisplayStyle.Flex : DisplayStyle.None;
            tab.EnableInClassList("inventory-tab--selected", selected);
        }

        // Whether the other side takes an item from the bag.
        private static bool Takes(Mode mode, int item)
        {
            switch (mode)
            {
                case Mode.Stockpiles: return StorageRules.AcceptsInStockpile(item);
                case Mode.Fuel: return Fuels.IsFuel(item);
                default: return StorageRules.AcceptsInChest(item);
            }
        }

        private void ShowWeight(Inventory contents, int chests)
        {
            float weight = StorageRules.ChestWeight(contents, ecosystemSync.ItemWeights);
            float capacity = StorageRules.ChestMaxWeight * chests;
            storeFill.text = $"{Loc.Number(weight, "0.0")} / {capacity:0} kg";
            ShowShare(storeFill, weight / capacity);
        }

        // Its owner, or anyone while it is public, picks it up: empty, or with what it holds poured into
        // the rest of its pool (the server checks); a campfire with nothing queued.
        private void ShowPickup(Mode mode)
        {
            pickup.style.display = storageSync.OpenedIsOwner || storageSync.IsPublic(openId) ? DisplayStyle.Flex : DisplayStyle.None;
            bool alone = storageSync.OpenedMembers <= 1;
            switch (mode)
            {
                case Mode.Chests:
                    pickup.SetEnabled(storageSync.OpenedContents.DistinctCount == 0 || !alone);
                    break;
                case Mode.Stockpiles:
                    pickup.SetEnabled(storageSync.OpenedPileTotal == 0 || !alone);
                    break;
                default:
                    pickup.SetEnabled(storageSync.OpenedJobs.Count == 0);
                    break;
            }
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

        // ---- Moving items ----

        private void Press(bool bag, int itemOrPile, int member, bool byItem, VisualElement slot, PointerDownEvent e)
        {
            if (e.button == 1)
            {
                Move(bag, itemOrPile, member, byItem, false);
                return;
            }

            if (e.button != 0) return;

            if (e.shiftKey)
            {
                Move(bag, itemOrPile, member, byItem, true);
                return;
            }

            pressing = true;
            fromBag = bag;
            pressedItemOrPile = itemOrPile;
            pressedMember = member;
            pressedByItem = byItem;
            pressedSlot = slot;
            pressedAt = e.position;
            slot.CapturePointer(e.pointerId);
        }

        // Moves the whole stack, or one, from one side to the other: into or out of one storage of the
        // pool (member), or the whole pool (0).
        private void Move(bool bag, int itemOrPile, int member, bool byItem, bool all)
        {
            Mode mode = CurrentMode;
            if (bag)
            {
                int count = all ? inventorySync.Inventory.Count(itemOrPile) : 1;
                switch (mode)
                {
                    case Mode.Linked:
                        storageSync.RequestFireMove(true, itemOrPile, count, member);
                        break;
                    case Mode.Fuel:
                        storageSync.RequestFuel(itemOrPile, count);
                        break;
                    default:
                        storageSync.RequestMove(true, itemOrPile, count, member);
                        break;
                }

                return;
            }

            switch (mode)
            {
                case Mode.Chests:
                    storageSync.RequestMove(false, itemOrPile, all ? ContentsOf(storageSync.OpenedMemberList, storageSync.OpenedContents, member).Count(itemOrPile) : 1, member);
                    break;
                case Mode.Stockpiles:
                    int held = byItem ? StockpileCount(itemOrPile) : storageSync.OpenedPileCountAt(itemOrPile);
                    storageSync.RequestMove(false, itemOrPile, all ? held : 1, 0, byItem);
                    break;
                case Mode.Linked:
                    storageSync.RequestFireMove(false, itemOrPile, all ? ContentsOf(storageSync.OpenedLinkedMembers, storageSync.OpenedLinkedContents, member).Count(itemOrPile) : 1, member);
                    break;
                case Mode.Fuel:
                    storageSync.RequestTakeFuel(itemOrPile, all ? storageSync.OpenedFuelContents.Count(itemOrPile) : 1);
                    break;
            }
        }

        // What one storage holds, or the whole pool (member 0).
        private static Inventory ContentsOf(System.Collections.Generic.IReadOnlyList<StorageMember> members, Inventory total, int member)
        {
            foreach (StorageMember one in members)
            {
                if (one.Id == member) return one.Contents;
            }

            return total;
        }

        private int StockpileCount(int item)
        {
            int count = 0;
            foreach (StockpilePiles piles in storageSync.OpenedStockpiles)
            {
                count += piles.CountOf(item);
            }

            return count;
        }

        private void OnPointerMove(PointerMoveEvent e)
        {
            if (!pressing) return;

            if (!dragging && Vector2.Distance(e.position, pressedAt) >= DragThreshold)
            {
                dragging = true;
                bool pile = !fromBag && CurrentMode == Mode.Stockpiles && !pressedByItem;
                int item = pile ? storageSync.OpenedPileItem(pressedItemOrPile) : pressedItemOrPile;
                ghost.style.backgroundImage = new StyleBackground(ItemCatalog.IconOf(item, ecosystemSync));
                ghost.RemoveFromClassList("hidden");
                ghost.BringToFront();
            }

            if (!dragging) return;

            Vector2 local = screen.WorldToLocal(e.position);
            ghost.style.left = local.x - 36f;
            ghost.style.top = local.y - 36f;
        }

        // Dropped on the other side, the whole stack goes there; on a storage's card, into that one.
        private void OnPointerUp(PointerUpEvent e)
        {
            if (!pressing) return;

            if (dragging)
            {
                if (fromBag && storeWell.worldBound.Contains(e.position)) Move(true, pressedItemOrPile, pool.MemberAt(e.position), false, true);
                else if (!fromBag && bagWell.worldBound.Contains(e.position)) Move(false, pressedItemOrPile, pressedMember, pressedByItem, true);
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

using System;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;
using Voxel.Core;

namespace Voxel.Client
{
    // The ten quick slots at the bottom of the screen (Assets/UI/Hotbar.uxml): each shows its item's
    // icon and how many the player has (a slot keeps an item the player ran out of, greyed). The
    // chosen slot is the item in hand (SelectedItem), changed with the digits 1-9 and 0 or the
    // wheel; G gives one to the nearest player (Shift: all). The slots' content is the player's
    // Hotbar, kept by the server (InventorySync); which slot is chosen is this machine's own.
    // While the inventory is open, its slots take items dragged or put there (SlotAt, SlotClicked).
    public class HotbarHud : MonoBehaviour
    {
        private const string SelectedClass = "hotbar-slot--selected";
        private const string EmptyClass = "hotbar-slot--empty";
        private const string SpentClass = "hotbar-slot--spent";
        private const float HeldNameSeconds = 1.6f;

        [SerializeField] private UIDocument document;
        [SerializeField] private NetworkManager networkManager;
        [SerializeField] private WorldSync worldSync;
        [SerializeField] private InventorySync inventorySync;
        [SerializeField] private EcosystemSync ecosystemSync;
        [SerializeField] private ClaimSync claimSync;
        [SerializeField] private CraftingSync craftingSync;
        [SerializeField] private AppearanceMenu appearanceMenu;
        [SerializeField] private MapScreen mapScreen;

        private VisualElement screen;
        private VisualElement bar;
        private VisualElement buildPanel;
        private Label heldName;
        private readonly VisualElement[] slots = new VisualElement[Hotbar.SlotCount];
        private readonly VisualElement[] icons = new VisualElement[Hotbar.SlotCount];
        private readonly Label[] counts = new Label[Hotbar.SlotCount];
        private readonly int[] shownItems = new int[Hotbar.SlotCount];
        private readonly int[] shownCounts = new int[Hotbar.SlotCount];
        private int selected;
        private int shownSelected = -1;
        private float heldNameUntil;
        private string message;
        private float messageUntil;

        // Raised when a slot is clicked (slot, mouse button), for the inventory screen.
        public event Action<int, int> SlotClicked;

        // The item the player holds in hand: the chosen slot's, if they have any of it; else Items.None.
        public int SelectedItem
        {
            get
            {
                if (!Visible) return Items.None;

                int item = inventorySync.Hotbar[selected];
                return item != Items.None && inventorySync.Inventory.Has(item) ? item : Items.None;
            }
        }

        public int SelectedSlot => selected;

        // The bar's full-screen layer, drawn above the inventory: the item dragged from it is shown there.
        internal VisualElement Overlay => screen;

        // How much of the bottom of the screen the bar takes up, in screen pixels (the command line
        // stands above it).
        public float OccupiedHeight
        {
            get
            {
                if (!Visible || screen == null) return 0f;

                float panelHeight = screen.resolvedStyle.height;
                if (float.IsNaN(panelHeight) || panelHeight <= 0f) return 0f;

                // The hammer's panel stands above the bar while it shows.
                float top = bar.worldBound.yMin;
                if (buildPanel != null && !buildPanel.ClassListContains("hidden")) top = Mathf.Min(top, buildPanel.worldBound.yMin);
                return (panelHeight - top) * Screen.height / panelHeight;
            }
        }

        // In the game: hidden while the character is being created and while the full-screen map is open.
        private bool Visible => networkManager.IsListening && worldSync.World != null && !appearanceMenu.IsOpen && !mapScreen.IsOpen;

        private void Start()
        {
            if (Application.isBatchMode)
            {
                enabled = false;
                return;
            }

            VisualElement root = document.rootVisualElement;
            screen = root.Q("screen");
            bar = root.Q("hotbar");
            buildPanel = root.Q("build-panel");
            heldName = root.Q<Label>("held-name");
            for (int slot = 0; slot < Hotbar.SlotCount; slot++)
            {
                int index = slot;
                slots[slot] = root.Q($"slot-{slot}");
                icons[slot] = slots[slot].Q(className: "hotbar-icon");
                counts[slot] = slots[slot].Q<Label>(className: "hotbar-count");
                slots[slot].RegisterCallback<PointerDownEvent>(e => SlotClicked?.Invoke(index, e.button));
                shownItems[slot] = -1;
            }

            screen.style.display = DisplayStyle.None;
            inventorySync.Refused += OnRefused;
            inventorySync.SowRefused += OnSowRefused;
            if (claimSync != null) claimSync.Refused += ShowMessage;
            if (craftingSync != null)
            {
                craftingSync.Refused += OnCraftRefused;
                craftingSync.Crafted += OnCrafted;
            }
        }

        private void OnDestroy()
        {
            if (claimSync != null) claimSync.Refused -= ShowMessage;
            if (craftingSync != null)
            {
                craftingSync.Refused -= OnCraftRefused;
                craftingSync.Crafted -= OnCrafted;
            }

            if (inventorySync == null) return;

            inventorySync.Refused -= OnRefused;
            inventorySync.SowRefused -= OnSowRefused;
        }

        private void OnCraftRefused(CraftRefusal refusal) => ShowMessage(CraftingSync.TextOf(refusal));

        private void OnCrafted(int recipe)
        {
            if (Recipes.TryGet(recipe, out Recipe made)) ShowMessage(Loc.T("Crafted: {0}", ItemCatalog.NameOf(made.Output, ecosystemSync)));
        }

        // A seed that did not sprout: why ("Too cold for an oak").
        private void OnSowRefused(SowRefusal refusal, int species)
        {
            string text = SowRefusals.TextOf(refusal, EcosystemSync.DisplayName(ecosystemSync.PlantName(species)));
            if (text != null) ShowMessage(text);
        }

        private void OnRefused(FullReason reason)
        {
            switch (reason)
            {
                case FullReason.Blocks:
                    ShowMessage(Loc.T("Can't carry more than {0} blocks", CarryLimits.MaxBlocks));
                    break;
                case FullReason.Weight:
                    ShowMessage(Loc.T("Too heavy ({0} kg)", CarryLimits.MaxWeight.ToString("0")));
                    break;
                default:
                    ShowMessage("They can't carry that");
                    break;
            }
        }

        private void Update()
        {
            if (!Visible)
            {
                screen.style.display = DisplayStyle.None;
                return;
            }

            screen.style.display = DisplayStyle.Flex;
            ReadKeys();
            Refresh();
        }

        // The slot whose box holds this point of the panel, or -1.
        public int SlotAt(Vector2 panelPosition)
        {
            if (!Visible) return -1;

            for (int slot = 0; slot < Hotbar.SlotCount; slot++)
            {
                if (slots[slot].worldBound.Contains(panelPosition)) return slot;
            }

            return -1;
        }

        // Shows a short note above the bar a moment, in place of the held item's name ("Too full to eat"),
        // in the player's language: a fixed English text is translated here (Loc), a text with values
        // comes translated already.
        public void ShowMessage(string text)
        {
            message = Loc.T(text);
            messageUntil = Time.unscaledTime + HeldNameSeconds;
        }

        public void Select(int slot)
        {
            if ((uint)slot >= Hotbar.SlotCount || slot == selected) return;

            selected = slot;
            heldNameUntil = Time.unscaledTime + HeldNameSeconds;
        }

        private void ReadKeys()
        {
            Keyboard keyboard = Keyboard.current;
            Mouse mouse = Mouse.current;

            // Keys only steer the hand while playing: not with the cursor free (a screen is open) nor
            // while typing in the command line.
            if (keyboard == null || mouse == null || UnityEngine.Cursor.lockState != CursorLockMode.Locked || CommandConsole.HoldsInput) return;

            int digit = PressedSlot(keyboard);
            if (digit >= 0) Select(digit);

            float scroll = mouse.scroll.ReadValue().y;
            if (scroll < 0f) Select((selected + 1) % Hotbar.SlotCount);
            else if (scroll > 0f) Select((selected + Hotbar.SlotCount - 1) % Hotbar.SlotCount);

            if (keyboard.gKey.wasPressedThisFrame)
            {
                int item = SelectedItem;
                if (item != Items.None)
                {
                    bool all = keyboard.leftShiftKey.isPressed || keyboard.rightShiftKey.isPressed;
                    inventorySync.RequestGive(item, all ? inventorySync.Inventory.Count(item) : 1);
                }
            }
        }

        // The slot of a digit pressed on the top row (1 to 9, then 0 for the tenth), or -1. On an
        // AZERTY keyboard these are the keys of & é " ' ( - è _ ç à, which need no Shift here.
        public static int PressedSlot(Keyboard keyboard)
        {
            if (keyboard.digit1Key.wasPressedThisFrame) return 0;
            if (keyboard.digit2Key.wasPressedThisFrame) return 1;
            if (keyboard.digit3Key.wasPressedThisFrame) return 2;
            if (keyboard.digit4Key.wasPressedThisFrame) return 3;
            if (keyboard.digit5Key.wasPressedThisFrame) return 4;
            if (keyboard.digit6Key.wasPressedThisFrame) return 5;
            if (keyboard.digit7Key.wasPressedThisFrame) return 6;
            if (keyboard.digit8Key.wasPressedThisFrame) return 7;
            if (keyboard.digit9Key.wasPressedThisFrame) return 8;
            return keyboard.digit0Key.wasPressedThisFrame ? 9 : -1;
        }

        // Only slots whose item or count changed are redrawn.
        private void Refresh()
        {
            Hotbar hotbar = inventorySync.Hotbar;
            Inventory inventory = inventorySync.Inventory;
            for (int slot = 0; slot < Hotbar.SlotCount; slot++)
            {
                int item = hotbar[slot];
                int count = item == Items.None ? 0 : inventory.Count(item);
                if (item == shownItems[slot] && count == shownCounts[slot]) continue;

                shownItems[slot] = item;
                shownCounts[slot] = count;
                icons[slot].style.backgroundImage = item == Items.None ? null : new StyleBackground(ItemCatalog.IconOf(item, ecosystemSync));
                counts[slot].text = item == Items.None ? "" : count.ToString();
                slots[slot].EnableInClassList(EmptyClass, item == Items.None);
                slots[slot].EnableInClassList(SpentClass, item != Items.None && count == 0);
                slots[slot].tooltip = item == Items.None ? "" : ItemCatalog.NameOf(item, ecosystemSync);
            }

            if (selected != shownSelected)
            {
                if (shownSelected >= 0) slots[shownSelected].RemoveFromClassList(SelectedClass);
                slots[selected].AddToClassList(SelectedClass);
                shownSelected = selected;
            }

            if (Time.unscaledTime < messageUntil)
            {
                heldName.text = message;
                heldName.style.opacity = Mathf.Clamp01((messageUntil - Time.unscaledTime) / 0.4f);
                return;
            }

            int held = hotbar[selected];
            bool showName = held != Items.None && Time.unscaledTime < heldNameUntil;
            heldName.text = showName ? ItemCatalog.NameOf(held, ecosystemSync) : "";
            heldName.style.opacity = showName ? Mathf.Clamp01((heldNameUntil - Time.unscaledTime) / 0.4f) : 0f;
        }
    }
}

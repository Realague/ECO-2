using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.UIElements;
using Voxel.Core;

namespace Voxel.Client
{
    // What the hammer builds (the "build-panel" box of Assets/UI/Hotbar.uxml, shown above the bar while
    // the hammer is in hand): the construction block chosen from the bag (B, Shift+B back), the shape a
    // wood block takes (R, Shift+R back; a door has none) and the mode, block by block or in lines (V).
    // Holding R opens a wheel (the "build-wheel" box): the shapes on the inner ring, the bag's
    // construction blocks on the outer one; the mouse moves a pointer over it (the view stays still,
    // WheelOpen), and releasing R or a click chooses what it points at (a click keeps the wheel open,
    // to choose a shape and a block in one go). In line mode the first click sets where the line
    // starts (LineStart), the second builds or takes back the line (PlayerController). All of it is
    // this machine's own: nothing is sent or saved.
    public class BuildingHud : MonoBehaviour
    {
        private const string HiddenClass = "hidden";
        private const string HoverClass = "wheel-slot--hover";
        private const string ChosenClass = "wheel-slot--chosen";

        // Holding R longer than this opens the wheel; a shorter press cycles the shape.
        private const float HoldSeconds = 0.2f;

        // The wheel's rings (panel units): shapes, then blocks, at least this far out and far enough for
        // their slots not to touch; within DeadZone of the middle the pointer points at nothing.
        private const float InnerRadius = 172f;
        private const float OuterRadius = 292f;
        private const float BlockSpacing = 74f;
        private const float DeadZone = 56f;

        [SerializeField] private UIDocument document;
        [SerializeField] private HotbarHud hotbar;
        [SerializeField] private InventorySync inventorySync;
        [SerializeField] private EcosystemSync ecosystemSync;

        private VisualElement panel;
        private VisualElement icon;
        private Label blockName;
        private Label detail;
        private VisualElement wheel;
        private VisualElement wheelSlots;
        private VisualElement innerDisc;
        private VisualElement outerDisc;
        private VisualElement pointerDot;
        private Label wheelName;
        private readonly List<int> held = new List<int>();
        private readonly List<VisualElement> shapeSlots = new List<VisualElement>();
        private readonly List<VisualElement> blockSlots = new List<VisualElement>();
        private readonly List<int> blockSlotItems = new List<int>();
        private int block = Items.None;
        private int shapeIndex;
        private int shownBlock = -1;
        private string shownDetail;

        // When R went down (-1 while it is up), and where the wheel's pointer is, from its middle.
        private float rPressedAt = -1f;
        private Vector2 pointer;
        private float outerRadius;

        // Whether the hammer is in hand: the panel shows and right and left clicks build and take back.
        public bool HammerInHand => hotbar.SelectedItem == Items.Hammer;

        // Whether the wheel is open: the mouse moves its pointer, not the view, and clicks choose.
        public bool WheelOpen { get; private set; }

        // The construction block to place (an item, a block type), or Items.None when the bag holds none.
        public int Block => block;

        public Shape Shape => Shapes.All[shapeIndex];

        public bool LineMode { get; private set; }

        // Where the line being laid out starts, and whether it is to take blocks back (a left click).
        public bool HasLineStart { get; private set; }
        public BlockPos LineStart { get; private set; }
        public bool LineTakesBack { get; private set; }

        // How many cells the line from LineStart to the aimed cell holds now (PlayerController's preview).
        public int LineLength { get; set; }

        private void Start()
        {
            if (Application.isBatchMode)
            {
                enabled = false;
                return;
            }

            VisualElement root = document.rootVisualElement;
            panel = root.Q("build-panel");
            icon = root.Q("build-icon");
            blockName = root.Q<Label>("build-block");
            detail = root.Q<Label>("build-detail");
            wheel = root.Q("build-wheel");
            wheelSlots = root.Q("wheel-slots");
            innerDisc = root.Q("wheel-inner-disc");
            outerDisc = root.Q("wheel-outer-disc");
            pointerDot = root.Q("wheel-pointer");
            wheelName = root.Q<Label>("wheel-name");
            Loc.Changed += OnLanguageChanged;
        }

        private void OnDestroy()
        {
            Loc.Changed -= OnLanguageChanged;
        }

        private void OnLanguageChanged()
        {
            shownBlock = -1;
            shownDetail = null;
        }

        // The block a click places by a player looking along yaw (degrees): the chosen block in the chosen
        // shape, facing the way they look (a roof's corner, the diagonal they look along).
        public ushort BlockToPlace(float yaw)
        {
            var type = (ushort)block;
            if (!Shapes.HasShapes(type)) return type;

            int facing = Shapes.IsCorner(Shape) ? Shapes.CornerFacingOf(yaw) : StorageRules.FacingOf(yaw);
            return Shapes.Make(type, Shape, facing);
        }

        public void StartLine(BlockPos start, bool takeBack)
        {
            HasLineStart = true;
            LineStart = start;
            LineTakesBack = takeBack;
        }

        public void ClearLine()
        {
            HasLineStart = false;
            LineLength = 0;
        }

        // The shape's name, as shown in the panel and the aiming label.
        internal static string NameOf(Shape shape)
        {
            switch (shape)
            {
                case Shape.Wall: return Loc.T("Wall");
                case Shape.Post: return Loc.T("Post");
                case Shape.Floor: return Loc.T("Floor");
                case Shape.FlatRoof: return Loc.T("Flat roof");
                case Shape.Slope: return Loc.T("Sloped roof");
                case Shape.Fence: return Loc.T("Fence");
                case Shape.Stairs: return Loc.T("Stairs");
                case Shape.OuterCorner: return Loc.T("Roof corner (outer)");
                case Shape.InnerCorner: return Loc.T("Roof corner (inner)");
                case Shape.Ladder: return Loc.T("Ladder");
                default: return Loc.T("Full block");
            }
        }

        private void Update()
        {
            if (panel == null) return;

            if (!HammerInHand)
            {
                ClearLine();
                CloseWheel();
                rPressedAt = -1f;
                panel.AddToClassList(HiddenClass);
                return;
            }

            GatherHeld();
            if (!held.Contains(block)) block = held.Count > 0 ? held[0] : Items.None;

            ReadKeys();
            panel.RemoveFromClassList(HiddenClass);
            Refresh();
        }

        // The construction blocks the bag holds, in item order.
        private void GatherHeld()
        {
            held.Clear();
            for (int type = Blocks.FirstPlanks; type <= Blocks.Door; type++)
            {
                if (Blocks.IsConstruction((ushort)type) && inventorySync.Inventory.Has(type)) held.Add(type);
            }
        }

        private void ReadKeys()
        {
            Keyboard keyboard = Keyboard.current;
            Mouse mouse = Mouse.current;
            if (keyboard == null || mouse == null || UnityEngine.Cursor.lockState != CursorLockMode.Locked || CommandConsole.HoldsInput)
            {
                CloseWheel();
                rPressedAt = -1f;
                return;
            }

            int step = keyboard.leftShiftKey.isPressed || keyboard.rightShiftKey.isPressed ? -1 : 1;
            KeyControl shapeKey = KeyFor(keyboard, "r", keyboard.rKey);
            if (shapeKey.wasPressedThisFrame) rPressedAt = Time.unscaledTime;

            if (rPressedAt >= 0f && shapeKey.isPressed && !WheelOpen && Time.unscaledTime - rPressedAt >= HoldSeconds) OpenWheel();

            if (WheelOpen)
            {
                MovePointer(mouse);
                if (mouse.leftButton.wasPressedThisFrame) ChooseHovered();
                if (!shapeKey.isPressed)
                {
                    ChooseHovered();
                    CloseWheel();
                    rPressedAt = -1f;
                }

                return;
            }

            // A short press cycles the shape, as before the wheel.
            if (shapeKey.wasReleasedThisFrame && rPressedAt >= 0f)
            {
                rPressedAt = -1f;
                if (block != Blocks.Door) shapeIndex = Wrap(shapeIndex + step, Shapes.All.Length);
            }

            if (KeyFor(keyboard, "b", keyboard.bKey).wasPressedThisFrame && held.Count > 0)
            {
                int index = held.IndexOf(block);
                block = held[index < 0 ? 0 : Wrap(index + step, held.Count)];
            }

            if (KeyFor(keyboard, "v", keyboard.vKey).wasPressedThisFrame)
            {
                LineMode = !LineMode;
                ClearLine();
            }
        }

        private static int Wrap(int value, int count) => ((value % count) + count) % count;

        // A key found by the letter it types on this keyboard's layout (the same letters on AZERTY).
        private static KeyControl KeyFor(Keyboard keyboard, string letter, KeyControl fallback)
        {
            return keyboard.FindKeyOnCurrentKeyboardLayout(letter) ?? fallback;
        }

        // ---- The wheel ----

        private void OpenWheel()
        {
            if (wheel == null) return;

            WheelOpen = true;
            pointer = Vector2.zero;
            BuildWheel();
            wheel.RemoveFromClassList(HiddenClass);
            ShowHover();
        }

        private void CloseWheel()
        {
            if (!WheelOpen) return;

            WheelOpen = false;
            wheel.AddToClassList(HiddenClass);
        }

        // The slots, made again at each opening since the bag may have changed.
        private void BuildWheel()
        {
            wheelSlots.Clear();
            shapeSlots.Clear();
            blockSlots.Clear();
            blockSlotItems.Clear();

            for (int i = 0; i < Shapes.All.Length; i++)
            {
                var slot = new VisualElement { pickingMode = PickingMode.Ignore };
                slot.AddToClassList("wheel-slot");
                slot.AddToClassList("wheel-slot--shape");
                slot.EnableInClassList("wheel-slot--faded", block == Blocks.Door);
                var glyph = new ShapeGlyph(Shapes.All[i]);
                glyph.AddToClassList("shape-glyph");
                slot.Add(glyph);
                Place(slot, AngleOf(i, Shapes.All.Length), InnerRadius);
                wheelSlots.Add(slot);
                shapeSlots.Add(slot);
            }

            outerRadius = Mathf.Max(OuterRadius, held.Count * BlockSpacing / (2f * Mathf.PI));
            for (int i = 0; i < held.Count; i++)
            {
                var slot = new VisualElement { pickingMode = PickingMode.Ignore };
                slot.AddToClassList("wheel-slot");
                slot.AddToClassList("wheel-slot--block");
                var picture = new VisualElement { pickingMode = PickingMode.Ignore };
                picture.AddToClassList("wheel-block-icon");
                picture.style.backgroundImage = new StyleBackground(ItemCatalog.IconOf(held[i], ecosystemSync));
                slot.Add(picture);
                var count = new Label(inventorySync.Inventory.Count(held[i]).ToString()) { pickingMode = PickingMode.Ignore };
                count.AddToClassList("wheel-block-count");
                slot.Add(count);
                Place(slot, AngleOf(i, held.Count), outerRadius);
                wheelSlots.Add(slot);
                blockSlots.Add(slot);
                blockSlotItems.Add(held[i]);
            }

            SizeDisc(innerDisc, InnerRadius + 56f);
            SizeDisc(outerDisc, held.Count > 0 ? outerRadius + 48f : 0f);
            MarkChosen();
        }

        // Slot i of count, clockwise from the top (radians).
        private static float AngleOf(int i, int count) => i * 2f * Mathf.PI / count;

        private static void Place(VisualElement element, float angle, float radius)
        {
            element.style.left = radius * Mathf.Sin(angle);
            element.style.top = -radius * Mathf.Cos(angle);
        }

        private static void SizeDisc(VisualElement disc, float radius)
        {
            disc.style.display = radius > 0f ? DisplayStyle.Flex : DisplayStyle.None;
            disc.style.left = -radius;
            disc.style.top = -radius;
            disc.style.width = 2f * radius;
            disc.style.height = 2f * radius;
            disc.style.borderTopLeftRadius = radius;
            disc.style.borderTopRightRadius = radius;
            disc.style.borderBottomLeftRadius = radius;
            disc.style.borderBottomRightRadius = radius;
        }

        // The mouse's moves, in panel units, move the pointer, kept a little past the outer ring.
        private void MovePointer(Mouse mouse)
        {
            Vector2 delta = mouse.delta.ReadValue();
            float panelHeight = document.rootVisualElement.resolvedStyle.height;
            float scale = float.IsNaN(panelHeight) || panelHeight <= 0f ? 1f : panelHeight / Screen.height;
            pointer += new Vector2(delta.x, -delta.y) * scale;
            float limit = (blockSlots.Count > 0 ? outerRadius : InnerRadius) + 40f;
            if (pointer.magnitude > limit) pointer = pointer.normalized * limit;
            ShowHover();
        }

        // What the pointer points at: a shape (isShape) or a block's slot, by its ring and angle; false
        // near the middle.
        private bool TryHovered(out bool isShape, out int index)
        {
            isShape = true;
            index = -1;
            float distance = pointer.magnitude;
            if (distance < DeadZone) return false;

            isShape = blockSlots.Count == 0 || distance < (InnerRadius + outerRadius) * 0.5f;
            int count = isShape ? shapeSlots.Count : blockSlots.Count;
            float angle = Mathf.Atan2(pointer.x, -pointer.y);
            if (angle < 0f) angle += 2f * Mathf.PI;
            index = Wrap(Mathf.RoundToInt(angle / (2f * Mathf.PI / count)), count);
            return true;
        }

        private void ShowHover()
        {
            bool any = TryHovered(out bool isShape, out int index);
            for (int i = 0; i < shapeSlots.Count; i++) shapeSlots[i].EnableInClassList(HoverClass, any && isShape && i == index);
            for (int i = 0; i < blockSlots.Count; i++) blockSlots[i].EnableInClassList(HoverClass, any && !isShape && i == index);

            pointerDot.style.left = pointer.x;
            pointerDot.style.top = pointer.y;
            wheelName.text = !any ? CurrentChoice()
                : isShape ? NameOf(Shapes.All[index])
                : ItemCatalog.NameOf(blockSlotItems[index], ecosystemSync);
        }

        // What is chosen now, shown in the middle while the pointer points at nothing.
        private string CurrentChoice()
        {
            if (block == Items.None) return Loc.T("No construction block in the bag");

            string name = ItemCatalog.NameOf(block, ecosystemSync);
            return block == Blocks.Door ? name : name + "\n" + NameOf(Shape);
        }

        private void ChooseHovered()
        {
            if (!TryHovered(out bool isShape, out int index)) return;

            if (isShape) shapeIndex = index;
            else block = blockSlotItems[index];

            for (int i = 0; i < shapeSlots.Count; i++) shapeSlots[i].EnableInClassList("wheel-slot--faded", block == Blocks.Door);
            MarkChosen();
        }

        private void MarkChosen()
        {
            for (int i = 0; i < shapeSlots.Count; i++) shapeSlots[i].EnableInClassList(ChosenClass, i == shapeIndex);
            for (int i = 0; i < blockSlots.Count; i++) blockSlots[i].EnableInClassList(ChosenClass, blockSlotItems[i] == block);
        }

        // ---- The panel ----

        private void Refresh()
        {
            if (block != shownBlock)
            {
                shownBlock = block;
                icon.style.backgroundImage = block == Items.None ? null : new StyleBackground(ItemCatalog.IconOf(block, ecosystemSync));
                blockName.text = block == Items.None ? Loc.T("No construction block in the bag") : ItemCatalog.NameOf(block, ecosystemSync);
            }

            string shape = block == Blocks.Door || block == Items.None ? "" : NameOf(Shape) + " · ";
            string mode = !LineMode ? Loc.T("Block by block")
                : HasLineStart ? Loc.T("Line · {0} / {1}", LineLength, BuildLines.MaxLength)
                : Loc.T("Line");
            string text = shape + mode;
            if (text == shownDetail) return;

            shownDetail = text;
            detail.text = text;
        }
    }
}

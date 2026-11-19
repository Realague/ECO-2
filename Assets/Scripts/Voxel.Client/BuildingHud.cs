using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.UIElements;
using Voxel.Core;

namespace Voxel.Client
{
    // What the hammer builds (the "build-panel" box of Assets/UI/Hotbar.uxml, shown at the bottom right
    // while the hammer is in hand, in place of the key hints, which HudBanner hides; not while a window
    // is open): the construction block chosen from the bag (B, Shift+B back), the shape a wood block takes (R, Shift+R back; a door has none), its rotation (E, Shift+E back: Shapes'
    // rotations of the shape, turned from the way the player looks; kept per shape) and the mode, block
    // by block or in lines (V). Holding R opens a wheel (the "build-wheel" box): the shapes on the inner
    // ring, the bag's construction blocks on the outer one; holding E opens it with the shape's
    // rotations on its one ring. The mouse moves a pointer over it (the view stays still, WheelOpen),
    // and releasing the key or a click chooses what it points at (a click keeps the wheel open, to
    // choose a shape and a block in one go). In line mode the first click sets where the line starts
    // (LineStart), the second builds or takes back the line (PlayerController). All of it is this
    // machine's own: nothing is sent or saved.
    public class BuildingHud : MonoBehaviour
    {
        private const string HiddenClass = "hidden";
        private const string HoverClass = "wheel-slot--hover";
        private const string ChosenClass = "wheel-slot--chosen";

        // Holding R or E longer than this opens the wheel; a shorter press cycles the shape or rotation.
        private const float HoldSeconds = 0.35f;

        // The wheel's rings (panel units): shapes (or rotations), then blocks, at least this far out and
        // far enough for their slots not to touch; within DeadZone of the middle the pointer points at
        // nothing.
        private const float InnerRadius = 172f;
        private const float OuterRadius = 292f;
        private const float BlockSpacing = 74f;
        private const float DeadZone = 56f;

        // What a wheel shows: shapes and blocks (R), or the shape's rotations (E).
        private enum WheelKind
        {
            Shapes,
            Rotations
        }

        [SerializeField] private UIDocument document;
        [SerializeField] private HotbarHud hotbar;
        [SerializeField] private InventorySync inventorySync;
        [SerializeField] private EcosystemSync ecosystemSync;

        private VisualElement panel;
        private InventoryScreen inventoryScreen;
        private VisualElement icon;
        private Label blockName;
        private Label detail;
        private Label modeLabel;
        private VisualElement wheel;
        private VisualElement wheelSlots;
        private VisualElement innerDisc;
        private VisualElement outerDisc;
        private VisualElement pointerDot;
        private Label wheelName;
        private Label wheelHint;
        private readonly List<int> held = new List<int>();
        private readonly List<VisualElement> shapeSlots = new List<VisualElement>();
        private readonly List<VisualElement> blockSlots = new List<VisualElement>();
        private readonly List<int> blockSlotItems = new List<int>();
        private readonly int[] rotations = new int[Shapes.Families.Length];
        private int block = Items.None;
        private int familyIndex;
        // A block picked in creative mode, given by the server: kept chosen while it is on its way.
        private const float PickWaitSeconds = 2f;
        private int pickedBlock = Items.None;
        private float pickedUntil;
        private int shownBlock = -1;
        private string shownDetail;

        // Which key went down and when (-1 while none is down), what the open wheel shows, and where
        // its pointer is, from its middle.
        private WheelKind pressedKind;
        private float pressedAt = -1f;
        private WheelKind wheelKind;
        private Vector2 pointer;

        // Whether the pointer has pointed at something since the wheel opened.
        private bool wheelPointed;
        private float outerRadius;

        // Whether the hammer is in hand: the panel shows and right and left clicks build and take back.
        public bool HammerInHand => hotbar.SelectedItem == Items.Hammer;

        // Whether the wheel is open: the mouse moves its pointer, not the view, and clicks choose.
        public bool WheelOpen { get; private set; }

        // The construction block to place (an item, a block type), or Items.None when the bag holds none.
        public int Block => block;

        public ShapeFamily Family => Shapes.Families[familyIndex];

        // The chosen rotation of the chosen shape.
        public int Rotation => rotations[familyIndex];

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

            inventoryScreen = FindAnyObjectByType<InventoryScreen>();
            VisualElement root = document.rootVisualElement;
            panel = root.Q("build-panel");
            icon = root.Q("build-icon");
            blockName = root.Q<Label>("build-block");
            detail = root.Q<Label>("build-detail");
            modeLabel = root.Q<Label>("build-mode");
            wheel = root.Q("build-wheel");
            wheelSlots = root.Q("wheel-slots");
            innerDisc = root.Q("wheel-inner-disc");
            outerDisc = root.Q("wheel-outer-disc");
            pointerDot = root.Q("wheel-pointer");
            wheelName = root.Q<Label>("wheel-name");
            wheelHint = root.Q<Label>("wheel-hint");
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
        // shape and rotation, turned from the way they look (a roof's corner, the diagonal they look along).
        public ushort BlockToPlace(float yaw) => Shapes.Place((ushort)block, Family, Rotation, yaw);

        // The pipette (a middle click with the hammer on a construction block, PlayerController): that
        // block, with the shape and rotation that would place it again as it is for a player looking
        // along yaw. In creative mode a block the bag lacks is given (a stack); otherwise the bag must hold
        // it, or nothing changes.
        public void Pick(ushort aimed, float yaw)
        {
            int type = Blocks.TypeOf(aimed);
            if (!Blocks.IsConstruction((ushort)type))
            {
                hotbar.ShowMessage("Only construction blocks can be picked");
                return;
            }

            if (!inventorySync.Inventory.Has(type))
            {
                if (!inventorySync.Creative)
                {
                    string name = ItemCatalog.NameOf(type, ecosystemSync);
                    hotbar.ShowMessage(Loc.T("You have no {1}", name, name.ToLower(Loc.Culture)));
                    return;
                }

                inventorySync.RequestCreativeGive(type, CreativeRules.StackOf(type));
                // Until the server's answer puts it in the bag, the panel keeps it (Update).
                pickedBlock = type;
                pickedUntil = Time.unscaledTime + PickWaitSeconds;
            }

            block = type;
            if (type == Blocks.Door) return;

            // The family and rotation that place this very code; a code no rotation gives (a fence of
            // earlier versions, without its axis) keeps its family and the rotation chosen before.
            for (int family = 0; family < Shapes.Families.Length; family++)
            {
                for (int rotation = 0; rotation < Shapes.RotationCount(Shapes.Families[family]); rotation++)
                {
                    if (Shapes.Place((ushort)type, Shapes.Families[family], rotation, yaw) != aimed) continue;

                    familyIndex = family;
                    rotations[family] = rotation;
                    return;
                }
            }

            int index = Array.IndexOf(Shapes.Families, Shapes.FamilyOf(Shapes.ShapeOf(aimed)));
            if (index >= 0) familyIndex = index;
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

        // A shaped block's name, as the aiming label shows it: its family's, or "Beam" for a lying post.
        internal static string NameOf(Shape shape)
        {
            return shape == Shape.Beam || shape == Shape.EdgeBeam ? Loc.T("Beam") : NameOf(Shapes.FamilyOf(shape));
        }

        // A shape's name, as shown in the panel and the wheel.
        internal static string NameOf(ShapeFamily family)
        {
            switch (family)
            {
                case ShapeFamily.Wall: return Loc.T("Wall");
                case ShapeFamily.Post: return Loc.T("Post");
                case ShapeFamily.Slab: return Loc.T("Slab");
                case ShapeFamily.Slope: return Loc.T("Sloped roof");
                case ShapeFamily.Fence: return Loc.T("Fence");
                case ShapeFamily.LowWall: return Loc.T("Low wall");
                case ShapeFamily.Column: return Loc.T("Column");
                case ShapeFamily.Stairs: return Loc.T("Stairs");
                case ShapeFamily.OuterCorner: return Loc.T("Roof corner (outer)");
                case ShapeFamily.InnerCorner: return Loc.T("Roof corner (inner)");
                case ShapeFamily.Ladder: return Loc.T("Ladder");
                default: return Loc.T("Full block");
            }
        }

        private static readonly string[] PostRotations =
        {
            "Upright, in the middle", "Upright, far left corner", "Upright, far right corner", "Upright, near right corner",
            "Upright, near left corner", "Lying across", "Lying ahead", "Lying on the far top edge", "Lying on the right top edge",
            "Lying on the near top edge", "Lying on the left top edge"
        };

        private static readonly string[] SlabRotations = { "Top", "Bottom", "Upright, far", "Upright, right", "Upright, near", "Upright, left" };

        // A rotation's name: where a post or a slab lies, a column's part, a fence's way, else the
        // quarter turns from the way the player looks.
        internal static string RotationName(ShapeFamily family, int rotation)
        {
            switch (family)
            {
                case ShapeFamily.Post: return Loc.T(PostRotations[rotation]);
                case ShapeFamily.Slab: return Loc.In("slab", SlabRotations[rotation]);
                case ShapeFamily.Column: return Loc.In("column", rotation == 0 ? "Shaft" : rotation == 1 ? "Base" : "Top");
                case ShapeFamily.Fence:
                case ShapeFamily.LowWall: return Loc.T(rotation == 0 ? "Across" : "Ahead");
                default: return (rotation * 90) + "°";
            }
        }

        private void Update()
        {
            if (panel == null) return;

            if (!HammerInHand)
            {
                ClearLine();
                CloseWheel();
                pressedAt = -1f;
                panel.AddToClassList(HiddenClass);
                return;
            }

            GatherHeld();
            bool waitingForPick = block == pickedBlock && Time.unscaledTime < pickedUntil;
            if (!held.Contains(block) && !waitingForPick) block = held.Count > 0 ? held[0] : Items.None;

            ReadKeys();
            // The inventory and a storage window take the screen: the panel would stand over their sheets.
            bool windowOpen = (inventoryScreen != null && inventoryScreen.IsOpen) || StorageScreen.IsOpen;
            if (windowOpen) panel.AddToClassList(HiddenClass);
            else panel.RemoveFromClassList(HiddenClass);
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
                pressedAt = -1f;
                return;
            }

            int step = keyboard.leftShiftKey.isPressed || keyboard.rightShiftKey.isPressed ? -1 : 1;
            KeyControl shapeKey = KeyFor(keyboard, "r", keyboard.rKey);
            KeyControl turnKey = KeyFor(keyboard, "e", keyboard.eKey);
            if (!WheelOpen && pressedAt < 0f)
            {
                if (shapeKey.wasPressedThisFrame) Press(WheelKind.Shapes);
                else if (turnKey.wasPressedThisFrame) Press(WheelKind.Rotations);
            }

            KeyControl key = pressedKind == WheelKind.Shapes ? shapeKey : turnKey;
            if (pressedAt >= 0f && key.isPressed && !WheelOpen && Time.unscaledTime - pressedAt >= HoldSeconds && CanOpen(pressedKind)) OpenWheel(pressedKind);

            if (WheelOpen)
            {
                MovePointer(mouse);
                if (TryHovered(out _, out _)) wheelPointed = true;
                if (mouse.leftButton.wasPressedThisFrame) ChooseHovered();
                if (!key.isPressed)
                {
                    // Released without ever pointing at anything (a slow press): it was meant as a press.
                    if (TryHovered(out _, out _)) ChooseHovered();
                    else if (!wheelPointed) Cycle(wheelKind, step);
                    CloseWheel();
                    pressedAt = -1f;
                }

                return;
            }

            // A short press cycles the shape or the rotation, as before the wheel.
            if (pressedAt >= 0f && key.wasReleasedThisFrame)
            {
                pressedAt = -1f;
                Cycle(pressedKind, step);
            }
            else if (pressedAt >= 0f && !key.isPressed)
            {
                pressedAt = -1f;
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

        private void Press(WheelKind kind)
        {
            pressedKind = kind;
            pressedAt = Time.unscaledTime;
        }

        // Whether a wheel has anything to choose from: a door has no shape, a full block no rotation.
        private bool CanOpen(WheelKind kind) => kind == WheelKind.Shapes || (block != Blocks.Door && Shapes.RotationCount(Family) > 1);

        private void Cycle(WheelKind kind, int step)
        {
            if (block == Blocks.Door) return;

            if (kind == WheelKind.Shapes) familyIndex = Wrap(familyIndex + step, Shapes.Families.Length);
            else rotations[familyIndex] = Wrap(Rotation + step, Shapes.RotationCount(Family));
        }

        private static int Wrap(int value, int count) => ((value % count) + count) % count;

        // A key found by the letter it types on this keyboard's layout (the same letters on AZERTY).
        private static KeyControl KeyFor(Keyboard keyboard, string letter, KeyControl fallback)
        {
            return keyboard.FindKeyOnCurrentKeyboardLayout(letter) ?? fallback;
        }

        // A glyph of a family in a rotation, as the player would place it looking ahead.
        private static ShapeGlyph GlyphOf(ShapeFamily family, int rotation)
        {
            (Shape shape, int turns) = Shapes.RotationOf(family, rotation);
            return new ShapeGlyph(shape, turns & 3);
        }

        // ---- The wheel ----

        private void OpenWheel(WheelKind kind)
        {
            if (wheel == null) return;

            WheelOpen = true;
            wheelKind = kind;
            wheelPointed = false;
            pointer = Vector2.zero;
            BuildWheel();
            if (wheelHint != null) wheelHint.text = Loc.T(kind == WheelKind.Shapes ? "Point at one, then release R or click" : "Point at one, then release E or click");
            wheel.RemoveFromClassList(HiddenClass);
            ShowHover();
        }

        private void CloseWheel()
        {
            if (!WheelOpen) return;

            WheelOpen = false;
            wheel.AddToClassList(HiddenClass);
        }

        // The slots, made again at each opening since the bag may have changed: the shapes (each in its
        // chosen rotation) and the blocks, or the chosen shape's rotations.
        private void BuildWheel()
        {
            wheelSlots.Clear();
            shapeSlots.Clear();
            blockSlots.Clear();
            blockSlotItems.Clear();

            int count = wheelKind == WheelKind.Shapes ? Shapes.Families.Length : Shapes.RotationCount(Family);
            for (int i = 0; i < count; i++)
            {
                var slot = new VisualElement { pickingMode = PickingMode.Ignore };
                slot.AddToClassList("wheel-slot");
                slot.AddToClassList("wheel-slot--shape");
                slot.EnableInClassList("wheel-slot--faded", block == Blocks.Door);
                ShapeGlyph glyph = wheelKind == WheelKind.Shapes ? GlyphOf(Shapes.Families[i], rotations[i]) : GlyphOf(Family, i);
                glyph.AddToClassList("shape-glyph");
                slot.Add(glyph);
                Place(slot, AngleOf(i, count), InnerRadius);
                wheelSlots.Add(slot);
                shapeSlots.Add(slot);
            }

            if (wheelKind == WheelKind.Shapes)
            {
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
                    var countLabel = new Label(inventorySync.Inventory.Count(held[i]).ToString()) { pickingMode = PickingMode.Ignore };
                    countLabel.AddToClassList("wheel-block-count");
                    slot.Add(countLabel);
                    Place(slot, AngleOf(i, held.Count), outerRadius);
                    wheelSlots.Add(slot);
                    blockSlots.Add(slot);
                    blockSlotItems.Add(held[i]);
                }
            }

            SizeDisc(innerDisc, InnerRadius + 56f);
            SizeDisc(outerDisc, blockSlots.Count > 0 ? outerRadius + 48f : 0f);
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

        // What the pointer points at: a shape or rotation (isShape) or a block's slot, by its ring and
        // angle; false near the middle.
        private bool TryHovered(out bool isShape, out int index)
        {
            isShape = true;
            index = -1;
            float distance = pointer.magnitude;
            if (distance < DeadZone) return false;

            isShape = blockSlots.Count == 0 || distance < (InnerRadius + outerRadius) * 0.5f;
            int count = isShape ? shapeSlots.Count : blockSlots.Count;
            if (count == 0) return false;

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
                : !isShape ? ItemCatalog.NameOf(blockSlotItems[index], ecosystemSync)
                : wheelKind == WheelKind.Shapes ? NameOf(Shapes.Families[index])
                : NameOf(Family) + "\n" + RotationName(Family, index);
        }

        // What is chosen now, shown in the middle while the pointer points at nothing.
        private string CurrentChoice()
        {
            if (block == Items.None) return Loc.T("No construction block in the bag");

            string name = ItemCatalog.NameOf(block, ecosystemSync);
            return block == Blocks.Door ? name : name + "\n" + ShapeText(" · ");
        }

        // The shape and, when it has several, its rotation.
        private string ShapeText(string separator)
        {
            return Shapes.RotationCount(Family) > 1 ? NameOf(Family) + separator + RotationName(Family, Rotation) : NameOf(Family);
        }

        private void ChooseHovered()
        {
            if (!TryHovered(out bool isShape, out int index)) return;

            if (!isShape) block = blockSlotItems[index];
            else if (block != Blocks.Door && wheelKind == WheelKind.Shapes) familyIndex = index;
            else if (block != Blocks.Door) rotations[familyIndex] = index;

            for (int i = 0; i < shapeSlots.Count; i++) shapeSlots[i].EnableInClassList("wheel-slot--faded", block == Blocks.Door);
            MarkChosen();
        }

        private void MarkChosen()
        {
            int chosen = wheelKind == WheelKind.Shapes ? familyIndex : Rotation;
            for (int i = 0; i < shapeSlots.Count; i++) shapeSlots[i].EnableInClassList(ChosenClass, i == chosen);
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

            string shape = block == Blocks.Door || block == Items.None ? "" : ShapeText(" · ");
            string mode = !LineMode ? Loc.T("Block by block")
                : HasLineStart ? Loc.T("Line · {0} / {1}", LineLength, BuildLines.MaxLength)
                : Loc.T("Line");
            string text = shape + "\n" + mode;
            if (text == shownDetail) return;

            shownDetail = text;
            detail.text = shape;
            detail.EnableInClassList(HiddenClass, shape.Length == 0);
            if (modeLabel != null) modeLabel.text = mode;
        }
    }
}

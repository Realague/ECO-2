using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using Voxel.Core;

namespace Voxel.Client
{
    // A grid of item slots in a scrolled well, as the inventory draws them (Inventory.uss: icon, count
    // at the bottom right, sorted by kind then name, empty slots to whole rows and at least a few rows,
    // as many columns of about TargetSlot as the well takes, widened to fill it). Rebuilt only when what
    // it shows changes. Presses, moves and releases on a slot are passed on with its item; a pressed
    // slot holds the pointer (CapturePointer) so a drag's moves and end come to it.
    internal sealed class ItemGrid
    {
        private const float TargetSlot = 124f;
        private const float SlotGap = 12f;
        private const float FitSlack = 2f;

        private readonly ScrollView view;
        private readonly EcosystemSync ecosystemSync;
        private readonly int minimumRows;
        private readonly List<KeyValuePair<int, int>> shown = new List<KeyValuePair<int, int>>();
        private readonly List<int> sorted = new List<int>();
        private readonly Dictionary<int, VisualElement> slots = new Dictionary<int, VisualElement>();
        private readonly HashSet<int> shownDimmed = new HashSet<int>();
        private readonly Dictionary<int, int> counts = new Dictionary<int, int>();
        private int columns;
        private float size;
        private bool dirty = true;

        // A press on an item's slot (the slot, the event), a move or a release while it holds the pointer.
        public event Action<int, VisualElement, PointerDownEvent> Pressed;
        public event Action<PointerMoveEvent> Moved;
        public event Action<PointerUpEvent> Released;

        public int HoveredItem { get; private set; } = Items.None;

        public ItemGrid(ScrollView view, EcosystemSync ecosystemSync, int minimumRows)
        {
            this.view = view;
            this.ecosystemSync = ecosystemSync;
            this.minimumRows = minimumRows;
            view.contentContainer.style.flexDirection = FlexDirection.Row;
            view.contentContainer.style.flexWrap = Wrap.Wrap;
            view.verticalScrollerVisibility = ScrollerVisibility.AlwaysVisible;
            view.contentViewport.RegisterCallback<GeometryChangedEvent>(_ =>
            {
                Layout(out int newColumns, out float newSize);
                if (newColumns != columns || !Mathf.Approximately(newSize, size)) dirty = true;
            });
        }

        public void Invalidate() => dirty = true;

        // Shows these items (item, count); dimmed ones are greyed and take no clicks.
        public void Show(IEnumerable<KeyValuePair<int, int>> entries, Func<int, bool> dimmed)
        {
            sorted.Clear();
            bool changed = dirty;
            int index = 0;
            foreach (KeyValuePair<int, int> entry in entries)
            {
                if (index >= shown.Count || !shown[index].Equals(entry)) changed = true;
                if (dimmed(entry.Key) != shownDimmed.Contains(entry.Key)) changed = true;
                sorted.Add(entry.Key);
                index++;
            }

            if (index != shown.Count) changed = true;
            if (!changed) return;

            dirty = false;
            shown.Clear();
            shownDimmed.Clear();
            foreach (KeyValuePair<int, int> entry in entries)
            {
                shown.Add(entry);
                if (dimmed(entry.Key)) shownDimmed.Add(entry.Key);
            }

            sorted.Sort((a, b) =>
            {
                int byKind = ItemCatalog.CategoryOf(a).CompareTo(ItemCatalog.CategoryOf(b));
                return byKind != 0 ? byKind : string.Compare(ItemCatalog.NameOf(a, ecosystemSync), ItemCatalog.NameOf(b, ecosystemSync), StringComparison.OrdinalIgnoreCase);
            });

            Layout(out columns, out size);
            view.Clear();
            slots.Clear();
            HoveredItem = Items.None;
            counts.Clear();
            foreach (KeyValuePair<int, int> entry in shown)
            {
                counts[entry.Key] = entry.Value;
            }

            foreach (int item in sorted)
            {
                VisualElement slot = MakeSlot(item, counts[item], shownDimmed.Contains(item));
                view.Add(slot);
                slots[item] = slot;
            }

            int total = Mathf.Max(minimumRows, (sorted.Count + columns - 1) / columns) * columns;
            for (int i = sorted.Count; i < total; i++)
            {
                var empty = new VisualElement { pickingMode = PickingMode.Ignore };
                empty.AddToClassList("item-slot");
                empty.AddToClassList("item-slot--empty");
                empty.style.width = size;
                empty.style.height = size;
                view.Add(empty);
            }
        }

        public bool TryGetSlot(int item, out VisualElement slot) => slots.TryGetValue(item, out slot);

        private void Layout(out int columnCount, out float slotSize)
        {
            float width = view.contentViewport.layout.width;
            if (float.IsNaN(width) || width < TargetSlot + SlotGap) width = 4f * (TargetSlot + SlotGap);
            columnCount = Mathf.Max(1, Mathf.FloorToInt(width / (TargetSlot + SlotGap)));
            // A little slack: slots filling the width exactly can wrap once the panel scales to the screen.
            slotSize = Mathf.Floor((width - FitSlack) / columnCount - SlotGap);
        }

        private VisualElement MakeSlot(int item, int count, bool dimmed)
        {
            var slot = new VisualElement { userData = item };
            slot.AddToClassList("item-slot");
            slot.EnableInClassList("item-slot--dimmed", dimmed);
            slot.style.width = size;
            slot.style.height = size;
            var icon = new VisualElement { pickingMode = PickingMode.Ignore };
            icon.AddToClassList("item-icon");
            icon.style.backgroundImage = new StyleBackground(ItemCatalog.IconOf(item, ecosystemSync));
            var amount = new Label(count.ToString()) { pickingMode = PickingMode.Ignore };
            amount.AddToClassList("item-count");
            slot.Add(icon);
            slot.Add(amount);

            slot.RegisterCallback<PointerEnterEvent>(_ => HoveredItem = item);
            slot.RegisterCallback<PointerLeaveEvent>(_ =>
            {
                if (HoveredItem == item) HoveredItem = Items.None;
            });
            if (dimmed) return slot;

            slot.RegisterCallback<PointerDownEvent>(e => Pressed?.Invoke(item, slot, e));
            slot.RegisterCallback<PointerMoveEvent>(e => Moved?.Invoke(e));
            slot.RegisterCallback<PointerUpEvent>(e => Released?.Invoke(e));
            return slot;
        }
    }
}

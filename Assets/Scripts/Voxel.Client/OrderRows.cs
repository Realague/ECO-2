using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace Voxel.Client
{
    // The rows of a queue of orders (Inventory.uss: .job-*), for the inventory's crafting tab and a
    // campfire's: each order's icon, name and a line under it, a bar with what it waits for, an
    // optional button putting calories into it (Shift: all it needs) and one cancelling it. Laid out
    // again only when the orders change (Build); their bars and states are set every frame (Set).
    internal sealed class OrderRows
    {
        private readonly ScrollView view;
        private readonly Label empty;
        private readonly Action<int> cancel;
        private readonly Action<int, bool> labour;
        private readonly Dictionary<int, Row> rows = new Dictionary<int, Row>();

        // An order as its row shows it.
        public struct Line
        {
            public int Id;
            public Texture2D Icon;
            public string Name;
            public string Under;
        }

        private sealed class Row
        {
            public VisualElement Root;
            public VisualElement Fill;
            public VisualElement LabourFill;
            public Label LabourText;
            public Label Status;
            public Button Labour;
        }

        // A bar, with a small text after it when asked for (text: null otherwise).
        private static VisualElement BarLine(out VisualElement fill, out Label text, string fillClass)
        {
            var line = new VisualElement { pickingMode = PickingMode.Ignore };
            line.AddToClassList("job-bar-line");
            var bar = new VisualElement { pickingMode = PickingMode.Ignore };
            bar.AddToClassList("job-bar");
            fill = new VisualElement { pickingMode = PickingMode.Ignore };
            fill.AddToClassList("job-bar-fill");
            if (fillClass != null) fill.AddToClassList(fillClass);
            bar.Add(fill);
            line.Add(bar);
            text = null;
            if (fillClass == null) return line;

            text = new Label { pickingMode = PickingMode.Ignore };
            text.AddToClassList("job-bar-text");
            line.Add(text);
            return line;
        }

        // labour: null for a queue nobody puts calories into.
        public OrderRows(ScrollView view, Label empty, Action<int> cancel, Action<int, bool> labour)
        {
            this.view = view;
            this.empty = empty;
            this.cancel = cancel;
            this.labour = labour;
            view.verticalScrollerVisibility = ScrollerVisibility.Auto;
        }

        public void Build(IReadOnlyList<Line> lines)
        {
            view.Clear();
            rows.Clear();
            empty.style.display = lines.Count == 0 ? DisplayStyle.Flex : DisplayStyle.None;
            foreach (Line line in lines)
            {
                view.Add(MakeRow(line));
            }
        }

        // An order's bar (0 to 1, in the cooking colour or the work's), what it waits for or does, and
        // whether its calories button shows.
        public void Set(int id, float fill, bool hot, string status, bool waiting, bool showLabour, bool active)
        {
            if (!rows.TryGetValue(id, out Row row)) return;

            row.Fill.style.width = Length.Percent(Mathf.Clamp01(fill) * 100f);
            row.Fill.EnableInClassList("job-bar-fill--cooking", hot);
            row.Status.text = status;
            row.Status.EnableInClassList("job-status--waiting", waiting);
            row.Root.EnableInClassList("job-row--cooking", active);
            if (row.Labour != null) row.Labour.style.display = showLabour ? DisplayStyle.Flex : DisplayStyle.None;
        }

        // A queue with calories: the bar of the calories put into an order (0 to 1) and what it reads,
        // shown beside its progress bar, both always.
        public void SetLabour(int id, float fill, string text)
        {
            if (!rows.TryGetValue(id, out Row row) || row.LabourFill == null) return;

            row.LabourFill.style.width = Length.Percent(Mathf.Clamp01(fill) * 100f);
            row.LabourText.text = text;
        }

        private VisualElement MakeRow(Line line)
        {
            var row = new Row { Root = new VisualElement() };
            row.Root.AddToClassList("job-row");
            var picture = new VisualElement { pickingMode = PickingMode.Ignore };
            picture.AddToClassList("job-icon");
            picture.style.backgroundImage = new StyleBackground(line.Icon);
            row.Root.Add(picture);

            var text = new VisualElement { pickingMode = PickingMode.Ignore };
            text.AddToClassList("job-text");
            var name = new Label(line.Name) { pickingMode = PickingMode.Ignore };
            name.AddToClassList("job-name");
            var under = new Label(line.Under) { pickingMode = PickingMode.Ignore };
            under.AddToClassList("job-by");
            text.Add(name);
            text.Add(under);

            // The calories put in (a queue with calories), then how far the item under way has gone.
            if (labour != null)
            {
                VisualElement labourLine = BarLine(out row.LabourFill, out row.LabourText, "job-bar-fill--labour");
                text.Add(labourLine);
            }

            text.Add(BarLine(out row.Fill, out _, null));
            row.Status = new Label { pickingMode = PickingMode.Ignore };
            row.Status.AddToClassList("job-status");
            text.Add(row.Status);
            row.Root.Add(text);

            int id = line.Id;
            if (labour != null)
            {
                row.Labour = new Button { focusable = false, text = Loc.T("+{0} kcal", StorageSync.LabourStep.ToString("0")), tooltip = Loc.T("Shift + click: all it needs") };
                row.Labour.AddToClassList("button");
                row.Labour.AddToClassList("button--primary");
                row.Labour.AddToClassList("job-labour");
                row.Labour.RegisterCallback<ClickEvent>(e => labour(id, e.shiftKey));
                row.Root.Add(row.Labour);
            }

            var cross = new Button { focusable = false, text = "✕", tooltip = Loc.T(labour != null ? "Cancel: the ingredients come back, the calories put in are lost" : "Cancel: nothing was taken yet") };
            cross.AddToClassList("button");
            cross.AddToClassList("button--secondary");
            cross.AddToClassList("job-cancel");
            cross.clicked += () => cancel(id);
            row.Root.Add(cross);

            rows[id] = row;
            return row.Root;
        }
    }
}

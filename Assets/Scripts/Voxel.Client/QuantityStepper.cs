using System;
using UnityEngine.UIElements;

namespace Voxel.Client
{
    // How many to make, on a recipe's sheet (Inventory.uss: .stepper*): "−", the count, which can also
    // be typed (two digits, kept within 1 and the limit), "+" (Shift: ten at a time) and "Max", as many as
    // can be made now (at least one). Built into an empty element. While the count is being typed
    // (IsTyping) its screen leaves the keys alone, as for a search.
    internal sealed class QuantityStepper
    {
        private readonly Label title;
        private readonly TextField field;
        private readonly Button less;
        private readonly Button more;
        private readonly Button max;
        private readonly int limit;
        private readonly Func<int> maxNow;

        public int Value { get; private set; } = 1;

        public bool IsTyping => field.focusController?.focusedElement == field;

        public event Action Changed;

        public QuantityStepper(VisualElement root, int limit, Func<int> maxNow)
        {
            this.limit = limit;
            this.maxNow = maxNow;
            root.AddToClassList("stepper");
            title = new Label { pickingMode = PickingMode.Ignore };
            title.AddToClassList("stepper-title");
            root.Add(title);
            less = MakeButton("−", e => Set(Value - (e.shiftKey ? 10 : 1)));
            root.Add(less);
            field = new TextField { value = "1", maxLength = limit >= 100 ? 3 : 2 };
            field.AddToClassList("stepper-field");
            field.RegisterValueChangedCallback(OnTyped);
            field.RegisterCallback<FocusOutEvent>(_ => field.SetValueWithoutNotify(Value.ToString()));
            field.RegisterCallback<KeyDownEvent>(e =>
            {
                if (e.keyCode == UnityEngine.KeyCode.Return || e.keyCode == UnityEngine.KeyCode.KeypadEnter) field.Blur();
            }, TrickleDown.TrickleDown);
            root.Add(field);
            more = MakeButton("+", e => Set(Value + (e.shiftKey ? 10 : 1)));
            root.Add(more);
            max = MakeButton("", _ => Set(Math.Max(1, this.maxNow())));
            max.AddToClassList("stepper-max");
            root.Add(max);
            Retranslate();
        }

        public void Retranslate()
        {
            title.text = Loc.T("Quantity");
            field.tooltip = Loc.T("Type how many to make");
            less.tooltip = Loc.T("Shift + click: ten less");
            more.tooltip = Loc.T("Shift + click: ten more");
            max.text = Loc.T("Max");
            max.tooltip = Loc.T("As many as can be made now");
        }

        // Leaves the field, if it was being typed in.
        public void Blur() => field.Blur();

        public void Set(int count)
        {
            int clamped = Math.Max(1, Math.Min(limit, count));
            if (!IsTyping) field.SetValueWithoutNotify(clamped.ToString());
            if (clamped == Value) return;

            Value = clamped;
            Changed?.Invoke();
        }

        // Only digits are kept; an empty field counts as 1 until it is left.
        private void OnTyped(ChangeEvent<string> e)
        {
            string digits = "";
            foreach (char c in e.newValue)
            {
                if (char.IsDigit(c)) digits += c;
            }

            if (digits != e.newValue) field.SetValueWithoutNotify(digits);
            Set(int.TryParse(digits, out int typed) ? typed : 1);
        }

        private static Button MakeButton(string text, Action<ClickEvent> clicked)
        {
            var button = new Button { text = text, focusable = false };
            button.AddToClassList("stepper-button");
            button.RegisterCallback<ClickEvent>(e => clicked(e));
            return button;
        }
    }
}

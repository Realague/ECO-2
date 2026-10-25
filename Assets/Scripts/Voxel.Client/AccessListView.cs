using System;
using System.Collections.Generic;
using UnityEngine.UIElements;

namespace Voxel.Client
{
    // An owner's access list on screen (a storage's, their land's): a row per player, whether they are
    // connected, whether they are allowed, and a button to allow or remove them. Its look is the
    // access-* classes of the document's style sheet.
    internal static class AccessListView
    {
        public static void Fill(VisualElement list, VisualElement empty, IReadOnlyList<AccessEntry> entries, Action<string, bool> request)
        {
            list.Clear();
            empty.style.display = entries.Count == 0 ? DisplayStyle.Flex : DisplayStyle.None;
            foreach (AccessEntry entry in entries)
            {
                var row = new VisualElement();
                row.AddToClassList("access-row");
                var dot = new VisualElement();
                dot.AddToClassList("access-dot");
                dot.EnableInClassList("access-dot--online", entry.Online);
                var name = new Label(entry.Name);
                name.AddToClassList("access-name");
                var state = new Label(Loc.T(entry.Allowed ? "Allowed" : entry.Online ? "Not allowed" : "Allowed, away"));
                state.AddToClassList("access-state");
                var toggle = new Button { focusable = false };
                toggle.AddToClassList("button");
                toggle.AddToClassList(entry.Allowed ? "button--danger" : "button--primary");
                toggle.AddToClassList("access-toggle");
                var text = new Label(Loc.T(entry.Allowed ? "Remove" : "Allow"));
                text.AddToClassList("button-text");
                toggle.Add(text);
                string playerName = entry.Name;
                bool allow = !entry.Allowed;
                toggle.clicked += () => request(playerName, allow);
                row.Add(dot);
                row.Add(name);
                row.Add(state);
                row.Add(toggle);
                list.Add(row);
            }
        }
    }
}

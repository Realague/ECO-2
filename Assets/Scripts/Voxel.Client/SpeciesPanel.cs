using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine.UIElements;

namespace Voxel.Client
{
    // The full-screen map's list of layers (Assets/UI/Map.uxml, "species-panel"): three drop-down
    // menus (closed at first), plants, animals and the soil. Each species shows its colour on the
    // map, its name and how many there are in the world, each nutrient (nitrogen, phosphorus,
    // potassium) its colour and its mean level, from the last census. The search field filters them
    // and opens the menus holding a match. A click chooses the layer the map shows; a click on the
    // chosen one clears it.
    internal sealed class SpeciesPanel
    {
        private const string SelectedClass = "species-row--selected";

        private static readonly string[] NutrientNames = { "Nitrogen", "Phosphorus", "Potassium" };

        private sealed class Row
        {
            public SpeciesCensus.Kind Kind;
            public int Species;
            public string Name;
            public string English;
            public Label Label;
            public Button Button;
            public Label Count;
        }

        // A menu, the kind of rows it holds, and whether it was open before a search opened or
        // closed it (given back when the search is cleared).
        private sealed class Group
        {
            public SpeciesCensus.Kind Kind;
            public Foldout Foldout;
            public bool WasOpen;

            // Its title in English, and how many rows it counts (its title's value).
            public string English;
            public int Count;
        }

        private readonly TextField search;
        private readonly List<Row> rows = new List<Row>();
        private readonly List<Group> groups = new List<Group>();
        private bool searching;
        private Row selected;

        public SpeciesPanel(VisualElement root, EcosystemSync ecosystemSync, WorldMap worldMap)
        {
            search = root.Q<TextField>("species-search");
            ScrollView list = root.Q<ScrollView>("species-list");

            IReadOnlyList<SpeciesAsset> plants = ecosystemSync.PlantAssets;
            Foldout plantsGroup = AddGroup(list, SpeciesCensus.Kind.Plant, "Plants ({0})", plants.Count);
            for (int s = 0; s < plants.Count; s++)
            {
                AddRow(plantsGroup, SpeciesCensus.Kind.Plant, s, EcosystemSync.DisplayName(plants[s].name), worldMap.PlantColour(s));
            }

            IReadOnlyList<AnimalSpeciesAsset> animals = ecosystemSync.AnimalAssets;
            Foldout animalsGroup = AddGroup(list, SpeciesCensus.Kind.Animal, "Animals ({0})", animals.Count);
            for (int s = 0; s < animals.Count; s++)
            {
                AddRow(animalsGroup, SpeciesCensus.Kind.Animal, s, EcosystemSync.DisplayName(animals[s].name), worldMap.AnimalColour(s));
            }

            Foldout soilGroup = AddGroup(list, SpeciesCensus.Kind.Soil, "Soil (NPK)", 0);
            for (int n = 0; n < NutrientNames.Length; n++)
            {
                AddRow(soilGroup, SpeciesCensus.Kind.Soil, n, NutrientNames[n], ClimateColours.NutrientColour(n));
            }

            search.RegisterValueChangedCallback(_ => Filter());
        }

        // Writes the menus' titles and the names again, in the player's language (Loc), after it changed.
        public void Retranslate()
        {
            foreach (Group group in groups)
            {
                group.Foldout.text = Loc.T(group.English, group.Count);
            }

            foreach (Row row in rows)
            {
                row.Name = Loc.Name(row.English);
                row.Label.text = row.Name;
            }

            // A search under way matches the new names.
            if (searching) Filter();
        }

        public event Action SelectionChanged;

        public SpeciesCensus.Kind SelectedKind => selected?.Kind ?? SpeciesCensus.Kind.None;

        public int SelectedSpecies => selected?.Species ?? -1;

        public string SelectedName => selected?.Name;

        // What the chosen row shows on its right: a species' count, a nutrient's mean level; or empty.
        public string SelectedCount => selected?.Count.text ?? "";

        // While the search field has the focus, the map's keys type into it instead.
        public bool HasFocus => search.focusController?.focusedElement == search;

        public void Blur() => search.Blur();

        // How many of each species there are, and the soil's mean levels (1 is 100 %), from a census.
        public void ShowCounts(SpeciesCensus census)
        {
            foreach (Row row in rows)
            {
                if (row.Kind == SpeciesCensus.Kind.Soil)
                {
                    row.Count.text = row.Species < census.SoilMeans.Length ? $"{UnityEngine.Mathf.RoundToInt(census.SoilMeans[row.Species] * 100f)} %" : "";
                    continue;
                }

                int[] counts = row.Kind == SpeciesCensus.Kind.Plant ? census.PlantCounts : census.AnimalCounts;
                row.Count.text = row.Species < counts.Length ? Loc.Number(counts[row.Species]) : "";
            }
        }

        // A drop-down menu, closed, whose header is not focusable: the map's keys stay the map's.
        // title: in English, with the count of its rows as {0}.
        private Foldout AddGroup(ScrollView list, SpeciesCensus.Kind kind, string title, int count)
        {
            var foldout = new Foldout { text = Loc.T(title, count), value = false };
            foldout.AddToClassList("species-group");
            foldout.Q<Toggle>().focusable = false;
            list.Add(foldout);
            groups.Add(new Group { Kind = kind, Foldout = foldout, English = title, Count = count });
            return foldout;
        }

        // english: the species' or nutrient's name in English, shown in the player's language.
        private void AddRow(Foldout group, SpeciesCensus.Kind kind, int species, string english, UnityEngine.Color colour)
        {
            string name = Loc.Name(english);
            var row = new Row { Kind = kind, Species = species, Name = name, English = english, Button = new Button { focusable = false } };
            row.Button.AddToClassList("species-row");

            var swatch = new VisualElement();
            swatch.AddToClassList("species-swatch");
            swatch.style.backgroundColor = colour;
            row.Button.Add(swatch);

            var label = new Label(name);
            label.AddToClassList("species-name");
            row.Button.Add(label);
            row.Label = label;

            row.Count = new Label();
            row.Count.AddToClassList("species-count");
            row.Button.Add(row.Count);

            row.Button.clicked += () => Choose(row);
            group.Add(row.Button);
            rows.Add(row);
        }

        private void Choose(Row row)
        {
            selected?.Button.RemoveFromClassList(SelectedClass);
            selected = selected == row ? null : row;
            selected?.Button.AddToClassList(SelectedClass);
            SelectionChanged?.Invoke();
        }

        // Shows the rows whose name holds the search text, and only the menus holding one, open;
        // with no text, every row, and each menu as the player had left it.
        private void Filter()
        {
            string text = (search.value ?? "").Trim();
            bool nowSearching = text.Length > 0;
            foreach (Group group in groups)
            {
                if (nowSearching && !searching) group.WasOpen = group.Foldout.value;

                bool any = false;
                foreach (Row row in rows)
                {
                    if (row.Kind != group.Kind) continue;

                    bool shown = !nowSearching || row.Name.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0;
                    row.Button.style.display = shown ? DisplayStyle.Flex : DisplayStyle.None;
                    any |= shown;
                }

                group.Foldout.style.display = any ? DisplayStyle.Flex : DisplayStyle.None;
                group.Foldout.value = nowSearching ? any : group.WasOpen;
            }

            searching = nowSearching;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine.UIElements;

namespace Voxel.Client
{
    // The full-screen map's list of species (Assets/UI/Map.uxml, "species-panel"): two drop-down
    // menus, plants and animals (closed at first), each species with its colour on the map, its name
    // and how many there are in the world (from the last census), filtered by the search field,
    // which opens the menus holding a match. A click chooses
    // the species whose layer the map shows; a click on the chosen one clears it.
    internal sealed class SpeciesPanel
    {
        private const string SelectedClass = "species-row--selected";

        private sealed class Row
        {
            public SpeciesCensus.Kind Kind;
            public int Species;
            public string Name;
            public Button Button;
            public Label Count;
        }

        private readonly TextField search;
        private readonly List<Row> rows = new List<Row>();
        private readonly Foldout plantsGroup;
        private readonly Foldout animalsGroup;

        // Whether each menu was open before a search opened or closed it, given back when the search is cleared.
        private bool plantsOpen;
        private bool animalsOpen;
        private bool searching;
        private Row selected;

        public SpeciesPanel(VisualElement root, EcosystemSync ecosystemSync, WorldMap worldMap)
        {
            search = root.Q<TextField>("species-search");
            ScrollView list = root.Q<ScrollView>("species-list");

            plantsGroup = Group(list, $"Plants ({ecosystemSync.PlantAssets.Count})");
            IReadOnlyList<SpeciesAsset> plants = ecosystemSync.PlantAssets;
            for (int s = 0; s < plants.Count; s++)
            {
                AddRow(plantsGroup, SpeciesCensus.Kind.Plant, s, EcosystemSync.DisplayName(plants[s].name), worldMap.PlantColour(s));
            }

            animalsGroup = Group(list, $"Animals ({ecosystemSync.AnimalAssets.Count})");
            IReadOnlyList<AnimalSpeciesAsset> animals = ecosystemSync.AnimalAssets;
            for (int s = 0; s < animals.Count; s++)
            {
                AddRow(animalsGroup, SpeciesCensus.Kind.Animal, s, EcosystemSync.DisplayName(animals[s].name), worldMap.AnimalColour(s));
            }

            search.RegisterValueChangedCallback(_ => Filter());
        }

        public event Action SelectionChanged;

        public SpeciesCensus.Kind SelectedKind => selected?.Kind ?? SpeciesCensus.Kind.None;

        public int SelectedSpecies => selected?.Species ?? -1;

        public string SelectedName => selected?.Name;

        // While the search field has the focus, the map's keys type into it instead.
        public bool HasFocus => search.focusController?.focusedElement == search;

        public void Blur() => search.Blur();

        // How many of each species there are, from a census.
        public void ShowCounts(SpeciesCensus census)
        {
            foreach (Row row in rows)
            {
                int[] counts = row.Kind == SpeciesCensus.Kind.Plant ? census.PlantCounts : census.AnimalCounts;
                row.Count.text = row.Species < counts.Length ? counts[row.Species].ToString("N0", CultureInfo.InvariantCulture) : "";
            }
        }

        // The count shown for the chosen species, or empty.
        public string SelectedCount => selected?.Count.text ?? "";

        // A drop-down menu, closed, whose header is not focusable: the map's keys stay the map's.
        private static Foldout Group(ScrollView list, string title)
        {
            var group = new Foldout { text = title, value = false };
            group.AddToClassList("species-group");
            group.Q<Toggle>().focusable = false;
            list.Add(group);
            return group;
        }

        private void AddRow(Foldout group, SpeciesCensus.Kind kind, int species, string name, UnityEngine.Color colour)
        {
            var row = new Row { Kind = kind, Species = species, Name = name, Button = new Button { focusable = false } };
            row.Button.AddToClassList("species-row");

            var swatch = new VisualElement();
            swatch.AddToClassList("species-swatch");
            swatch.style.backgroundColor = colour;
            row.Button.Add(swatch);

            var label = new Label(name);
            label.AddToClassList("species-name");
            row.Button.Add(label);

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

        // Shows the species whose name holds the search text, and only the menus holding one, open;
        // with no text, every species, and each menu as the player had left it.
        private void Filter()
        {
            string text = (search.value ?? "").Trim();
            bool anyPlant = false;
            bool anyAnimal = false;
            foreach (Row row in rows)
            {
                bool shown = text.Length == 0 || row.Name.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0;
                row.Button.style.display = shown ? DisplayStyle.Flex : DisplayStyle.None;
                if (shown && row.Kind == SpeciesCensus.Kind.Plant) anyPlant = true;
                if (shown && row.Kind == SpeciesCensus.Kind.Animal) anyAnimal = true;
            }

            if (text.Length > 0 && !searching)
            {
                plantsOpen = plantsGroup.value;
                animalsOpen = animalsGroup.value;
            }

            searching = text.Length > 0;
            plantsGroup.style.display = anyPlant ? DisplayStyle.Flex : DisplayStyle.None;
            animalsGroup.style.display = anyAnimal ? DisplayStyle.Flex : DisplayStyle.None;
            plantsGroup.value = searching ? anyPlant : plantsOpen;
            animalsGroup.value = searching ? anyAnimal : animalsOpen;
        }
    }
}

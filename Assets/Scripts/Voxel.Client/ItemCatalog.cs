using System.Collections.Generic;
using UnityEngine;
using Voxel.Core;

namespace Voxel.Client
{
    // Kinds of items, as the inventory sorts and filters them.
    internal enum ItemCategory
    {
        Blocks,
        Wood,
        Seeds,
        Food,
        Materials,
        Tools
    }

    // What the screens show of an item: its name, kind, description and icon. Icons are the images
    // Eco > Rebuild item icons writes into Assets/Resources/ItemIcons (ItemIconBuilder), named after
    // the block's type or the species' asset (so they do not depend on the order of species); a
    // missing one is drawn as a plain tile of the item's kind, with a warning.
    internal static class ItemCatalog
    {
        private const string IconFolder = "ItemIcons";

        public static readonly ItemCategory[] Categories = { ItemCategory.Tools, ItemCategory.Blocks, ItemCategory.Wood, ItemCategory.Seeds, ItemCategory.Food, ItemCategory.Materials };

        private static readonly Dictionary<int, Texture2D> Icons = new Dictionary<int, Texture2D>();
        private static readonly Dictionary<ItemCategory, Texture2D> MissingIcons = new Dictionary<ItemCategory, Texture2D>();

        public static ItemCategory CategoryOf(int item)
        {
            if (Items.IsTool(item) || item == Items.Arrow || Items.IsStorage(item) || item == Blocks.Torch) return ItemCategory.Tools;
            if (Items.IsSeed(item)) return ItemCategory.Seeds;
            if (Items.IsProduce(item)) return ItemCategory.Food;
            if (Items.IsWood(item)) return ItemCategory.Wood;
            return Items.IsCarcass(item) || item == Items.Straw ? ItemCategory.Materials : ItemCategory.Blocks;
        }

        public static string CategoryName(ItemCategory category)
        {
            switch (category)
            {
                case ItemCategory.Seeds: return Loc.T("Seeds");
                case ItemCategory.Food: return Loc.T("Food");
                case ItemCategory.Materials: return Loc.T("Materials");
                case ItemCategory.Tools: return Loc.T("Tools");
                case ItemCategory.Wood: return Loc.T("Wood");
                default: return Loc.T("Blocks");
            }
        }

        // Singular, for the details of one item: "Seed", "Food"...
        public static string KindName(int item)
        {
            switch (CategoryOf(item))
            {
                case ItemCategory.Seeds: return Loc.T("Seed");
                case ItemCategory.Food: return Loc.T("Food");
                case ItemCategory.Materials: return Loc.T(Items.IsCarcass(item) ? "Carcass" : "Material");
                case ItemCategory.Tools: return Loc.T(item == Items.Arrow ? "Ammunition" : Items.IsStorage(item) ? "Storage" : item == Blocks.Torch ? "Light" : "Tool");
                case ItemCategory.Wood: return Loc.T("Wood");
                default: return Loc.T("Block");
            }
        }

        // In the player's language (Loc): "Oak seeds", "Graines de chêne". A species' name goes in as
        // {0} (translated) and {1} (the same in lower case).
        public static string NameOf(int item, EcosystemSync ecosystemSync)
        {
            if (Items.IsSeed(item)) return WithPlant("{0} seeds", Items.SeedSpecies(item), ecosystemSync);
            if (Items.IsProduce(item)) return Loc.In("produce", EcosystemSync.DisplayName(ecosystemSync.ProduceName(Items.ProduceSpecies(item))));
            if (Items.IsWood(item)) return WithPlant("{0} wood", Items.WoodSpecies(item), ecosystemSync);
            if (Items.IsCarcass(item)) return WithAnimal("{0} carcass", Items.CarcassSpecies(item), ecosystemSync);
            return Loc.Name(Items.NameOf(item));
        }

        // {0}: the species with its English article ("an oak"), {1}: its translated name in lower case.
        public static string DescriptionOf(int item, EcosystemSync ecosystemSync)
        {
            if (Items.IsSeed(item))
            {
                int species = Items.SeedSpecies(item);
                return ecosystemSync.IsTree(species)
                    ? WithPlant("Plant it to grow {2}: hold it and right click on the ground where it grows. It needs a climate and soil that suit it, and room from others of its kind.", species, ecosystemSync)
                    : WithPlant("Plant it to grow {2}. Only gardeners plant crops, on tilled soil.", species, ecosystemSync);
            }

            if (Items.IsProduce(item))
            {
                int species = Items.ProduceSpecies(item);
                return ecosystemSync.FoodOf(item).IsFood
                    ? WithPlant("Harvested from {1} plants when they are ripe. Right click it here, or hold it and right click, to eat it.", species, ecosystemSync)
                    : WithPlant("Harvested from {1} plants when they are ripe. It cannot be eaten.", species, ecosystemSync);
            }

            switch (item)
            {
                case Items.Shovel: return Loc.T("Digs soft ground: dirt, grass, sand, gravel, clay, snow and mud. Hold it and left click.");
                case Items.Pickaxe: return Loc.T("Breaks hard blocks: rock, ores and ice. Hold it and left click.");
                case Items.Axe: return Loc.T("Fells trees and every plant that gives wood. Hold it and left click.");
                case Items.Scythe: return Loc.T("Cuts grass, reeds and ferns, and harvests cereals and vegetables (right click on a ripe one). Fruit is picked by hand.");
                case Items.Hoe: return Loc.T("Tills grass, dirt, mud or clay (right click): seeds only grow on tilled soil.");
                case Items.Bow: return Loc.T("Hunts animals. Hold the left button to draw, let go to shoot; a shot in the head does double damage.");
                case Items.Arrow: return Loc.T("Shot with the bow. An arrow lies where it lands: right click on it to pick it up.");
                case Items.Chest: return Loc.T("Holds tools, seeds, food and the like, up to 150 kg. Hold it and right click on the ground to place it; right click on it to open it. Only you and the players you allow can open it.");
                case Blocks.Torch: return Loc.T("Lights what is around it, day and night. Hold it and right click on the top or the side of a block to set it there; hit it, with anything in hand, to take it down.");
                case Items.Stockpile: return Loc.T("Holds blocks and wood: nine piles of 27 on a patch of flat ground, 3 by 3. Hold it and right click to place it in front of you; right click on it to open it.");
                case Items.SmallStockpile: return Loc.T("Holds blocks and wood: four piles of 20 on a patch of flat ground, 2 by 2. Hold it and right click to place it in front of you; right click on it to open it.");
            }

            if (Items.IsWood(item)) return WithPlant("From {2} once felled: each log on the ground gives one. Counted with the blocks, and kept on a stockpile.", Items.WoodSpecies(item), ecosystemSync);
            if (Items.IsCarcass(item)) return WithAnimal("The body of {2}, taken from the ground while at least half of it was left. Of no use yet: it will be butchered later.", Items.CarcassSpecies(item), ecosystemSync);
            if (item == Items.Straw) return Loc.T("Left over when wheat is harvested.");
            return Loc.T("A building block. Hold it and right click to place it.");
        }

        // A text about a species, translated: {0} its name, {1} the same in lower case, {2} in lower
        // case with an English article ("an oak"; a translation does with {1}).
        private static string WithPlant(string english, int species, EcosystemSync ecosystemSync) => WithName(english, PlantName(species, ecosystemSync));

        private static string WithAnimal(string english, int species, EcosystemSync ecosystemSync) => WithName(english, AnimalName(species, ecosystemSync));

        private static string WithName(string english, string name) =>
            Loc.T(english, Loc.Name(name), Loc.Lower(name), Article(name.ToLowerInvariant()));

        public static Texture2D IconOf(int item, EcosystemSync ecosystemSync)
        {
            if (Icons.TryGetValue(item, out Texture2D icon) && icon != null) return icon;

            icon = Resources.Load<Texture2D>($"{IconFolder}/{IconName(item, ecosystemSync)}");
            if (icon == null)
            {
                Debug.LogWarning($"No icon for {NameOf(item, ecosystemSync)} ({IconName(item, ecosystemSync)}): run Eco > Rebuild item icons.");
                icon = MissingIcon(CategoryOf(item));
            }

            Icons[item] = icon;
            return icon;
        }

        private static string IconName(int item, EcosystemSync ecosystemSync)
        {
            if (Items.IsSeed(item)) return $"Seed_{ecosystemSync.PlantName(Items.SeedSpecies(item))}";
            if (Items.IsProduce(item)) return $"Produce_{ecosystemSync.PlantName(Items.ProduceSpecies(item))}";
            if (Items.IsWood(item)) return $"Wood_{ecosystemSync.PlantName(Items.WoodSpecies(item))}";
            if (Items.IsCarcass(item)) return $"Carcass_{ecosystemSync.AnimalName(Items.CarcassSpecies(item))}";
            if (item == Items.Straw) return "Straw";
            if (item == Items.Arrow) return "Arrow";
            if (item == Items.Chest) return "Chest";
            if (item == Items.Stockpile) return "Stockpile";
            if (item == Items.SmallStockpile) return "SmallStockpile";
            if (item == Blocks.Torch) return "Torch";
            if (Items.IsTool(item)) return $"Tool_{Items.NameOf(item)}";
            return $"Block_{item}";
        }

        private static string PlantName(int species, EcosystemSync ecosystemSync) => EcosystemSync.DisplayName(ecosystemSync.PlantName(species));

        private static string AnimalName(int species, EcosystemSync ecosystemSync) => EcosystemSync.DisplayName(ecosystemSync.AnimalName(species));

        private static string Article(string noun)
        {
            if (noun.Length == 0) return noun;

            return ("aeiou".IndexOf(noun[0]) >= 0 ? "an " : "a ") + noun;
        }

        // A rounded tile in the kind's colour, for an item without an icon.
        private static Texture2D MissingIcon(ItemCategory category)
        {
            if (MissingIcons.TryGetValue(category, out Texture2D icon) && icon != null) return icon;

            Color colour = category == ItemCategory.Tools ? new Color(0.45f, 0.47f, 0.5f)
                : category == ItemCategory.Seeds ? new Color(0.62f, 0.5f, 0.32f)
                : category == ItemCategory.Food ? new Color(0.78f, 0.36f, 0.3f)
                : category == ItemCategory.Materials ? new Color(0.55f, 0.4f, 0.26f)
                : new Color(0.5f, 0.52f, 0.55f);
            const int size = 32;
            icon = new Texture2D(size, size, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear };
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = Mathf.Max(0f, Mathf.Abs(x + 0.5f - size * 0.5f) - (size * 0.5f - 6f));
                    float dy = Mathf.Max(0f, Mathf.Abs(y + 0.5f - size * 0.5f) - (size * 0.5f - 6f));
                    bool inside = dx * dx + dy * dy <= 36f && x > 2 && y > 2 && x < size - 3 && y < size - 3;
                    icon.SetPixel(x, y, inside ? colour : Color.clear);
                }
            }

            icon.Apply();
            MissingIcons[category] = icon;
            return icon;
        }
    }
}

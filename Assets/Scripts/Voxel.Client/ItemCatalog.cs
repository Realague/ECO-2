using System.Collections.Generic;
using UnityEngine;
using Voxel.Core;

namespace Voxel.Client
{
    // Kinds of items, as the inventory sorts and filters them.
    internal enum ItemCategory
    {
        Blocks,
        Seeds,
        Food,
        Materials
    }

    // What the screens show of an item: its name, kind, description and icon. Icons are the images
    // Eco > Rebuild item icons writes into Assets/Resources/ItemIcons (ItemIconBuilder), named after
    // the block's type or the species' asset (so they do not depend on the order of species); a
    // missing one is drawn as a plain tile of the item's kind, with a warning.
    internal static class ItemCatalog
    {
        private const string IconFolder = "ItemIcons";

        public static readonly ItemCategory[] Categories = { ItemCategory.Blocks, ItemCategory.Seeds, ItemCategory.Food, ItemCategory.Materials };

        private static readonly Dictionary<int, Texture2D> Icons = new Dictionary<int, Texture2D>();
        private static readonly Dictionary<ItemCategory, Texture2D> MissingIcons = new Dictionary<ItemCategory, Texture2D>();

        public static ItemCategory CategoryOf(int item)
        {
            if (Items.IsSeed(item)) return ItemCategory.Seeds;
            if (Items.IsProduce(item)) return ItemCategory.Food;
            return item == Items.Wood || item == Items.Straw ? ItemCategory.Materials : ItemCategory.Blocks;
        }

        public static string CategoryName(ItemCategory category)
        {
            switch (category)
            {
                case ItemCategory.Seeds: return "Seeds";
                case ItemCategory.Food: return "Food";
                case ItemCategory.Materials: return "Materials";
                default: return "Blocks";
            }
        }

        // Singular, for the details of one item: "Seed", "Food"...
        public static string KindName(int item)
        {
            switch (CategoryOf(item))
            {
                case ItemCategory.Seeds: return "Seed";
                case ItemCategory.Food: return "Food";
                case ItemCategory.Materials: return "Material";
                default: return "Block";
            }
        }

        public static string NameOf(int item, EcosystemSync ecosystemSync)
        {
            if (Items.IsSeed(item)) return $"{PlantName(Items.SeedSpecies(item), ecosystemSync)} seeds";
            if (Items.IsProduce(item)) return EcosystemSync.DisplayName(ecosystemSync.ProduceName(Items.ProduceSpecies(item)));
            return Items.NameOf(item);
        }

        public static string DescriptionOf(int item, EcosystemSync ecosystemSync)
        {
            if (Items.IsSeed(item))
            {
                return $"Plant it to grow {Article(PlantName(Items.SeedSpecies(item), ecosystemSync).ToLowerInvariant())}. Only gardeners can plant seeds.";
            }

            if (Items.IsProduce(item))
            {
                string harvested = $"Harvested from {PlantName(Items.ProduceSpecies(item), ecosystemSync).ToLowerInvariant()} plants when they are ripe.";
                return ecosystemSync.FoodOf(item).IsFood ? harvested + " Right click it here, or hold it and right click, to eat it." : harvested + " It cannot be eaten.";
            }

            if (item == Items.Wood) return "Felled from trees and shrubs: the bigger the plant, the more wood.";
            if (item == Items.Straw) return "Left over when wheat is harvested.";
            return "A building block. Hold it and right click to place it.";
        }

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
            if (item == Items.Wood) return "Wood";
            if (item == Items.Straw) return "Straw";
            return $"Block_{item}";
        }

        private static string PlantName(int species, EcosystemSync ecosystemSync) => EcosystemSync.DisplayName(ecosystemSync.PlantName(species));

        private static string Article(string noun)
        {
            if (noun.Length == 0) return noun;

            return ("aeiou".IndexOf(noun[0]) >= 0 ? "an " : "a ") + noun;
        }

        // A rounded tile in the kind's colour, for an item without an icon.
        private static Texture2D MissingIcon(ItemCategory category)
        {
            if (MissingIcons.TryGetValue(category, out Texture2D icon) && icon != null) return icon;

            Color colour = category == ItemCategory.Seeds ? new Color(0.62f, 0.5f, 0.32f)
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

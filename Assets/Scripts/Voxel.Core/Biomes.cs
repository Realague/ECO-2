namespace Voxel.Core
{
    // The name of the kind of land at a column, shown to the player (the place under them): from the
    // water laid out by the landscape, its marshes and bare rock, the block on top (snow) and the
    // climate. Display only, the same on every machine; plants do not count (a client far from them
    // does not know them).
    public static class Biomes
    {
        public const string Sea = "Sea";
        public const string Lake = "Lake";
        public const string River = "River";
        public const string Marsh = "Marsh";
        public const string Snowfield = "Snowfield";
        public const string Mountains = "Mountains";
        public const string Jungle = "Jungle";
        public const string TropicalForest = "Tropical forest";
        public const string Savanna = "Savanna";
        public const string Desert = "Desert";
        public const string Tundra = "Tundra";
        public const string Taiga = "Taiga";
        public const string TemperateForest = "Temperate forest";
        public const string Meadow = "Meadow";

        public static readonly string[] All =
        {
            Sea, Lake, River, Marsh, Snowfield, Mountains, Jungle, TropicalForest, Savanna, Desert, Tundra, Taiga,
            TemperateForest, Meadow
        };

        // Thresholds on Geography's temperature and humidity, from 0 to 1 (the climates the plant
        // species are made for: jungle trees from 0.66 in both, cold-land trees under 0.25).
        private const float Hot = 0.6f;
        private const float Cold = 0.25f;
        private const float JungleWarmth = 0.66f;
        private const float JungleWetness = 0.66f;
        private const float Dry = 0.25f;
        private const float Wet = 0.5f;
        private const float TundraWetness = 0.3f;

        // landscape is the one the world was generated from (Landscape.For), or null: water and
        // marshes are then not told apart, and water is a lake. Empty outside the world.
        public static string NameAt(World world, Landscape landscape, int x, int z)
        {
            if ((uint)x >= (uint)world.SizeX || (uint)z >= (uint)world.SizeZ) return "";

            int top = world.GetTopHeight(x, z);
            ushort block = top > 0 ? Blocks.TypeOf(world.GetBlock(x, top - 1, z)) : Blocks.Air;
            bool water = block == Blocks.Water || block == Blocks.Ice;
            if (landscape != null)
            {
                if (landscape.IsMarsh(x, z)) return Marsh;
                if (water && landscape.IsSea(x, z)) return Sea;
                if (water && landscape.IsRiver(x, z)) return River;
            }

            if (water) return Lake;
            if (block == Blocks.Snow) return Snowfield;
            if (landscape != null && landscape.IsRock(x, z)) return Mountains;

            float warmth = (float)world.Geography.TemperatureAt(x, z) / IntNoise.One;
            float wetness = (float)world.Geography.HumidityAt(x, z) / IntNoise.One;
            if (warmth >= JungleWarmth && wetness >= JungleWetness) return Jungle;
            if (warmth >= Hot)
            {
                if (wetness < Dry) return Desert;
                return wetness < Wet ? Savanna : TropicalForest;
            }

            if (warmth < Cold) return wetness < TundraWetness ? Tundra : Taiga;

            return wetness >= Wet ? TemperateForest : Meadow;
        }
    }
}

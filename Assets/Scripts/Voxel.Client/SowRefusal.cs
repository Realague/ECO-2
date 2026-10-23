using Eco.Sim;

namespace Voxel.Client
{
    // Why a seed a player sowed did not sprout, as told to them: the simulation's reasons
    // (PlantRefusal, same values), a crop sown on soil that is not tilled, or sown by someone who is
    // not a gardener. None: nothing to say (it sprouted, or the request made no sense).
    public enum SowRefusal : byte
    {
        None = (byte)PlantRefusal.None,
        Occupied = (byte)PlantRefusal.Occupied,
        Ground = (byte)PlantRefusal.Ground,
        TooCold = (byte)PlantRefusal.TooCold,
        TooHot = (byte)PlantRefusal.TooHot,
        TooDry = (byte)PlantRefusal.TooDry,
        TooWet = (byte)PlantRefusal.TooWet,
        PoorSoil = (byte)PlantRefusal.PoorSoil,
        TooClose = (byte)PlantRefusal.TooClose,
        NeedsWater = (byte)PlantRefusal.NeedsWater,
        NeedsShade = (byte)PlantRefusal.NeedsShade,
        NotTilled = 100,
        NotGardener = 101
    }

    internal static class SowRefusals
    {
        // What the player reads, plantName in lower case ("oak", "apple tree").
        public static string TextOf(SowRefusal refusal, string plantName)
        {
            string a = Article(plantName);
            switch (refusal)
            {
                case SowRefusal.Occupied: return "Something already stands there";
                case SowRefusal.Ground: return $"{Capital(a)} can't grow on this ground";
                case SowRefusal.TooCold: return $"Too cold for {a}";
                case SowRefusal.TooHot: return $"Too hot for {a}";
                case SowRefusal.TooDry: return $"Too dry for {a}";
                case SowRefusal.TooWet: return $"Too wet for {a}";
                case SowRefusal.PoorSoil: return $"The soil is too poor for {a}";
                case SowRefusal.TooClose: return $"Too close to another {plantName}";
                case SowRefusal.NeedsWater: return $"{Capital(a)} needs water beside it";
                case SowRefusal.NeedsShade: return $"{Capital(a)} needs shade";
                case SowRefusal.NotTilled: return "Crops only grow on tilled soil: use a hoe";
                case SowRefusal.NotGardener: return "Only gardeners can plant crops";
                default: return null;
            }
        }

        private static string Article(string name) => (name.Length > 0 && "aeiou".IndexOf(name[0]) >= 0 ? "an " : "a ") + name;

        private static string Capital(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text.Substring(1);
    }
}

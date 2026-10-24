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
        // What the player reads, in their language (Loc), plantName the species' English name ("Oak",
        // "Apple tree"). In the texts: {0} the translated name, {1} in lower case, {2} the English
        // with its article ("an oak"), {3} the same capitalised.
        public static string TextOf(SowRefusal refusal, string plantName)
        {
            switch (refusal)
            {
                case SowRefusal.Occupied: return Loc.T("Something already stands there");
                case SowRefusal.Ground: return Named("{3} can't grow on this ground", plantName);
                case SowRefusal.TooCold: return Named("Too cold for {2}", plantName);
                case SowRefusal.TooHot: return Named("Too hot for {2}", plantName);
                case SowRefusal.TooDry: return Named("Too dry for {2}", plantName);
                case SowRefusal.TooWet: return Named("Too wet for {2}", plantName);
                case SowRefusal.PoorSoil: return Named("The soil is too poor for {2}", plantName);
                case SowRefusal.TooClose: return Named("Too close to another {1}", plantName);
                case SowRefusal.NeedsWater: return Named("{3} needs water beside it", plantName);
                case SowRefusal.NeedsShade: return Named("{3} needs shade", plantName);
                case SowRefusal.NotTilled: return Loc.T("Crops only grow on tilled soil: use a hoe");
                case SowRefusal.NotGardener: return Loc.T("Only gardeners can plant crops");
                default: return null;
            }
        }

        private static string Named(string english, string plantName)
        {
            string a = Article(plantName.ToLowerInvariant());
            return Loc.T(english, Loc.Name(plantName), Loc.Lower(plantName), a, Capital(a));
        }

        private static string Article(string name) => (name.Length > 0 && "aeiou".IndexOf(name[0]) >= 0 ? "an " : "a ") + name;

        private static string Capital(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text.Substring(1);
    }
}

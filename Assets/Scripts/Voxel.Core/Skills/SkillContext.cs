namespace Voxel.Core
{
    // What an action is, for the skills: a path of words joined by colons, from the general to the
    // particular ("fell:oak", "fell:oak:prune", "dig:stump:oak", "craft:Axe"). A bonus, an unlock or
    // an XP rule names a context; it applies to an action whose context is the same or lies under it,
    // so "fell" covers every felling, pruning and bucking of every tree.
    public static class SkillContext
    {
        public const char Separator = ':';

        // Felling a tree (fell:<species>), then pruning its branches (fell:<species>:prune) and
        // bucking its trunk into logs (fell:<species>:buck).
        public const string Fell = "fell";
        public const string Prune = "prune";
        public const string Buck = "buck";

        // Digging with a shovel: a stump (dig:stump:<species>).
        public const string Dig = "dig";
        public const string Stump = "stump";

        // Other hits: a rock or an ore broken with a pickaxe (mine:<block>), soft ground with a shovel
        // (dig:<block>), a plant cut with a scythe (cut:<species>), a construction block taken back
        // with the hammer (build:<block>), a torch taken down by hand (take:<block>).
        public const string Mine = "mine";
        public const string Cut = "cut";
        public const string Build = "build";
        public const string Take = "take";

        // Crafting a recipe (craft:<recipe key>, Recipes) or one of a category (craft:category:<name>).
        public const string Craft = "craft";
        public const string Category = "category";

        // What weighs: wood (wood:<species>), for the weight a player carries.
        public const string Wood = "wood";

        // Placing a block (place:<block type>) and tilling (till).
        public const string Place = "place";
        public const string Till = "till";

        public static string Of(string a, string b) => a + Separator + b;

        public static string Of(string a, string b, string c) => a + Separator + b + Separator + c;

        // The context of a hit on a block: by the tool it takes and the block's type.
        public static string OfBlockHit(ushort block)
        {
            string name = Items.NameOf(Blocks.TypeOf(block));
            switch (ToolRules.ToBreak(block))
            {
                case Items.Pickaxe: return Of(Mine, name);
                case Items.Shovel: return Of(Dig, name);
                case Items.Hammer: return Of(Build, name);
                default: return Of(Take, name);
            }
        }

        // Whether a rule naming pattern applies to an action of this context. An empty pattern applies
        // to nothing.
        public static bool Matches(string pattern, string action)
        {
            if (string.IsNullOrEmpty(pattern) || string.IsNullOrEmpty(action)) return false;
            if (!action.StartsWith(pattern, System.StringComparison.Ordinal)) return false;
            return action.Length == pattern.Length || action[pattern.Length] == Separator;
        }
    }
}

namespace Voxel.Core
{
    // The interactions skills give (SkillBonusKind.Interaction): what the game checks for by name.
    public static class SkillInteractions
    {
        // Felling: a tree felled by the holder lies down already bucked into logs (once its branches
        // are off), already pruned, and leaves no stump.
        public const string AutoBuck = "felling.autoBuck";
        public const string AutoPrune = "felling.autoPrune";
        public const string AutoStump = "felling.autoStump";
    }
}

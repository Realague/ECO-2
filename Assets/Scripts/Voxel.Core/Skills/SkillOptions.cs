namespace Voxel.Core
{
    // How much of the skills a server shows its players (its configuration: -skills-showall,
    // -skills-showtiers, -skills-details, or the host's own settings).
    public struct SkillOptions
    {
        // Every skill, even those the player has not unlocked yet.
        public bool ShowAllSkills;

        // Every tier, even those after the one the player has reached.
        public bool ShowAllTiers;

        // Each upgrade's technical description (its numbers and contexts).
        public bool ShowDetails;

        public byte ToByte() => (byte)((ShowAllSkills ? 1 : 0) | (ShowAllTiers ? 2 : 0) | (ShowDetails ? 4 : 0));

        public static SkillOptions FromByte(byte flags) => new SkillOptions
        {
            ShowAllSkills = (flags & 1) != 0,
            ShowAllTiers = (flags & 2) != 0,
            ShowDetails = (flags & 4) != 0
        };
    }
}

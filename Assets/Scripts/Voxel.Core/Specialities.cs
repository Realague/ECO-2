using System.Collections.Generic;

namespace Voxel.Core
{
    // A player has a single speciality, chosen once and for all: this is what makes players
    // useful to one another. Carpenter and Mason were the crafting specialities: crafting is gone,
    // so they are no longer offered, and a player who had one chooses again (PlayerStore).
    public enum Speciality : byte
    {
        None = 0,
        Carpenter = 1,
        Mason = 2,
        Gardener = 3
    }

    public static class Specialities
    {
        public static readonly IReadOnlyList<Speciality> Choices = new[] { Speciality.Gardener };

        public static bool IsChoice(Speciality speciality) => speciality == Speciality.Gardener;

        // A speciality of an earlier version that no longer exists.
        public static bool IsRetired(Speciality speciality) => speciality == Speciality.Carpenter || speciality == Speciality.Mason;

        public static string NameOf(Speciality speciality)
        {
            switch (speciality)
            {
                case Speciality.Gardener: return "Gardener";
                default: return "None";
            }
        }

        public static string DescriptionOf(Speciality speciality)
        {
            switch (speciality)
            {
                case Speciality.Gardener: return "replants the seeds of trees and bushes";
                default: return "";
            }
        }
    }
}

using System;
using System.Collections.Generic;

namespace Voxel.Core
{
    public enum Sex : byte
    {
        Male = 0,
        Female = 1
    }

    public enum HairStyle : byte
    {
        Short = 0,
        Buzz = 1,
        Long = 2,
        Ponytail = 3,
        Bun = 4,
        Bald = 5
    }

    public enum FacialHair : byte
    {
        None = 0,
        Stubble = 1,
        Moustache = 2,
        Beard = 3
    }

    // How a player looks, chosen at the first connection and kept by the server with the player's
    // state. It travels and is saved as one 16-bit code; 0 means "not chosen yet".
    public readonly struct Appearance : IEquatable<Appearance>
    {
        public const int SkinToneCount = 6;
        public const int HairStyleCount = 6;
        public const int FacialHairCount = 4;
        public const ushort NotChosen = 0;

        // Colour names, by index. Index 0 of each is the colour every player had before the colours
        // could be chosen, so that appearances saved then (with zero in these bits) look the same.
        public static readonly IReadOnlyList<string> EyeColorNames = new[] { "Green", "Brown", "Hazel", "Blue", "Grey", "Dark" };
        public static readonly IReadOnlyList<string> HairColorNames = new[] { "Brown", "Black", "Dark brown", "Auburn", "Ginger", "Blond", "Grey" };

        // Bit 15 marks a chosen appearance, so that no valid code is 0. Below it, from bit 0: skin
        // tone (3 bits), sex (1), hair style (3), eye colour (3), hair colour (3), facial hair (2).
        // Every bit is now used: a new choice needs a wider code (a new players.dat format and a
        // wider NetworkVariable in PlayerModel).
        private const int ChosenBit = 1 << 15;
        private const int FieldBits = 3;
        private const int FieldMask = (1 << FieldBits) - 1;
        private const int SexShift = FieldBits;
        private const int HairShift = SexShift + 1;
        private const int EyeColorShift = HairShift + FieldBits;
        private const int HairColorShift = EyeColorShift + FieldBits;
        private const int FacialHairShift = HairColorShift + FieldBits;
        private const int FacialHairMask = 3;

        public static readonly Appearance Default = new Appearance(1, Sex.Male, HairStyle.Short);

        public Appearance(int skinTone, Sex sex, HairStyle hair, int eyeColor = 0, int hairColor = 0, FacialHair facialHair = FacialHair.None)
        {
            SkinTone = skinTone;
            Sex = sex;
            Hair = hair;
            EyeColor = eyeColor;
            HairColor = hairColor;
            FacialHair = facialHair;
        }

        public static int EyeColorCount => EyeColorNames.Count;

        public static int HairColorCount => HairColorNames.Count;

        // From lightest (0) to darkest (SkinToneCount - 1).
        public int SkinTone { get; }

        public Sex Sex { get; }

        public HairStyle Hair { get; }

        // Index into EyeColorNames.
        public int EyeColor { get; }

        // Index into HairColorNames.
        public int HairColor { get; }

        // Open to either sex; it takes the hair colour.
        public FacialHair FacialHair { get; }

        public bool IsValid =>
            SkinTone >= 0 && SkinTone < SkinToneCount
            && (Sex == Sex.Male || Sex == Sex.Female)
            && (int)Hair >= 0 && (int)Hair < HairStyleCount
            && EyeColor >= 0 && EyeColor < EyeColorCount
            && HairColor >= 0 && HairColor < HairColorCount
            && (int)FacialHair >= 0 && (int)FacialHair < FacialHairCount;

        public ushort Encode() => (ushort)(ChosenBit
            | SkinTone
            | (int)Sex << SexShift
            | (int)Hair << HairShift
            | EyeColor << EyeColorShift
            | HairColor << HairColorShift
            | (int)FacialHair << FacialHairShift);

        // The code may come from the network or from a damaged file: anything that is not exactly
        // a valid appearance is refused, NotChosen included.
        public static bool TryDecode(ushort code, out Appearance appearance)
        {
            appearance = default;
            if ((code & ChosenBit) == 0) return false;

            appearance = new Appearance(
                code & FieldMask,
                (Sex)((code >> SexShift) & 1),
                (HairStyle)((code >> HairShift) & FieldMask),
                (code >> EyeColorShift) & FieldMask,
                (code >> HairColorShift) & FieldMask,
                (FacialHair)((code >> FacialHairShift) & FacialHairMask));
            return appearance.IsValid;
        }

        // The same appearance with one choice changed, by index, wrapping around at both ends: what a
        // menu needs to step through the options (index - 1 from the first option gives the last).
        public Appearance WithSkinTone(int skinTone) => new Appearance(Wrap(skinTone, SkinToneCount), Sex, Hair, EyeColor, HairColor, FacialHair);

        public Appearance WithSex(int sex) => new Appearance(SkinTone, (Sex)Wrap(sex, 2), Hair, EyeColor, HairColor, FacialHair);

        public Appearance WithHair(int hair) => new Appearance(SkinTone, Sex, (HairStyle)Wrap(hair, HairStyleCount), EyeColor, HairColor, FacialHair);

        public Appearance WithEyeColor(int eyeColor) => new Appearance(SkinTone, Sex, Hair, Wrap(eyeColor, EyeColorCount), HairColor, FacialHair);

        public Appearance WithHairColor(int hairColor) => new Appearance(SkinTone, Sex, Hair, EyeColor, Wrap(hairColor, HairColorCount), FacialHair);

        public Appearance WithFacialHair(int facialHair) =>
            new Appearance(SkinTone, Sex, Hair, EyeColor, HairColor, (FacialHair)Wrap(facialHair, FacialHairCount));

        public bool Equals(Appearance other) =>
            SkinTone == other.SkinTone && Sex == other.Sex && Hair == other.Hair
            && EyeColor == other.EyeColor && HairColor == other.HairColor && FacialHair == other.FacialHair;

        public override bool Equals(object obj) => obj is Appearance other && Equals(other);

        public override int GetHashCode() => Encode();

        public override string ToString() => $"skin {SkinTone}, {Sex}, {Hair}, eyes {EyeColor}, hair colour {HairColor}, {FacialHair}";

        public static string NameOf(Sex sex) => sex == Sex.Female ? "Female" : "Male";

        public static string NameOf(FacialHair facialHair)
        {
            switch (facialHair)
            {
                case FacialHair.Stubble: return "Stubble";
                case FacialHair.Moustache: return "Moustache";
                case FacialHair.Beard: return "Full beard";
                default: return "None";
            }
        }

        public static string NameOf(HairStyle hair)
        {
            switch (hair)
            {
                case HairStyle.Short: return "Short";
                case HairStyle.Buzz: return "Buzz cut";
                case HairStyle.Long: return "Long";
                case HairStyle.Ponytail: return "Ponytail";
                case HairStyle.Bun: return "Bun";
                default: return "Bald";
            }
        }

        private static int Wrap(int value, int count) => (value % count + count) % count;
    }
}

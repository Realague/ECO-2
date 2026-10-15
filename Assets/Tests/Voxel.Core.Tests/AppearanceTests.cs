using NUnit.Framework;

namespace Voxel.Core.Tests
{
    public class AppearanceTests
    {
        [Test]
        public void EveryAppearance_SurvivesEncodingAndHasItsOwnCode()
        {
            var codes = new System.Collections.Generic.HashSet<ushort>();
            for (int tone = 0; tone < Appearance.SkinToneCount; tone++)
            {
                foreach (Sex sex in new[] { Sex.Male, Sex.Female })
                {
                    for (int hair = 0; hair < Appearance.HairStyleCount; hair++)
                    {
                        for (int eyes = 0; eyes < Appearance.EyeColorCount; eyes++)
                        {
                            for (int hairColor = 0; hairColor < Appearance.HairColorCount; hairColor++)
                            {
                                for (int facial = 0; facial < Appearance.FacialHairCount; facial++)
                                {
                                    var appearance = new Appearance(tone, sex, (HairStyle)hair, eyes, hairColor, (FacialHair)facial);
                                    ushort code = appearance.Encode();

                                    Assert.IsTrue(Appearance.TryDecode(code, out Appearance decoded), appearance.ToString());
                                    Assert.AreEqual(appearance, decoded);
                                    Assert.AreNotEqual(Appearance.NotChosen, code);
                                    Assert.IsTrue(codes.Add(code), "two appearances share code " + code);
                                }
                            }
                        }
                    }
                }
            }
        }

        // Appearances saved before the colours could be chosen have zero in their bits: they keep the
        // colours everyone had then, which are index 0.
        [Test]
        public void CodeFromBeforeTheColours_DecodesWithTheFirstColours()
        {
            const ushort savedBeforeColours = 0x8000 | 4 | 1 << 3 | 3 << 4;

            Assert.IsTrue(Appearance.TryDecode(savedBeforeColours, out Appearance appearance));

            Assert.AreEqual(new Appearance(4, Sex.Female, HairStyle.Ponytail, 0, 0), appearance);
            Assert.AreEqual(FacialHair.None, appearance.FacialHair);
        }

        // Bits 13 and 14 carry the facial hair.
        [TestCase((ushort)0x8000, FacialHair.None)]
        [TestCase((ushort)0xA000, FacialHair.Stubble)]
        [TestCase((ushort)0xC000, FacialHair.Moustache)]
        [TestCase((ushort)0xE000, FacialHair.Beard)]
        public void TryDecode_ReadsTheFacialHair(ushort code, FacialHair expected)
        {
            Assert.IsTrue(Appearance.TryDecode(code, out Appearance appearance));
            Assert.AreEqual(expected, appearance.FacialHair);
        }

        [TestCase((ushort)0)]
        [TestCase((ushort)0x0001)]
        [TestCase((ushort)0x6000)]
        [TestCase((ushort)0x8006)]
        [TestCase((ushort)0x8060)]
        [TestCase((ushort)0x8070)]
        [TestCase((ushort)0x8300)]
        [TestCase((ushort)0x9C00)]
        [TestCase((ushort)0xFFFF)]
        public void TryDecode_CodeThatIsNotAValidAppearance_Fails(ushort code)
        {
            Assert.IsFalse(Appearance.TryDecode(code, out _));
        }

        [TestCase(-1, Sex.Male, HairStyle.Short, 0, 0, FacialHair.None)]
        [TestCase(6, Sex.Male, HairStyle.Short, 0, 0, FacialHair.None)]
        [TestCase(0, (Sex)2, HairStyle.Short, 0, 0, FacialHair.None)]
        [TestCase(0, Sex.Female, (HairStyle)6, 0, 0, FacialHair.None)]
        [TestCase(0, Sex.Female, HairStyle.Short, 6, 0, FacialHair.None)]
        [TestCase(0, Sex.Female, HairStyle.Short, -1, 0, FacialHair.None)]
        [TestCase(0, Sex.Female, HairStyle.Short, 0, 7, FacialHair.None)]
        [TestCase(0, Sex.Female, HairStyle.Short, 0, 0, (FacialHair)4)]
        public void IsValid_OutOfRangeChoice_IsFalse(int tone, Sex sex, HairStyle hair, int eyes, int hairColor, FacialHair facialHair)
        {
            Assert.IsFalse(new Appearance(tone, sex, hair, eyes, hairColor, facialHair).IsValid);
        }

        [Test]
        public void Default_IsValid()
        {
            Assert.IsTrue(Appearance.Default.IsValid);
        }

        [Test]
        public void With_WrapsAroundAtBothEnds()
        {
            Appearance appearance = Appearance.Default;

            Assert.AreEqual(Appearance.SkinToneCount - 1, appearance.WithSkinTone(-1).SkinTone);
            Assert.AreEqual(0, appearance.WithSkinTone(Appearance.SkinToneCount).SkinTone);
            Assert.AreEqual(Sex.Female, appearance.WithSex((int)Sex.Male - 1).Sex);
            Assert.AreEqual(Sex.Male, appearance.WithSex((int)Sex.Female + 1).Sex);
            Assert.AreEqual(HairStyle.Short, appearance.WithHair(Appearance.HairStyleCount).Hair);
            Assert.AreEqual(HairStyle.Bald, appearance.WithHair((int)HairStyle.Short - 1).Hair);
            Assert.AreEqual(Appearance.EyeColorCount - 1, appearance.WithEyeColor(-1).EyeColor);
            Assert.AreEqual(0, appearance.WithHairColor(Appearance.HairColorCount).HairColor);
            Assert.AreEqual(FacialHair.Beard, appearance.WithFacialHair((int)FacialHair.None - 1).FacialHair);
            Assert.AreEqual(FacialHair.None, appearance.WithFacialHair(Appearance.FacialHairCount).FacialHair);
        }

        [Test]
        public void With_KeepsTheOtherChoices()
        {
            var appearance = new Appearance(3, Sex.Female, HairStyle.Bun, 4, 5, FacialHair.Moustache);

            Appearance changed = appearance.WithSkinTone(0).WithSex(0).WithHair(0).WithEyeColor(0).WithHairColor(0);

            Assert.AreEqual(FacialHair.Moustache, changed.FacialHair);
            Assert.AreEqual(4, appearance.WithFacialHair(0).EyeColor);
            Assert.AreEqual(5, appearance.WithFacialHair(0).HairColor);
            Assert.AreEqual(appearance, appearance.WithFacialHair((int)FacialHair.Moustache).WithSkinTone(3));
        }
    }
}

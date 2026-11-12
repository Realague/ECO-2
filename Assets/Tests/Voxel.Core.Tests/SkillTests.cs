using System.IO;
using NUnit.Framework;

namespace Voxel.Core.Tests
{
    public class SkillTests
    {
        // A skill shaped like Felling: 4 points a tier, three single upgrades at tier 1, one in five
        // steps (step n at tier n), one with options, a chained one and a hidden one.
        private static SkillDefinition Felling()
        {
            var tiers = new TierDefinition[SkillDefinition.TierCount];
            float[] quotas = { 100f, 250f, 500f, 1000f, 2000f };
            for (int t = 0; t < tiers.Length; t++) tiers[t] = new TierDefinition { Name = "T" + (t + 1), XpQuota = quotas[t], Upgrades = new UpgradeDefinition[0] };

            var steps = new UpgradeStep[5];
            for (int s = 0; s < steps.Length; s++)
            {
                steps[s] = new UpgradeStep { Tier = s + 1, Bonuses = new[] { new SkillBonus { Kind = SkillBonusKind.TimePercent, Context = "fell", Value = 5 * (s + 1) } } };
            }

            tiers[0].Upgrades = new[]
            {
                Single("autoBuck", SkillBonusKind.Interaction, SkillInteractions.AutoBuck, 0),
                Single("autoPrune", SkillBonusKind.Interaction, SkillInteractions.AutoPrune, 0),
                new UpgradeDefinition { Id = "speed", Steps = steps },
                new UpgradeDefinition
                {
                    Id = "style",
                    Choices = new[]
                    {
                        new UpgradeChoice { Id = "a", Bonuses = new[] { new SkillBonus { Kind = SkillBonusKind.YieldPercent, Context = "fell", Value = 10 } } },
                        new UpgradeChoice { Id = "b", Bonuses = new[] { new SkillBonus { Kind = SkillBonusKind.CaloriePercent, Context = "fell", Value = 10 } } },
                        new UpgradeChoice { Id = "c" }
                    },
                    MaxChoices = 2
                },
                new UpgradeDefinition { Id = "expert", Hidden = true }
            };

            tiers[1].Upgrades = new[]
            {
                new UpgradeDefinition
                {
                    Id = "yield",
                    Requires = new[] { "autoBuck" },
                    Steps = new[] { new UpgradeStep { Bonuses = new[] { new SkillBonus { Kind = SkillBonusKind.YieldPercent, Context = "fell:Oak", Formula = "10 * tier" } } } }
                },
                new UpgradeDefinition
                {
                    Id = "betterYield",
                    Replaces = "yield",
                    Steps = new[] { new UpgradeStep { Bonuses = new[] { new SkillBonus { Kind = SkillBonusKind.YieldPercent, Context = "fell", Value = 50 } } } }
                }
            };

            return new SkillDefinition
            {
                Id = "felling",
                Name = "Felling",
                Unlocks = new[] { new SkillUnlock { Context = "fell", Tool = Items.Axe } },
                XpContexts = new[] { "fell" },
                PointsPerTier = 4,
                Tiers = tiers
            }.Seal();
        }

        private static UpgradeDefinition Single(string id, SkillBonusKind kind, string context, double value) => new UpgradeDefinition
        {
            Id = id,
            Steps = new[] { new UpgradeStep { Bonuses = new[] { new SkillBonus { Kind = kind, Context = context, Value = value } } } }
        };

        private static SkillCatalog CatalogOf(SkillDefinition skill) => new SkillCatalog(new[] { skill });

        [Test]
        public void Contexts_MatchThemselvesAndWhatLiesUnder()
        {
            Assert.IsTrue(SkillContext.Matches("fell", "fell"));
            Assert.IsTrue(SkillContext.Matches("fell", "fell:Oak:prune"));
            Assert.IsTrue(SkillContext.Matches("fell:Oak", "fell:Oak:buck"));
            Assert.IsFalse(SkillContext.Matches("fell", "felling"));
            Assert.IsFalse(SkillContext.Matches("fell:Oak", "fell:Birch"));
            Assert.IsFalse(SkillContext.Matches("", "fell"));
            Assert.AreEqual("mine:Granite", SkillContext.OfBlockHit(Blocks.Stone));
            Assert.AreEqual("dig:Dirt", SkillContext.OfBlockHit(Blocks.Dirt));
        }

        [Test]
        public void ASkill_UnlocksWithItsTool_AndLearnsFromCalories()
        {
            SkillDefinition skill = Felling();
            SkillCatalog catalog = CatalogOf(skill);
            var state = new SkillState();

            Assert.IsFalse(SkillRules.Practise(catalog, state, "fell:Oak", 10f, Items.Shovel), "the wrong tool unlocks nothing");
            Assert.IsFalse(SkillRules.Practise(catalog, state, "mine:Granite", 10f, Items.Axe));
            Assert.IsTrue(SkillRules.Practise(catalog, state, "fell:Oak", 10f, Items.Axe));

            state.TryGet("felling", out SkillProgress progress);
            Assert.IsTrue(progress.Unlocked);
            Assert.AreEqual(10f, progress.Xp, 1e-4f, "the unlocking hit counts already");

            SkillRules.Practise(catalog, state, "fell:Oak:prune", 5f, Items.Axe);
            SkillRules.Practise(catalog, state, "dig:stump:Oak", 50f, Items.Shovel);
            Assert.AreEqual(15f, progress.Xp, 1e-4f, "only its own actions teach it");
        }

        [Test]
        public void Points_ComeAQuarterAtATime_AndTiersOpenByQuota()
        {
            SkillDefinition skill = Felling();
            var progress = new SkillProgress { Unlocked = true };

            Assert.AreEqual(1, SkillRules.OpenTiers(skill, progress));
            Assert.AreEqual(0, SkillRules.PointsEarned(skill, progress));

            progress.Xp = 24.9f;
            Assert.AreEqual(0, SkillRules.PointsEarned(skill, progress));
            progress.Xp = 25f;
            Assert.AreEqual(1, SkillRules.PointsEarned(skill, progress), "a quarter of 100 kcal: one point of four");
            progress.Xp = 99f;
            Assert.AreEqual(3, SkillRules.PointsEarned(skill, progress));
            Assert.AreEqual(1, SkillRules.OpenTiers(skill, progress));

            progress.Xp = 100f;
            Assert.AreEqual(2, SkillRules.OpenTiers(skill, progress));
            Assert.AreEqual(4, SkillRules.PointsEarned(skill, progress));
            progress.Xp = 100f + 125f;
            Assert.AreEqual(6, SkillRules.PointsEarned(skill, progress), "half of tier 2");

            progress.Xp = skill.MaxXp;
            Assert.AreEqual(5, SkillRules.OpenTiers(skill, progress));
            Assert.AreEqual(20, SkillRules.PointsEarned(skill, progress), "20 points in all");
            Assert.AreEqual(0, SkillRules.OpenTiers(skill, new SkillProgress()), "a locked skill has no tier");
        }

        [Test]
        public void StagedUpgrades_TakeOneStepPerTier()
        {
            SkillDefinition skill = Felling();
            var progress = new SkillProgress { Unlocked = true, Xp = 50f };

            Assert.AreEqual(SkillRefusal.None, SkillRules.Buy(skill, progress, "speed"));
            Assert.AreEqual(SkillRefusal.TierClosed, SkillRules.CanBuy(skill, progress, "speed"), "step 2 needs tier 2");

            progress.Xp = 100f + 250f;
            Assert.AreEqual(SkillRefusal.None, SkillRules.Buy(skill, progress, "speed"));
            Assert.AreEqual(2, progress.Of("speed").Steps);
            Assert.AreEqual(2, SkillRules.PointsSpent(skill, progress));
        }

        [Test]
        public void Buying_IsRefusedWithoutPoints_OrForAHiddenOrUnknownUpgrade()
        {
            SkillDefinition skill = Felling();
            var progress = new SkillProgress { Unlocked = true, Xp = 25f };

            Assert.AreEqual(SkillRefusal.None, SkillRules.Buy(skill, progress, "autoPrune"));
            Assert.AreEqual(SkillRefusal.Complete, SkillRules.CanBuy(skill, progress, "autoPrune"));
            Assert.AreEqual(SkillRefusal.NotEnoughPoints, SkillRules.CanBuy(skill, progress, "autoBuck"));
            Assert.AreEqual(SkillRefusal.Hidden, SkillRules.CanBuy(skill, progress, "expert"));
            Assert.AreEqual(SkillRefusal.UnknownUpgrade, SkillRules.CanBuy(skill, progress, "nothing"));
            Assert.AreEqual(SkillRefusal.SkillLocked, SkillRules.CanBuy(skill, new SkillProgress(), "autoBuck"));
        }

        [Test]
        public void Requirements_AndChains_AreChecked()
        {
            SkillDefinition skill = Felling();
            var progress = new SkillProgress { Unlocked = true, Xp = skill.MaxXp };

            Assert.AreEqual(SkillRefusal.RequiresMissing, SkillRules.CanBuy(skill, progress, "yield"));
            Assert.AreEqual(SkillRefusal.ReplacesMissing, SkillRules.CanBuy(skill, progress, "betterYield"));
            Assert.AreEqual(SkillRefusal.None, SkillRules.Buy(skill, progress, "autoBuck"));
            Assert.AreEqual(SkillRefusal.None, SkillRules.Buy(skill, progress, "yield"));
            Assert.AreEqual(SkillRefusal.None, SkillRules.Buy(skill, progress, "betterYield"));
        }

        [Test]
        public void Choices_AreBoughtOneAtATime_UpToTheirLimit()
        {
            SkillDefinition skill = Felling();
            var progress = new SkillProgress { Unlocked = true, Xp = skill.MaxXp };

            Assert.AreEqual(SkillRefusal.NoChoice, SkillRules.CanBuy(skill, progress, "style"));
            Assert.AreEqual(SkillRefusal.None, SkillRules.Buy(skill, progress, "style", 1));
            Assert.AreEqual(SkillRefusal.ChoiceTaken, SkillRules.CanBuy(skill, progress, "style", 1));
            Assert.AreEqual(SkillRefusal.None, SkillRules.Buy(skill, progress, "style", 0));
            Assert.AreEqual(SkillRefusal.Complete, SkillRules.CanBuy(skill, progress, "style", 2), "two options at most");
            Assert.IsTrue(SkillRules.IsComplete(skill.Tiers[0].Upgrades[3], progress.Of("style")));
        }

        [Test]
        public void Bonuses_UseTheLastStep_AddUp_AndAChainReplaces()
        {
            SkillDefinition skill = Felling();
            SkillCatalog catalog = CatalogOf(skill);
            var state = new SkillState();
            SkillProgress progress = state.Get("felling");
            progress.Unlocked = true;
            progress.Xp = skill.MaxXp;

            SkillRules.Buy(skill, progress, "speed");
            SkillRules.Buy(skill, progress, "speed");
            SkillRules.Buy(skill, progress, "speed");
            Assert.AreEqual(15, SkillBonuses.Total(catalog, state, SkillBonusKind.TimePercent, "fell:Oak:prune"), 1e-9, "the third step's 15 %, not 5 + 10 + 15");
            Assert.AreEqual(0, SkillBonuses.Total(catalog, state, SkillBonusKind.TimePercent, "mine:Granite"), 1e-9);

            SkillRules.Buy(skill, progress, "autoBuck");
            SkillRules.Buy(skill, progress, "yield");
            SkillRules.Buy(skill, progress, "style", 0);
            Assert.AreEqual(10 + 20, SkillBonuses.Total(catalog, state, SkillBonusKind.YieldPercent, "fell:Oak"), 1e-9, "the option's 10 % and the formula's 10 × tier 2");
            Assert.AreEqual(10, SkillBonuses.Total(catalog, state, SkillBonusKind.YieldPercent, "fell:Birch"), 1e-9);
            Assert.IsTrue(SkillBonuses.Has(catalog, state, SkillBonusKind.Interaction, SkillInteractions.AutoBuck));
            Assert.IsFalse(SkillBonuses.Has(catalog, state, SkillBonusKind.Interaction, SkillInteractions.AutoPrune));

            SkillRules.Buy(skill, progress, "betterYield");
            Assert.AreEqual(10 + 50, SkillBonuses.Total(catalog, state, SkillBonusKind.YieldPercent, "fell:Oak"), 1e-9, "the chained upgrade takes the place of the one it replaces");
        }

        [Test]
        public void Bonuses_ApplyWithFloors()
        {
            Assert.AreEqual(8, SkillBonuses.Hits(8, 5), "8 × 0.95 = 7.6, rounded up");
            Assert.AreEqual(6, SkillBonuses.Hits(8, 25));
            Assert.AreEqual(1, SkillBonuses.Hits(8, 99), "never under a tenth, nor one hit");
            Assert.AreEqual(1, SkillBonuses.Hits(1, 50));
            Assert.AreEqual(0.75, SkillBonuses.Factor(25), 1e-9);
            Assert.AreEqual(0.1, SkillBonuses.Factor(200), 1e-9);
            Assert.AreEqual(4, SkillBonuses.Yield(4, 0, 0, 0.99));
            Assert.AreEqual(4, SkillBonuses.Yield(4, 10, 0, 0.5), "4.4: one more with a chance of 0.4");
            Assert.AreEqual(5, SkillBonuses.Yield(4, 10, 0, 0.3));
            Assert.AreEqual(6, SkillBonuses.Yield(4, 0, 2, 0.9));
            Assert.AreEqual(5f, SkillBonuses.Seconds(10f, 50, 0), 1e-5f);
            Assert.AreEqual(1f, SkillBonuses.Seconds(10f, 50, 20), 1e-5f);
        }

        [Test]
        public void TheState_SurvivesASaveAndReload()
        {
            var state = new SkillState();
            SkillProgress progress = state.Get("felling");
            progress.Unlocked = true;
            progress.Xp = 321.5f;
            progress.Upgrades["speed"] = new UpgradeProgress { Steps = 3 };
            progress.Upgrades["style"] = new UpgradeProgress { Choices = 5 };
            state.Get("unknown").Xp = 4f;

            var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true)) state.Write(writer);
            stream.Position = 0;
            var loaded = new SkillState();
            using (var reader = new BinaryReader(stream)) loaded.Read(reader);

            Assert.IsTrue(loaded.TryGet("felling", out SkillProgress read));
            Assert.IsTrue(read.Unlocked);
            Assert.AreEqual(321.5f, read.Xp);
            Assert.AreEqual(3, read.Of("speed").Steps);
            Assert.AreEqual(2, read.Of("style").ChoiceCount);
            Assert.IsTrue(loaded.TryGet("unknown", out _), "ids no skill has are kept");
        }

        [Test]
        public void PlayerSkills_AreSavedWithThePlayer()
        {
            var store = new PlayerStore();
            PlayerState state = store.GetOrAdd("player");
            SkillProgress progress = state.Skills.Get("felling");
            progress.Unlocked = true;
            progress.Xp = 42f;

            var stream = new MemoryStream();
            store.Write(stream);
            stream.Position = 0;
            PlayerStore loaded = PlayerStore.Read(stream);

            Assert.IsTrue(loaded.GetOrAdd("player").Skills.TryGet("felling", out SkillProgress read));
            Assert.AreEqual(42f, read.Xp);
        }

        [Test]
        public void Validation_FindsWhatIsWrong()
        {
            Assert.IsEmpty(SkillCatalog.Validate(new[] { Felling() }));

            SkillDefinition broken = Felling();
            broken.Tiers[0].Upgrades[2].Requires = new[] { "yield" };
            broken.Tiers[1].Upgrades[0].Steps[0].Bonuses[0].Formula = "10 * (tier";
            var problems = SkillCatalog.Validate(new[] { broken, Felling() });
            Assert.AreEqual(3, problems.Count, string.Join("\n", problems));
        }
    }
}

using System;

namespace Voxel.Core
{
    // Why an upgrade cannot be bought now.
    public enum SkillRefusal : byte
    {
        None,
        UnknownSkill,
        UnknownUpgrade,
        SkillLocked,
        Hidden,
        TierClosed,
        RequiresMissing,
        ReplacesMissing,
        Complete,
        ChoiceTaken,
        NoChoice,
        NotEnoughPoints
    }

    // How a player progresses in skills: experience opens tiers, each quarter of an open tier gives
    // points, points buy upgrades, under their conditions.
    public static class SkillRules
    {
        // How many tiers are open, 0 for a locked skill: the first once unlocked, each next one once
        // the experience covers the quotas before it.
        public static int OpenTiers(SkillDefinition skill, SkillProgress progress)
        {
            if (progress == null || !progress.Unlocked) return 0;

            int open = 1;
            while (open < skill.Tiers.Length && progress.Xp >= skill.TierStart(open + 1)) open++;
            return open;
        }

        // How far through a tier (1 to 5) the experience is, 0 to 1.
        public static float TierProgress(SkillDefinition skill, SkillProgress progress, int tier)
        {
            if (progress == null || !progress.Unlocked || tier < 1 || tier > skill.Tiers.Length) return 0f;

            float quota = skill.Tiers[tier - 1].XpQuota;
            if (quota <= 0f) return progress.Xp >= skill.TierStart(tier) ? 1f : 0f;
            return Math.Max(0f, Math.Min(1f, (progress.Xp - skill.TierStart(tier)) / quota));
        }

        // The quarters of a tier reached, 0 to 4.
        public static int Quarters(SkillDefinition skill, SkillProgress progress, int tier)
        {
            if (tier > OpenTiers(skill, progress)) return 0;
            return Math.Min(SkillDefinition.QuartersPerTier, (int)Math.Floor(TierProgress(skill, progress, tier) * SkillDefinition.QuartersPerTier + 1e-6));
        }

        public static int PointsEarned(SkillDefinition skill, SkillProgress progress)
        {
            int points = 0;
            for (int tier = 1; tier <= skill.Tiers.Length; tier++) points += skill.PointsForQuarters(Quarters(skill, progress, tier));
            return points;
        }

        public static int PointsSpent(SkillDefinition skill, SkillProgress progress)
        {
            if (progress == null) return 0;

            int points = 0;
            foreach (UpgradeDefinition upgrade in skill.Upgrades)
            {
                UpgradeProgress bought = progress.Of(upgrade.Id);
                points += upgrade.Cost * (upgrade.HasChoices ? bought.ChoiceCount : bought.Steps);
            }

            return points;
        }

        public static int PointsLeft(SkillDefinition skill, SkillProgress progress) => PointsEarned(skill, progress) - PointsSpent(skill, progress);

        // Whether an upgrade counts as bought, for those that require it: every step, or as many options as it allows.
        public static bool IsComplete(UpgradeDefinition upgrade, UpgradeProgress bought)
        {
            if (upgrade.HasChoices) return bought.ChoiceCount >= upgrade.ChoiceLimit;
            return bought.Steps >= upgrade.Steps.Length;
        }

        // Whether a player may buy the next step of an upgrade, or one of its options (choice, from 0).
        public static SkillRefusal CanBuy(SkillDefinition skill, SkillProgress progress, string upgradeId, int choice = -1)
        {
            if (skill == null) return SkillRefusal.UnknownSkill;
            if (!skill.TryGetUpgrade(upgradeId, out UpgradeDefinition upgrade)) return SkillRefusal.UnknownUpgrade;
            if (progress == null || !progress.Unlocked) return SkillRefusal.SkillLocked;
            if (upgrade.Hidden) return SkillRefusal.Hidden;

            UpgradeProgress bought = progress.Of(upgrade.Id);
            if (IsComplete(upgrade, bought)) return SkillRefusal.Complete;

            int tier = upgrade.HasChoices ? upgrade.Tier : upgrade.TierOf(bought.Steps);
            if (tier > OpenTiers(skill, progress)) return SkillRefusal.TierClosed;

            foreach (string required in upgrade.Requires)
            {
                if (!skill.TryGetUpgrade(required, out UpgradeDefinition other) || !IsComplete(other, progress.Of(required))) return SkillRefusal.RequiresMissing;
            }

            if (!string.IsNullOrEmpty(upgrade.Replaces) && !progress.Of(upgrade.Replaces).Any) return SkillRefusal.ReplacesMissing;

            if (upgrade.HasChoices)
            {
                if (choice < 0 || choice >= upgrade.Choices.Length || choice >= 32) return SkillRefusal.NoChoice;
                if (bought.HasChoice(choice)) return SkillRefusal.ChoiceTaken;
            }

            return PointsLeft(skill, progress) < upgrade.Cost ? SkillRefusal.NotEnoughPoints : SkillRefusal.None;
        }

        // Buys the next step, or the option, if it may be bought.
        public static SkillRefusal Buy(SkillDefinition skill, SkillProgress progress, string upgradeId, int choice = -1)
        {
            SkillRefusal refusal = CanBuy(skill, progress, upgradeId, choice);
            if (refusal != SkillRefusal.None) return refusal;

            skill.TryGetUpgrade(upgradeId, out UpgradeDefinition upgrade);
            UpgradeProgress bought = progress.Of(upgrade.Id);
            if (upgrade.HasChoices) bought.Choices |= 1u << choice;
            else bought.Steps++;
            progress.Upgrades[upgrade.Id] = bought;
            return SkillRefusal.None;
        }

        // A player did something (context, SkillContext) burning these calories with a tool in hand
        // (Items.None for none): it unlocks the skills whose condition it meets (and whose prerequisite
        // skills have reached their tier), then gives its calories as experience to the unlocked
        // skills that learn from it. True when anything changed.
        public static bool Practise(SkillCatalog catalog, SkillState state, string context, float calories, int tool)
        {
            bool changed = false;
            foreach (SkillDefinition skill in catalog.Skills)
            {
                SkillProgress progress = state.TryGet(skill.Id, out SkillProgress known) ? known : null;
                if ((progress == null || !progress.Unlocked) && Unlocks(skill, context, tool) && PrerequisitesMet(catalog, state, skill))
                {
                    progress = state.Get(skill.Id);
                    progress.Unlocked = true;
                    changed = true;
                }

                if (progress == null || !progress.Unlocked || calories <= 0f || !LearnsFrom(skill, context)) continue;

                float before = progress.Xp;
                progress.Xp = Math.Min(skill.MaxXp, progress.Xp + calories);
                changed |= progress.Xp != before;
            }

            if (changed) state.Touch();
            return changed;
        }

        public static bool Unlocks(SkillDefinition skill, string context, int tool)
        {
            foreach (SkillUnlock unlock in skill.Unlocks)
            {
                if (SkillContext.Matches(unlock.Context, context) && (unlock.Tool == Items.None || unlock.Tool == tool)) return true;
            }

            return false;
        }

        public static bool LearnsFrom(SkillDefinition skill, string context)
        {
            foreach (string pattern in skill.XpContexts)
            {
                if (SkillContext.Matches(pattern, context)) return true;
            }

            return false;
        }

        public static bool PrerequisitesMet(SkillCatalog catalog, SkillState state, SkillDefinition skill)
        {
            foreach (SkillPrerequisite prerequisite in skill.Prerequisites)
            {
                if (!catalog.TryGet(prerequisite.SkillId, out SkillDefinition other)) return false;
                state.TryGet(prerequisite.SkillId, out SkillProgress progress);
                if (OpenTiers(other, progress) < prerequisite.Tier) return false;
            }

            return true;
        }
    }
}

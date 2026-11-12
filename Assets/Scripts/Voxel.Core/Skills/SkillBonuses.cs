using System;
using System.Collections.Generic;

namespace Voxel.Core
{
    // What a player's skills give an action: the helps of every upgrade bought, added up. An upgrade
    // in steps gives its last step's helps (each step is its new value); one with choices gives each
    // option chosen; a chained upgrade bought takes the place of the one it replaces. Percentages add
    // up between upgrades (and later with tools' and global bonuses).
    public static class SkillBonuses
    {
        // Every help of this kind for an action of this context, added up (a percentage as given, 5
        // for 5 %).
        public static double Total(SkillCatalog catalog, SkillState state, SkillBonusKind kind, string context)
        {
            double total = 0;
            ForEach(catalog, state, (bonus, step, steps, tier) =>
            {
                if (bonus.Kind == kind && SkillContext.Matches(bonus.Context, context)) total += bonus.ValueAt(step, steps, tier);
            });
            return total;
        }

        // Whether a player has a help of this kind naming exactly this (an interaction, a recipe).
        public static bool Has(SkillCatalog catalog, SkillState state, SkillBonusKind kind, string name)
        {
            bool found = false;
            ForEach(catalog, state, (bonus, step, steps, tier) =>
            {
                if (bonus.Kind == kind && bonus.Context == name) found = true;
            });
            return found;
        }

        // How many of something an action gives with these helps: n × (1 + percent) + flat, the
        // fraction left giving one more with that chance (roll, 0 to 1, drawn by the server).
        public static int Yield(int count, double percent, double flat, double roll)
        {
            if (count <= 0 && flat <= 0) return Math.Max(0, count);

            double value = Math.Max(0, count * (1 + percent / 100.0) + flat);
            int whole = (int)Math.Floor(value);
            return whole + (roll < value - whole ? 1 : 0);
        }

        // How many hits (or how long) an action takes with these helps: never under a tenth of what it
        // took, never under one hit.
        public static int Hits(int hits, double percent)
        {
            if (hits <= 1) return hits;
            return Math.Max(1, (int)Math.Ceiling(hits * Factor(percent) - 1e-9));
        }

        public static float Seconds(float seconds, double percent, double secondsOff)
        {
            double value = seconds * Factor(percent) - secondsOff;
            return (float)Math.Max(seconds * 0.1, value);
        }

        // What a percentage off leaves, never under a tenth (calories, weight).
        public static double Factor(double percentOff) => Math.Max(0.1, 1 - percentOff / 100.0);

        private delegate void BonusVisitor(SkillBonus bonus, int step, int steps, int tier);

        private static void ForEach(SkillCatalog catalog, SkillState state, BonusVisitor visit)
        {
            if (catalog == null || state == null) return;

            foreach (SkillDefinition skill in catalog.Skills)
            {
                if (!state.TryGet(skill.Id, out SkillProgress progress) || !progress.Unlocked) continue;

                // The upgrades a bought chained upgrade replaces give nothing more.
                var replacedHere = new HashSet<string>(StringComparer.Ordinal);
                foreach (UpgradeDefinition upgrade in skill.Upgrades)
                {
                    if (!string.IsNullOrEmpty(upgrade.Replaces) && progress.Of(upgrade.Id).Any) replacedHere.Add(upgrade.Replaces);
                }

                foreach (UpgradeDefinition upgrade in skill.Upgrades)
                {
                    if (upgrade.Hidden || replacedHere.Contains(upgrade.Id)) continue;

                    UpgradeProgress bought = progress.Of(upgrade.Id);
                    if (upgrade.HasChoices)
                    {
                        for (int c = 0; c < upgrade.Choices.Length && c < 32; c++)
                        {
                            if (!bought.HasChoice(c)) continue;
                            foreach (SkillBonus bonus in upgrade.Choices[c].Bonuses) visit(bonus, 1, 1, upgrade.Tier);
                        }
                    }
                    else if (bought.Steps > 0)
                    {
                        int last = Math.Min(bought.Steps, upgrade.Steps.Length) - 1;
                        if (last < 0) continue;
                        foreach (SkillBonus bonus in upgrade.Steps[last].Bonuses) visit(bonus, last + 1, upgrade.Steps.Length, upgrade.TierOf(last));
                    }
                }
            }
        }
    }
}

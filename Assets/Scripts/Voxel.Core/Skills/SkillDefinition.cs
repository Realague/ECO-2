using System;
using System.Collections.Generic;

namespace Voxel.Core
{
    // What kind of help a bonus gives (SkillBonus).
    public enum SkillBonusKind : byte
    {
        // More of what an action gives: a percentage (Value 5 is +5 %) or a number.
        YieldPercent,
        YieldFlat,

        // Less time, or fewer hits, an action takes: a percentage, or seconds off.
        TimePercent,
        TimeSeconds,

        // Fewer calories an action burns: a percentage.
        CaloriePercent,

        // Less weight carried: a percentage (the context names what weighs less: wood).
        WeightPercent,

        // A recipe only the holder can craft: the context is the recipe's key (Recipes).
        Recipe,

        // Something the holder can do that others cannot: the context is its name ("felling.autoBuck").
        Interaction,

        // A chance, in percent, that a hit finishes the action at once: a standing tree felled in one
        // blow (MiningSync). Kept last: assets store kinds by number.
        FinishChancePercent
    }

    // One help an upgrade gives (SkillBonusKind) to actions of a context (SkillContext): Value, or
    // Formula if it has one (SkillFormula), worked out for the step reached.
    public sealed class SkillBonus
    {
        public SkillBonusKind Kind;
        public string Context = "";
        public double Value;
        public string Formula = "";

        public double ValueAt(int step, int steps, int tier) =>
            string.IsNullOrEmpty(Formula) ? Value : SkillFormula.Evaluate(Formula, step, steps, tier);
    }

    // One step of an upgrade: its helps, which replace the previous step's (5 %, 10 %, 15 %…: each
    // step is the upgrade's new value), and the tier it needs (0: the upgrade's own).
    public sealed class UpgradeStep
    {
        public int Tier;
        public SkillBonus[] Bonuses = new SkillBonus[0];
    }

    // One option of an upgrade with choices.
    public sealed class UpgradeChoice
    {
        public string Id = "";
        public string Name = "";
        public string Description = "";
        public SkillBonus[] Bonuses = new SkillBonus[0];
    }

    // An upgrade of a tier: bought with skill points, either step by step (one step for a single
    // upgrade, several for one in steps, each costing Cost) or option by option (an upgrade with
    // choices, up to MaxChoices, each costing Cost). It needs its tier open, its required upgrades
    // complete, and for a chained one the upgrade it replaces bought (whose helps it then replaces).
    public sealed class UpgradeDefinition
    {
        public string Id = "";
        public string Name = "";
        public string Icon = "";
        public string[] Tags = new string[0];
        public string Description = "";
        public string TechnicalDescription = "";
        public int Cost = 1;
        public UpgradeStep[] Steps = new UpgradeStep[0];
        public UpgradeChoice[] Choices = new UpgradeChoice[0];
        public int MaxChoices;
        public string[] Requires = new string[0];
        public string Replaces = "";

        // Shown nowhere and never bought (an upgrade still to come).
        public bool Hidden;

        // The tier holding it, 1 to 5 (set by SkillDefinition).
        public int Tier { get; internal set; }

        public bool HasChoices => Choices.Length > 0;

        // How many options complete it.
        public int ChoiceLimit => MaxChoices > 0 ? Math.Min(MaxChoices, Choices.Length) : Choices.Length;

        // The tier a step needs (0-based step).
        public int TierOf(int step) => step < Steps.Length && Steps[step].Tier > 0 ? Steps[step].Tier : Tier;
    }

    // One of a skill's five tiers: open once the one before it is complete, complete once the skill's
    // experience in it reaches its quota.
    public sealed class TierDefinition
    {
        public string Name = "";
        public string Description = "";
        public float XpQuota = 100f;
        public UpgradeDefinition[] Upgrades = new UpgradeDefinition[0];
    }

    // What makes a skill available: an action of a context (with a tool, or Items.None for any).
    public sealed class SkillUnlock
    {
        public string Context = "";
        public int Tool = Items.None;
    }

    // A skill that must have reached a tier first.
    public sealed class SkillPrerequisite
    {
        public string SkillId = "";
        public int Tier = 1;
    }

    // A skill: unlocked by an action (Unlocks), it gains experience from the calories its actions
    // burn (XpContexts), opens its five tiers one after another (each giving PointsPerTier points, a
    // quarter at a time), and its points buy upgrades.
    public sealed class SkillDefinition
    {
        public const int TierCount = 5;
        public const int QuartersPerTier = 4;

        public string Id = "";
        public string Name = "";
        public string Description = "";
        public string Icon = "";
        public SkillPrerequisite[] Prerequisites = new SkillPrerequisite[0];
        public SkillUnlock[] Unlocks = new SkillUnlock[0];
        public string[] XpContexts = new string[0];
        public int PointsPerTier = 8;
        public TierDefinition[] Tiers = new TierDefinition[0];

        private Dictionary<string, UpgradeDefinition> upgrades;

        // Ties each upgrade to its tier and indexes them. To call once the definition is filled in.
        public SkillDefinition Seal()
        {
            upgrades = new Dictionary<string, UpgradeDefinition>(StringComparer.Ordinal);
            for (int t = 0; t < Tiers.Length; t++)
            {
                foreach (UpgradeDefinition upgrade in Tiers[t].Upgrades)
                {
                    upgrade.Tier = t + 1;
                    if (!upgrades.ContainsKey(upgrade.Id)) upgrades.Add(upgrade.Id, upgrade);
                }
            }

            return this;
        }

        public IEnumerable<UpgradeDefinition> Upgrades
        {
            get
            {
                foreach (TierDefinition tier in Tiers)
                {
                    foreach (UpgradeDefinition upgrade in tier.Upgrades) yield return upgrade;
                }
            }
        }

        public bool TryGetUpgrade(string id, out UpgradeDefinition upgrade)
        {
            if (upgrades == null) Seal();
            return upgrades.TryGetValue(id ?? "", out upgrade);
        }

        // The experience at which a tier (1 to 5) opens: the quotas of the tiers before it added up.
        public float TierStart(int tier)
        {
            float start = 0f;
            for (int t = 0; t < tier - 1 && t < Tiers.Length; t++) start += Tiers[t].XpQuota;
            return start;
        }

        // All the experience the skill can hold.
        public float MaxXp => TierStart(Tiers.Length + 1);

        // Points one quarter of a tier gives, the rest of the division going to the last quarters.
        public int PointsForQuarters(int quarters) => quarters * PointsPerTier / QuartersPerTier;
    }

    // Every skill a game knows, by id; the same on the server and every client (the build's assets).
    public sealed class SkillCatalog
    {
        private readonly Dictionary<string, SkillDefinition> byId = new Dictionary<string, SkillDefinition>(StringComparer.Ordinal);

        public IReadOnlyList<SkillDefinition> Skills { get; }

        public SkillCatalog(IEnumerable<SkillDefinition> skills)
        {
            var list = new List<SkillDefinition>();
            foreach (SkillDefinition skill in skills)
            {
                if (skill == null || byId.ContainsKey(skill.Id)) continue;

                skill.Seal();
                byId.Add(skill.Id, skill);
                list.Add(skill);
            }

            Skills = list;
        }

        public static readonly SkillCatalog Empty = new SkillCatalog(new SkillDefinition[0]);

        public bool TryGet(string id, out SkillDefinition skill) => byId.TryGetValue(id ?? "", out skill);

        // What is wrong with the definitions: ids twice, a skill without five tiers, an upgrade
        // requiring one of its own tier or a later one or one that does not exist, a chained upgrade
        // replacing nothing, a formula that does not read.
        public static List<string> Validate(IEnumerable<SkillDefinition> skills)
        {
            var problems = new List<string>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (SkillDefinition skill in skills)
            {
                if (skill == null) continue;
                if (string.IsNullOrEmpty(skill.Id)) problems.Add("A skill has no id.");
                else if (!ids.Add(skill.Id)) problems.Add($"Skill id \"{skill.Id}\" is used twice.");
                if (skill.Tiers.Length != SkillDefinition.TierCount) problems.Add($"Skill \"{skill.Id}\" has {skill.Tiers.Length} tiers, not {SkillDefinition.TierCount}.");
                if (skill.PointsPerTier <= 0) problems.Add($"Skill \"{skill.Id}\" gives no points.");

                skill.Seal();
                var upgradeIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (UpgradeDefinition upgrade in skill.Upgrades)
                {
                    string name = $"\"{skill.Id}/{upgrade.Id}\"";
                    if (string.IsNullOrEmpty(upgrade.Id) || !upgradeIds.Add(upgrade.Id)) problems.Add($"Upgrade {name} has no id or one used twice.");
                    if (!upgrade.HasChoices && upgrade.Steps.Length == 0 && !upgrade.Hidden) problems.Add($"Upgrade {name} has neither steps nor choices.");
                    if (upgrade.Cost < 0) problems.Add($"Upgrade {name} costs less than nothing.");

                    foreach (string required in upgrade.Requires)
                    {
                        if (!skill.TryGetUpgrade(required, out UpgradeDefinition other)) problems.Add($"Upgrade {name} requires \"{required}\", which does not exist.");
                        else if (other.Tier > upgrade.Tier) problems.Add($"Upgrade {name} requires \"{required}\", of a later tier.");
                    }

                    if (!string.IsNullOrEmpty(upgrade.Replaces) && !skill.TryGetUpgrade(upgrade.Replaces, out _)) problems.Add($"Upgrade {name} replaces \"{upgrade.Replaces}\", which does not exist.");

                    for (int s = 0; s < upgrade.Steps.Length; s++)
                    {
                        int tier = upgrade.TierOf(s);
                        if (tier < upgrade.Tier || tier > SkillDefinition.TierCount) problems.Add($"Upgrade {name}'s step {s + 1} needs tier {tier}.");
                        CheckFormulas(upgrade.Steps[s].Bonuses, name, problems);
                    }

                    foreach (UpgradeChoice choice in upgrade.Choices) CheckFormulas(choice.Bonuses, name, problems);
                }
            }

            return problems;
        }

        private static void CheckFormulas(SkillBonus[] bonuses, string name, List<string> problems)
        {
            foreach (SkillBonus bonus in bonuses)
            {
                if (!string.IsNullOrEmpty(bonus.Formula) && !SkillFormula.TryValidate(bonus.Formula, out string error)) problems.Add($"Upgrade {name}: {error}");
            }
        }
    }
}

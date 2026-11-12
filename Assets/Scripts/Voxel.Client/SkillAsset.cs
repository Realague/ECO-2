using System;
using UnityEngine;
using Voxel.Core;

namespace Voxel.Client
{
    // A skill as the editor shows it (Assets/Skills), turned into the rules' SkillDefinition by
    // ToDefinition. Its id is what saves keep: never change it once players have the skill. Names
    // and descriptions are written in English and translated at display (Loc, fr.txt).
    [CreateAssetMenu(menuName = "Eco/Skill", fileName = "Skill")]
    public class SkillAsset : ScriptableObject
    {
        // The tool an unlocking action needs.
        public enum ToolKind
        {
            None,
            Shovel,
            Pickaxe,
            Axe,
            Scythe,
            Hoe,
            Bow,
            Knife,
            Hammer
        }

        [Serializable]
        public class Prerequisite
        {
            public SkillAsset skill;
            [Range(1, 5)] public int tier = 1;
        }

        [Serializable]
        public class Unlock
        {
            [Tooltip("The action that unlocks the skill (SkillContext: fell, dig:stump, craft:<item>…).")]
            public string context = "";
            public ToolKind tool;
        }

        [Serializable]
        public class Bonus
        {
            public SkillBonusKind kind;
            [Tooltip("The actions it helps (SkillContext), or the recipe or interaction it gives.")]
            public string context = "";
            [Tooltip("A percentage for the percent kinds (5 is 5 %), a number or seconds for the others.")]
            public float value;
            [Tooltip("Optional: worked out from step, steps and tier instead of value (\"5 * step\").")]
            public string formula = "";
        }

        [Serializable]
        public class Step
        {
            [Tooltip("The tier this step needs (0: the upgrade's own).")]
            [Range(0, 5)] public int tier;
            public Bonus[] bonuses = new Bonus[0];
        }

        [Serializable]
        public class Choice
        {
            public string id = "";
            public string name = "";
            [Tooltip("Shown on the option's card (the upgrade's icon when none).")]
            public Texture2D icon;
            [TextArea] public string description = "";
            public Bonus[] bonuses = new Bonus[0];
        }

        [Serializable]
        public class Upgrade
        {
            public string id = "";
            public string name = "";
            public Texture2D icon;
            public string[] tags = new string[0];
            [TextArea] public string description = "";
            [TextArea] public string technicalDescription = "";
            [Tooltip("Skill points per step, or per option chosen.")]
            public int cost = 1;
            public Step[] steps = new Step[0];
            public Choice[] choices = new Choice[0];
            [Tooltip("How many options complete it (0: all of them).")]
            public int maxChoices;
            [Tooltip("Upgrades (of earlier tiers) to complete first.")]
            public string[] requires = new string[0];
            [Tooltip("A chained upgrade: the upgrade it replaces, bought first.")]
            public string replaces = "";
            [Tooltip("Not shown nor bought yet.")]
            public bool hidden;
        }

        [Serializable]
        public class Tier
        {
            public string name = "";
            [TextArea] public string description = "";
            [Tooltip("Experience (calories) to complete the tier.")]
            public float xpQuota = 100f;
            public Upgrade[] upgrades = new Upgrade[0];
        }

        [Tooltip("Kept in saves: never change it once players have the skill.")]
        public string id = "";
        public string displayName = "";
        [TextArea] public string description = "";
        public Texture2D icon;
        public Prerequisite[] prerequisites = new Prerequisite[0];
        public Unlock[] unlocks = new Unlock[0];
        [Tooltip("The actions whose calories give it experience (SkillContext).")]
        public string[] xpContexts = new string[0];
        [Tooltip("Points the five tiers each give, a quarter at a time.")]
        public int pointsPerTier = 8;
        public Tier[] tiers = new Tier[SkillDefinition.TierCount];

        public static int ItemOf(ToolKind tool)
        {
            switch (tool)
            {
                case ToolKind.Shovel: return Items.Shovel;
                case ToolKind.Pickaxe: return Items.Pickaxe;
                case ToolKind.Axe: return Items.Axe;
                case ToolKind.Scythe: return Items.Scythe;
                case ToolKind.Hoe: return Items.Hoe;
                case ToolKind.Bow: return Items.Bow;
                case ToolKind.Knife: return Items.Knife;
                case ToolKind.Hammer: return Items.Hammer;
                default: return Items.None;
            }
        }

        // The upgrade with this id, for its icon.
        public Upgrade FindUpgrade(string upgradeId)
        {
            foreach (Tier tier in tiers ?? new Tier[0])
            {
                if (tier == null) continue;
                foreach (Upgrade upgrade in tier.upgrades ?? new Upgrade[0])
                {
                    if (upgrade != null && upgrade.id == upgradeId) return upgrade;
                }
            }

            return null;
        }

        public SkillDefinition ToDefinition()
        {
            var tierDefinitions = Array.ConvertAll(tiers ?? new Tier[0], tier => new TierDefinition
            {
                Name = tier?.name ?? "",
                Description = tier?.description ?? "",
                XpQuota = tier?.xpQuota ?? 0f,
                Upgrades = Array.ConvertAll(tier?.upgrades ?? new Upgrade[0], UpgradeOf)
            });

            return new SkillDefinition
            {
                Id = id,
                Name = displayName,
                Description = description,
                Icon = icon != null ? icon.name : "",
                Prerequisites = Array.ConvertAll(prerequisites ?? new Prerequisite[0], p => new SkillPrerequisite { SkillId = p.skill != null ? p.skill.id : "", Tier = p.tier }),
                Unlocks = Array.ConvertAll(unlocks ?? new Unlock[0], u => new SkillUnlock { Context = u.context, Tool = ItemOf(u.tool) }),
                XpContexts = xpContexts ?? new string[0],
                PointsPerTier = pointsPerTier,
                Tiers = tierDefinitions
            }.Seal();
        }

        private static UpgradeDefinition UpgradeOf(Upgrade upgrade) => new UpgradeDefinition
        {
            Id = upgrade.id,
            Name = upgrade.name,
            Icon = upgrade.icon != null ? upgrade.icon.name : "",
            Tags = upgrade.tags ?? new string[0],
            Description = upgrade.description,
            TechnicalDescription = upgrade.technicalDescription,
            Cost = upgrade.cost,
            Steps = Array.ConvertAll(upgrade.steps ?? new Step[0], step => new UpgradeStep { Tier = step.tier, Bonuses = BonusesOf(step.bonuses) }),
            Choices = Array.ConvertAll(upgrade.choices ?? new Choice[0], choice => new UpgradeChoice
            {
                Id = choice.id,
                Name = choice.name,
                Description = choice.description,
                Bonuses = BonusesOf(choice.bonuses)
            }),
            MaxChoices = upgrade.maxChoices,
            Requires = upgrade.requires ?? new string[0],
            Replaces = upgrade.replaces ?? "",
            Hidden = upgrade.hidden
        };

        private static SkillBonus[] BonusesOf(Bonus[] bonuses) => Array.ConvertAll(bonuses ?? new Bonus[0], bonus => new SkillBonus
        {
            Kind = bonus.kind,
            Context = bonus.context ?? "",
            Value = bonus.value,
            Formula = bonus.formula ?? ""
        });
    }
}

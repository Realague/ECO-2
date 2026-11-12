using System;
using System.Collections.Generic;
using System.IO;

namespace Voxel.Core
{
    // What a player has bought of an upgrade: steps (one for a single upgrade) and the options chosen
    // (a bit per option).
    public struct UpgradeProgress
    {
        public int Steps;
        public uint Choices;

        public int ChoiceCount
        {
            get
            {
                int count = 0;
                for (uint bits = Choices; bits != 0; bits &= bits - 1) count++;
                return count;
            }
        }

        public bool HasChoice(int index) => index >= 0 && index < 32 && (Choices & (1u << index)) != 0;

        public bool Any => Steps > 0 || Choices != 0;
    }

    // Where a player is in one skill: whether it is unlocked, its experience, and what was bought.
    public sealed class SkillProgress
    {
        public bool Unlocked;
        public float Xp;
        public readonly Dictionary<string, UpgradeProgress> Upgrades = new Dictionary<string, UpgradeProgress>(StringComparer.Ordinal);

        public UpgradeProgress Of(string upgradeId) => Upgrades.TryGetValue(upgradeId ?? "", out UpgradeProgress progress) ? progress : default;
    }

    // A player's skills (PlayerState.Skills, players.dat format 12): by skill id, so that reordering
    // or adding skill assets never breaks a save; ids the game no longer knows are kept as they are
    // read and simply match nothing.
    public sealed class SkillState
    {
        private readonly Dictionary<string, SkillProgress> skills = new Dictionary<string, SkillProgress>(StringComparer.Ordinal);

        public IEnumerable<KeyValuePair<string, SkillProgress>> Entries => skills;

        // Counts every change, for whoever sends or shows the state.
        public int Version { get; private set; }

        public void Touch() => Version++;

        public bool TryGet(string skillId, out SkillProgress progress) => skills.TryGetValue(skillId ?? "", out progress);

        public SkillProgress Get(string skillId)
        {
            if (!skills.TryGetValue(skillId, out SkillProgress progress))
            {
                progress = new SkillProgress();
                skills.Add(skillId, progress);
            }

            return progress;
        }

        public void Clear()
        {
            skills.Clear();
            Touch();
        }

        public void CopyFrom(SkillState other)
        {
            skills.Clear();
            foreach (KeyValuePair<string, SkillProgress> entry in other.skills)
            {
                SkillProgress copy = Get(entry.Key);
                copy.Unlocked = entry.Value.Unlocked;
                copy.Xp = entry.Value.Xp;
                foreach (KeyValuePair<string, UpgradeProgress> upgrade in entry.Value.Upgrades) copy.Upgrades[upgrade.Key] = upgrade.Value;
            }

            Touch();
        }

        public void Write(BinaryWriter writer)
        {
            writer.Write(skills.Count);
            foreach (KeyValuePair<string, SkillProgress> entry in skills)
            {
                writer.Write(entry.Key);
                writer.Write(entry.Value.Unlocked);
                writer.Write(entry.Value.Xp);
                writer.Write(entry.Value.Upgrades.Count);
                foreach (KeyValuePair<string, UpgradeProgress> upgrade in entry.Value.Upgrades)
                {
                    writer.Write(upgrade.Key);
                    writer.Write((byte)Math.Min(255, upgrade.Value.Steps));
                    writer.Write(upgrade.Value.Choices);
                }
            }
        }

        public void Read(BinaryReader reader)
        {
            skills.Clear();
            int count = reader.ReadInt32();
            if (count < 0 || count > 10000) throw new InvalidDataException($"Bad skill count {count}.");

            for (int i = 0; i < count; i++)
            {
                SkillProgress progress = Get(reader.ReadString());
                progress.Unlocked = reader.ReadBoolean();
                progress.Xp = Math.Max(0f, reader.ReadSingle());
                int upgrades = reader.ReadInt32();
                if (upgrades < 0 || upgrades > 10000) throw new InvalidDataException($"Bad upgrade count {upgrades}.");

                for (int u = 0; u < upgrades; u++)
                {
                    string id = reader.ReadString();
                    progress.Upgrades[id] = new UpgradeProgress { Steps = reader.ReadByte(), Choices = reader.ReadUInt32() };
                }
            }

            Touch();
        }
    }
}

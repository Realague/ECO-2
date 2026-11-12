using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using Voxel.Core;

namespace Voxel.Client
{
    // Players' skills (SkillRules, SkillBonuses), kept by the server in each PlayerState and saved
    // with it. The actions that burn calories report them here (Practise: MiningSync for felling,
    // pruning, bucking and stumps, CraftingSync for crafts, HungerSync for placing and tilling), which
    // unlocks skills and gives experience; the game asks here what a player's skills give an action
    // (Bonus, Has). Each player's machine is sent its own state (Skill.State) on a change, at most
    // once a second for experience alone, at once for an unlock, a tier, points or a purchase, and
    // asks to buy an upgrade with Skill.Buy, which the server checks with SkillRules. It also knows
    // the server's skill options (SkillOptions: -skills-showall, -skills-showtiers, -skills-details,
    // or the host's own fields on GameSession).
    public class SkillSync : MonoBehaviour
    {
        private const string StateMessage = "Skill.State";
        private const string BuyMessage = "Skill.Buy";
        private const string NoticeMessage = "Skill.Notice";

        // Experience alone is sent at most this often.
        private const float XpSendInterval = 1f;

        private enum Notice : byte
        {
            Unlocked,
            TierReached,
            Points,
            Refused
        }

        [SerializeField] private NetworkManager networkManager;
        [SerializeField] private InventorySync inventorySync;
        [SerializeField] private HotbarHud hotbar;
        [SerializeField] private SkillAsset[] skillAssets = new SkillAsset[0];

        private SkillCatalog catalog = SkillCatalog.Empty;
        private PlayerRegistry registry;
        private SkillOptions serverOptions;
        private bool running;

        // Server side: what each player was last sent, and when; who has something important to hear.
        private readonly Dictionary<ulong, int> sentVersions = new Dictionary<ulong, int>();
        private readonly Dictionary<ulong, float> sentAt = new Dictionary<ulong, float>();
        private readonly HashSet<ulong> urgent = new HashSet<ulong>();
        private readonly List<ulong> clientIds = new List<ulong>();

        // This machine: its player's skills as the server last told them (a client), and the options.
        private readonly SkillState received = new SkillState();
        private SkillOptions options;

        private bool IsServer => registry != null;

        private CustomMessagingManager Messaging => networkManager.CustomMessagingManager;

        public SkillCatalog Catalog => catalog;

        public IReadOnlyList<SkillAsset> Assets => skillAssets;

        // The local player's skills: on the host the server's own copy, on a client the last state sent.
        public SkillState LocalState => IsServer && registry.TryGetState(networkManager.LocalClientId, out PlayerState state) ? state.Skills : received;

        public SkillOptions Options => IsServer ? serverOptions : options;

        // Raised when the local player's skills (or the options) changed.
        public event Action Changed;

        private void Awake()
        {
            var definitions = new List<SkillDefinition>();
            foreach (SkillAsset asset in skillAssets)
            {
                if (asset != null) definitions.Add(asset.ToDefinition());
            }

            foreach (string problem in SkillCatalog.Validate(definitions)) Debug.LogError($"Skills: {problem}");
            catalog = new SkillCatalog(definitions);
        }

        public SkillAsset AssetOf(string skillId) => Array.Find(skillAssets, asset => asset != null && asset.id == skillId);

        // To call right after the server or host has started.
        internal void StartServer(PlayerRegistry playerRegistry, SkillOptions skillOptions)
        {
            running = true;
            registry = playerRegistry;
            serverOptions = skillOptions;
            Messaging.RegisterNamedMessageHandler(BuyMessage, OnBuy);
            networkManager.OnClientDisconnectCallback += OnClientDisconnected;
            Changed?.Invoke();
        }

        // To call right after a client's connection has been launched.
        public void StartClient()
        {
            running = true;
            Messaging.RegisterNamedMessageHandler(StateMessage, OnState);
            Messaging.RegisterNamedMessageHandler(NoticeMessage, OnNotice);
        }

        public void Stop()
        {
            if (!running) return;

            if (IsServer) networkManager.OnClientDisconnectCallback -= OnClientDisconnected;
            registry = null;
            running = false;
            sentVersions.Clear();
            sentAt.Clear();
            urgent.Clear();
            received.Clear();
            options = default;
            Changed?.Invoke();
        }

        // ---- What skills give (server: any player; this machine: its own) ----

        // Every help of a kind for an action of a context a player's skills give, added up (5 for 5 %).
        public double Bonus(ulong clientId, SkillBonusKind kind, string context) =>
            StateOf(clientId, out SkillState state) ? SkillBonuses.Total(catalog, state, kind, context) : 0;

        public bool Has(ulong clientId, SkillBonusKind kind, string name) =>
            StateOf(clientId, out SkillState state) && SkillBonuses.Has(catalog, state, kind, name);

        public double LocalBonus(SkillBonusKind kind, string context) => SkillBonuses.Total(catalog, LocalState, kind, context);

        public bool LocalHas(SkillBonusKind kind, string name) => SkillBonuses.Has(catalog, LocalState, kind, name);

        // What a player's skills take off the weight of wood, as a factor (1 for nothing).
        public float WoodWeightFactor(ulong clientId) => (float)SkillBonuses.Factor(Bonus(clientId, SkillBonusKind.WeightPercent, SkillContext.Wood));

        public float LocalWoodWeightFactor => (float)SkillBonuses.Factor(LocalBonus(SkillBonusKind.WeightPercent, SkillContext.Wood));

        private bool StateOf(ulong clientId, out SkillState state)
        {
            state = null;
            if (IsServer && registry.TryGetState(clientId, out PlayerState player)) state = player.Skills;
            else if (!IsServer && running && clientId == networkManager.LocalClientId) state = received;
            return state != null;
        }

        // ---- Server ----

        // A player did something (context, SkillContext) burning these calories (before any skill took
        // some off) with this tool in hand: it may unlock skills and gives them experience. Nothing in
        // creative mode, where nothing is burnt.
        internal void Practise(ulong clientId, string context, float calories, int tool)
        {
            if (!IsServer || !registry.TryGetState(clientId, out PlayerState player) || player.Creative) return;

            SkillState state = player.Skills;
            var before = Snapshot(state);
            if (!SkillRules.Practise(catalog, state, context, calories, tool)) return;

            foreach (SkillDefinition skill in catalog.Skills)
            {
                state.TryGet(skill.Id, out SkillProgress progress);
                (bool unlocked, int tiers, int points) = (progress != null && progress.Unlocked, SkillRules.OpenTiers(skill, progress), SkillRules.PointsEarned(skill, progress));
                before.TryGetValue(skill.Id, out (bool unlocked, int tiers, int points) was);
                if (unlocked && !was.unlocked) Tell(clientId, Notice.Unlocked, skill.Id, 0);
                // A tier reached says the points it came with in the same message (two would replace each other).
                int gained = unlocked ? Math.Max(0, points - was.points) : 0;
                if (unlocked && was.unlocked && tiers > was.tiers) Tell(clientId, Notice.TierReached, skill.Id, tiers | (gained << 8));
                else if (gained > 0) Tell(clientId, Notice.Points, skill.Id, gained);
                if (unlocked != was.unlocked || tiers != was.tiers || points != was.points) urgent.Add(clientId);
            }
        }

        private Dictionary<string, (bool unlocked, int tiers, int points)> Snapshot(SkillState state)
        {
            var snapshot = new Dictionary<string, (bool, int, int)>(StringComparer.Ordinal);
            foreach (SkillDefinition skill in catalog.Skills)
            {
                state.TryGet(skill.Id, out SkillProgress progress);
                snapshot[skill.Id] = (progress != null && progress.Unlocked, SkillRules.OpenTiers(skill, progress), SkillRules.PointsEarned(skill, progress));
            }

            return snapshot;
        }

        private void Buy(ulong clientId, string skillId, string upgradeId, int choice)
        {
            if (!registry.TryGetState(clientId, out PlayerState player)) return;

            catalog.TryGet(skillId, out SkillDefinition skill);
            SkillProgress progress = player.Skills.TryGet(skillId, out SkillProgress known) ? known : null;
            SkillRefusal refusal = skill == null ? SkillRefusal.UnknownSkill : SkillRules.Buy(skill, progress, upgradeId, choice);
            if (refusal != SkillRefusal.None)
            {
                Tell(clientId, Notice.Refused, skillId, (int)refusal);
                return;
            }

            player.Skills.Touch();
            urgent.Add(clientId);

            // What the skills now give may change what the player can carry.
            inventorySync.Publish(clientId);
        }

        private void Update()
        {
            if (!IsServer) return;

            clientIds.Clear();
            clientIds.AddRange(networkManager.ConnectedClientsIds);
            foreach (ulong clientId in clientIds)
            {
                if (!registry.TryGetState(clientId, out PlayerState player)) continue;

                int version = player.Skills.Version;
                bool known = sentVersions.TryGetValue(clientId, out int sent);
                if (known && sent == version) continue;

                bool due = !known || urgent.Contains(clientId) || Time.unscaledTime - (sentAt.TryGetValue(clientId, out float at) ? at : 0f) >= XpSendInterval;
                if (!due) continue;

                sentVersions[clientId] = version;
                sentAt[clientId] = Time.unscaledTime;
                urgent.Remove(clientId);
                if (clientId == networkManager.LocalClientId) Changed?.Invoke();
                else SendState(clientId, player.Skills);
            }
        }

        private void OnClientDisconnected(ulong clientId)
        {
            sentVersions.Remove(clientId);
            sentAt.Remove(clientId);
            urgent.Remove(clientId);
        }

        private void OnBuy(ulong senderId, FastBufferReader reader)
        {
            reader.ReadValueSafe(out string skillId);
            reader.ReadValueSafe(out string upgradeId);
            reader.ReadValueSafe(out int choice);
            Buy(senderId, skillId, upgradeId, choice);
        }

        private void SendState(ulong clientId, SkillState state)
        {
            using (var writer = new FastBufferWriter(1024, Allocator.Temp, 1 << 20))
            {
                writer.WriteValueSafe(serverOptions.ToByte());
                var entries = new List<KeyValuePair<string, SkillProgress>>(state.Entries);
                writer.WriteValueSafe(entries.Count);
                foreach (KeyValuePair<string, SkillProgress> entry in entries)
                {
                    writer.WriteValueSafe(entry.Key);
                    writer.WriteValueSafe(entry.Value.Unlocked);
                    writer.WriteValueSafe(entry.Value.Xp);
                    writer.WriteValueSafe(entry.Value.Upgrades.Count);
                    foreach (KeyValuePair<string, UpgradeProgress> upgrade in entry.Value.Upgrades)
                    {
                        writer.WriteValueSafe(upgrade.Key);
                        writer.WriteValueSafe((byte)Mathf.Min(255, upgrade.Value.Steps));
                        writer.WriteValueSafe(upgrade.Value.Choices);
                    }
                }

                Messaging.SendNamedMessage(StateMessage, clientId, writer, NetworkDelivery.ReliableFragmentedSequenced);
            }
        }

        // Something to tell a player: on the host shown here, else sent.
        private void Tell(ulong clientId, Notice notice, string skillId, int value)
        {
            if (clientId == networkManager.LocalClientId)
            {
                Show(notice, skillId, value);
                return;
            }

            using (var writer = new FastBufferWriter(256, Allocator.Temp, 4096))
            {
                writer.WriteValueSafe((byte)notice);
                writer.WriteValueSafe(skillId ?? "");
                writer.WriteValueSafe(value);
                Messaging.SendNamedMessage(NoticeMessage, clientId, writer, NetworkDelivery.ReliableFragmentedSequenced);
            }
        }

        // ---- Client ----

        // Asks to buy the next step of an upgrade, or one of its options (choice from 0).
        public void RequestBuy(string skillId, string upgradeId, int choice = -1)
        {
            if (!running) return;

            // Checked first, so that the player is told at once.
            catalog.TryGet(skillId, out SkillDefinition skill);
            LocalState.TryGet(skillId, out SkillProgress progress);
            SkillRefusal refusal = skill == null ? SkillRefusal.UnknownSkill : SkillRules.CanBuy(skill, progress, upgradeId, choice);
            if (refusal != SkillRefusal.None)
            {
                Show(Notice.Refused, skillId, (int)refusal);
                return;
            }

            if (IsServer)
            {
                Buy(networkManager.LocalClientId, skillId, upgradeId, choice);
                return;
            }

            using (var writer = new FastBufferWriter(256, Allocator.Temp, 4096))
            {
                writer.WriteValueSafe(skillId ?? "");
                writer.WriteValueSafe(upgradeId ?? "");
                writer.WriteValueSafe(choice);
                Messaging.SendNamedMessage(BuyMessage, NetworkManager.ServerClientId, writer, NetworkDelivery.ReliableFragmentedSequenced);
            }
        }

        private void OnState(ulong senderId, FastBufferReader reader)
        {
            reader.ReadValueSafe(out byte flags);
            options = SkillOptions.FromByte(flags);
            received.Clear();
            reader.ReadValueSafe(out int count);
            for (int i = 0; i < count; i++)
            {
                reader.ReadValueSafe(out string skillId);
                SkillProgress progress = received.Get(skillId);
                reader.ReadValueSafe(out progress.Unlocked);
                reader.ReadValueSafe(out progress.Xp);
                reader.ReadValueSafe(out int upgrades);
                for (int u = 0; u < upgrades; u++)
                {
                    reader.ReadValueSafe(out string upgradeId);
                    reader.ReadValueSafe(out byte steps);
                    reader.ReadValueSafe(out uint choices);
                    progress.Upgrades[upgradeId] = new UpgradeProgress { Steps = steps, Choices = choices };
                }
            }

            received.Touch();
            Changed?.Invoke();
        }

        private void OnNotice(ulong senderId, FastBufferReader reader)
        {
            reader.ReadValueSafe(out byte notice);
            reader.ReadValueSafe(out string skillId);
            reader.ReadValueSafe(out int value);
            Show((Notice)notice, skillId, value);
        }

        private void Show(Notice notice, string skillId, int value)
        {
            if (hotbar == null) return;

            catalog.TryGet(skillId, out SkillDefinition skill);
            string name = skill != null ? Loc.Name(skill.Name) : skillId;
            switch (notice)
            {
                case Notice.Unlocked:
                    hotbar.ShowMessage(Loc.T("Skill unlocked: {0}", name));
                    break;
                case Notice.TierReached:
                    int points = value >> 8;
                    hotbar.ShowMessage(points > 0
                        ? Loc.T("{0}: tier {1} reached · +{2} skill points", name, value & 0xFF, points)
                        : Loc.T("{0}: tier {1} reached", name, value & 0xFF));
                    break;
                case Notice.Points:
                    hotbar.ShowMessage(Loc.T("{0}: +{1} skill points", name, value));
                    break;
                case Notice.Refused:
                    hotbar.ShowMessage(TextOf((SkillRefusal)value));
                    break;
            }
        }

        public static string TextOf(SkillRefusal refusal)
        {
            switch (refusal)
            {
                case SkillRefusal.SkillLocked: return "This skill is not unlocked yet";
                case SkillRefusal.TierClosed: return "Reach this tier first";
                case SkillRefusal.RequiresMissing: return "Complete the required upgrades first";
                case SkillRefusal.ReplacesMissing: return "Buy the upgrade it improves first";
                case SkillRefusal.Complete: return "Already complete";
                case SkillRefusal.ChoiceTaken: return "Already chosen";
                case SkillRefusal.NoChoice: return "Choose an option";
                case SkillRefusal.NotEnoughPoints: return "Not enough skill points";
                default: return "You can't buy that";
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using Voxel.Core;

namespace Voxel.Client
{
    // What the local player is to the owner of a plot.
    public enum ClaimRelation : byte
    {
        Other = 0,
        Yours = 1,
        Allowed = 2
    }

    // Claimed land (ClaimRules, Claims). The server keeps who holds each plot and whom each owner
    // allows, saves it with the world (claims.dat), claims and releases plots when their players ask
    // (within their quota) and lets owners allow others by name. The other syncs ask it before any
    // action a player takes on the world (Allows / Check): on someone else's land nothing is broken,
    // placed, felled, harvested, planted, picked up or hunted but by its owner and those they allow.
    // Every machine knows each plot's owner by name and what the local player is to them (theirs,
    // allowed or not), to check first (LocalCheck), to say whose land it is and to draw the claims on
    // the maps (Layer).
    public class ClaimSync : MonoBehaviour
    {
        private const string AllMessage = "Claim.All";
        private const string ChangedMessage = "Claim.Changed";
        private const string RelationMessage = "Claim.Relation";
        private const string RequestMessage = "Claim.Request";
        private const string AccessMessage = "Claim.Access";
        private const string AccessRequestMessage = "Claim.AccessRequest";
        private const string AccessListMessage = "Claim.AccessList";
        private const string RefusedMessage = "Claim.Refused";
        private const string SaveFileName = "claims.dat";

        [SerializeField] private NetworkManager networkManager;
        [SerializeField] private WorldSync worldSync;

        // An owner as this machine knows them.
        private struct ShownOwner
        {
            public string Name;
            public ClaimRelation Relation;
        }

        // Every machine: each plot's owner (an index into shownOwners, -1 if free).
        private int[] shownPlots = Array.Empty<int>();
        private readonly Dictionary<int, ShownOwner> shownOwners = new Dictionary<int, ShownOwner>();
        private readonly List<AccessEntry> access = new List<AccessEntry>();

        // Server side.
        private Claims claims;
        private PlayerRegistry registry;
        private string savePath;

        private ClaimLayer layer;
        private bool running;

        private bool IsServer => claims != null;

        private CustomMessagingManager Messaging => networkManager.CustomMessagingManager;

        public int PlotsX { get; private set; }

        public int PlotsZ { get; private set; }

        // The players the local player may allow on their land (the named players connected, then
        // those allowed who are not), as the server last sent them.
        public IReadOnlyList<AccessEntry> Access => access;

        // Raised when a plot changed hands; with -1, -1 when every plot may have (a new list, an
        // owner who allowed or stopped allowing the local player).
        public event Action<int, int> PlotChanged;

        public event Action AccessChanged;

        // Raised on the player's machine with what was refused, as a text to show.
        public event Action<string> Refused;

        // The plots drawn over the maps (none on a server without a display).
        public ClaimLayer Layer
        {
            get
            {
                if (layer == null && !Application.isBatchMode) layer = new ClaimLayer(this);
                return layer;
            }
        }

        public static string TextOf(ClaimRefusal refusal, string ownerName)
        {
            switch (refusal)
            {
                case ClaimRefusal.Taken: return Loc.T("Already claimed by {0}", NameOrSomeone(ownerName));
                case ClaimRefusal.QuotaReached: return Loc.T("You hold {0} plots already, the most you can", ClaimRules.MaxPlotsPerPlayer);
                case ClaimRefusal.NotYours: return Loc.T("Not your plot");
                case ClaimRefusal.NoName: return Loc.T("Choose a name first");
                case ClaimRefusal.OutsideWorld: return Loc.T("Outside the world");
                default: return Loc.T("Claimed by {0}", NameOrSomeone(ownerName));
            }
        }

        private static string NameOrSomeone(string name) => string.IsNullOrEmpty(name) ? Loc.T("someone") : name;

        // To call right after the server or host has started, with the world's folder.
        internal void StartServer(string directory, PlayerRegistry playerRegistry)
        {
            running = true;
            registry = playerRegistry;
            World world = worldSync.World;
            savePath = Path.Combine(directory, SaveFileName);
            claims = Load(savePath, world.SizeX, world.SizeZ);
            Resize(claims.PlotsX, claims.PlotsZ);
            Messaging.RegisterNamedMessageHandler(RequestMessage, OnRequest);
            Messaging.RegisterNamedMessageHandler(AccessMessage, OnAccess);
            Messaging.RegisterNamedMessageHandler(AccessRequestMessage, OnAccessRequest);
            networkManager.OnClientConnectedCallback += OnClientConnected;
            ShowLocal();
        }

        // To call right after a client's connection has been launched.
        public void StartClient()
        {
            running = true;
            Messaging.RegisterNamedMessageHandler(AllMessage, OnAll);
            Messaging.RegisterNamedMessageHandler(ChangedMessage, OnChanged);
            Messaging.RegisterNamedMessageHandler(RelationMessage, OnRelation);
            Messaging.RegisterNamedMessageHandler(AccessListMessage, OnAccessList);
            Messaging.RegisterNamedMessageHandler(RefusedMessage, OnRefused);
        }

        public void Stop()
        {
            if (!running) return;

            if (IsServer) networkManager.OnClientConnectedCallback -= OnClientConnected;
            claims = null;
            registry = null;
            Resize(0, 0);
            access.Clear();
            running = false;
            PlotChanged?.Invoke(-1, -1);
        }

        // Server side: writes the claims next to the other saves.
        public void Save()
        {
            if (claims == null) return;

            try
            {
                claims.SaveToFile(savePath);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                Debug.LogError($"Failed to save the claims: {e.Message} ({savePath})");
            }
        }

        // ---- What every machine can ask ----

        // Whether the local player may act on a column (free land, theirs, or allowed there).
        public bool LocalAllows(int x, int z)
        {
            if (!TryGetPlotOwner(ClaimRules.PlotOf(x), ClaimRules.PlotOf(z), out ShownOwner owner)) return true;

            return owner.Relation != ClaimRelation.Other;
        }

        // Checks a column before the local player acts on it, and says whose land it is if not.
        public bool LocalCheck(int x, int z)
        {
            if (LocalAllows(x, z)) return true;

            TryDescribe(x, z, out string ownerName, out _);
            Refused?.Invoke(TextOf(ClaimRefusal.None, ownerName));
            return false;
        }

        // Whose plot holds a column, and what the local player is to them; false if it is free.
        public bool TryDescribe(int x, int z, out string ownerName, out ClaimRelation relation) =>
            TryDescribePlot(ClaimRules.PlotOf(x), ClaimRules.PlotOf(z), out ownerName, out relation);

        public bool TryDescribePlot(int plotX, int plotZ, out string ownerName, out ClaimRelation relation)
        {
            ownerName = "";
            relation = ClaimRelation.Other;
            if (!TryGetPlotOwner(plotX, plotZ, out ShownOwner owner)) return false;

            ownerName = owner.Name;
            relation = owner.Relation;
            return true;
        }

        // How many plots the local player holds.
        public int LocalCount
        {
            get
            {
                int count = 0;
                foreach (int owner in shownPlots)
                {
                    if (owner >= 0 && shownOwners.TryGetValue(owner, out ShownOwner shown) && shown.Relation == ClaimRelation.Yours) count++;
                }

                return count;
            }
        }

        // The local player claims a free plot or releases one of theirs. Checked here first, so that
        // a refusal is said at once.
        public void RequestToggle(int plotX, int plotZ)
        {
            if (!running || plotX < 0 || plotZ < 0 || plotX >= PlotsX || plotZ >= PlotsZ) return;

            bool claimed = TryDescribePlot(plotX, plotZ, out string ownerName, out ClaimRelation relation);
            if (claimed && relation != ClaimRelation.Yours)
            {
                Refused?.Invoke(TextOf(ClaimRefusal.Taken, ownerName));
                return;
            }

            if (!claimed && LocalCount >= ClaimRules.MaxPlotsPerPlayer)
            {
                Refused?.Invoke(TextOf(ClaimRefusal.QuotaReached, ""));
                return;
            }

            if (IsServer)
            {
                Toggle(networkManager.LocalClientId, plotX, plotZ, !claimed);
                return;
            }

            Send(RequestMessage, NetworkManager.ServerClientId, writer =>
            {
                writer.WriteValueSafe(plotX);
                writer.WriteValueSafe(plotZ);
                writer.WriteValueSafe(!claimed);
            });
        }

        // The local player allows a player (by name) on all their land, or no longer.
        public void RequestAccess(string playerName, bool allowed)
        {
            if (!running || string.IsNullOrEmpty(playerName)) return;

            if (IsServer)
            {
                SetAccess(networkManager.LocalClientId, playerName, allowed);
                return;
            }

            Send(AccessMessage, NetworkManager.ServerClientId, writer =>
            {
                writer.WriteValueSafe(playerName);
                writer.WriteValueSafe(allowed);
            });
        }

        // Asks for the local player's access list (who is connected changes while nobody looks).
        public void RequestAccessList()
        {
            if (!running) return;

            if (IsServer) SendAccessList(networkManager.LocalClientId);
            else Send(AccessRequestMessage, NetworkManager.ServerClientId, writer => writer.WriteValueSafe((byte)0));
        }

        private void LateUpdate()
        {
            layer?.Update();
        }

        private void OnDestroy()
        {
            layer?.Dispose();
        }

        // ---- Server: what the other syncs ask ----

        // Whether a player may act on a column. Always true outside the server role.
        public bool Allows(ulong clientId, int x, int z)
        {
            if (!IsServer) return true;

            registry.TryGetPlayerId(clientId, out string playerId);
            return claims.CanAct(playerId, x, z);
        }

        // Server side: raised when plots changed hands or an owner allowed or removed a player.
        internal event Action ClaimsChanged;

        // Server side: the player id of whoever claimed the plot holding a column, or null.
        internal string OwnerIdAt(int x, int z) => IsServer ? claims.OwnerAt(x, z)?.PlayerId : null;

        // Server side: the zone of the plot holding a column (Claims.ZoneOf), or -1 if it is free.
        internal int ZoneAt(int x, int z) => IsServer ? claims.ZoneOf(ClaimRules.PlotOf(x), ClaimRules.PlotOf(z)) : -1;

        // Whether someone claimed the plot holding a column (on the server its claims, elsewhere what
        // this machine was sent).
        public bool IsClaimed(int x, int z)
        {
            if (IsServer) return claims.OwnerAt(x, z) != null;

            return TryGetPlotOwner(ClaimRules.PlotOf(x), ClaimRules.PlotOf(z), out _);
        }

        // Server side: whether a player (by id) may act on a column (Claims.CanAct).
        internal bool CanAct(string playerId, int x, int z) => !IsServer || claims.CanAct(playerId, x, z);

        // Whether a player may act on a column; if not, tells them whose land it is.
        public bool Check(ulong clientId, int x, int z)
        {
            if (Allows(clientId, x, z)) return true;

            ClaimOwner owner = claims.OwnerAt(x, z);
            Refuse(clientId, ClaimRefusal.None, owner != null ? registry.NameOf(owner.PlayerId) : "");
            return false;
        }

        // ---- Server ----

        private void Toggle(ulong clientId, int plotX, int plotZ, bool claim)
        {
            if (!registry.TryGetPlayerId(clientId, out string playerId)) return;

            ClaimRefusal refusal;
            bool done;
            if (claim)
            {
                done = registry.NameOf(playerId).Length > 0
                    ? claims.TryClaim(playerId, plotX, plotZ, out refusal)
                    : Fail(ClaimRefusal.NoName, out refusal);
            }
            else
            {
                done = claims.TryRelease(playerId, plotX, plotZ, out refusal);
            }

            if (!done)
            {
                ClaimOwner holder = claims.OwnerOfPlot(plotX, plotZ);
                Refuse(clientId, refusal, holder != null ? registry.NameOf(holder.PlayerId) : "");
                return;
            }

            ClaimOwner owner = claims.OwnerOfPlot(plotX, plotZ);
            int plot = plotZ * claims.PlotsX + plotX;
            foreach (ulong other in networkManager.ConnectedClientsIds)
            {
                if (other == networkManager.LocalClientId)
                {
                    ShowPlot(plot, owner == null ? -1 : owner.Index, owner == null ? default : ShownOf(owner, other));
                    continue;
                }

                Send(ChangedMessage, other, writer =>
                {
                    writer.WriteValueSafe(plot);
                    writer.WriteValueSafe(owner == null ? -1 : owner.Index);
                    if (owner != null) WriteOwner(writer, ShownOf(owner, other));
                });
            }

            ClaimsChanged?.Invoke();
        }

        private static bool Fail(ClaimRefusal why, out ClaimRefusal refusal)
        {
            refusal = why;
            return false;
        }

        private void SetAccess(ulong clientId, string playerName, bool allowed)
        {
            if (!registry.TryGetPlayerId(clientId, out string playerId)) return;
            if (!registry.TryFindByName(playerName, out string otherId) || otherId == playerId) return;
            if (!claims.SetAllowed(playerId, otherId, allowed)) return;

            SendAccessList(clientId);
            ClaimsChanged?.Invoke();

            // What the other player is to this owner changed: their machine checks and draws it.
            claims.TryGetOwner(playerId, out ClaimOwner owner);
            foreach (KeyValuePair<ulong, string> player in registry.Connected)
            {
                if (player.Value != otherId) continue;

                ShownOwner shown = ShownOf(owner, player.Key);
                if (player.Key == networkManager.LocalClientId)
                {
                    shownOwners[owner.Index] = shown;
                    PlotChanged?.Invoke(-1, -1);
                    continue;
                }

                Send(RelationMessage, player.Key, writer =>
                {
                    writer.WriteValueSafe(owner.Index);
                    WriteOwner(writer, shown);
                });
            }
        }

        private void SendAccessList(ulong clientId)
        {
            if (!registry.TryGetPlayerId(clientId, out string playerId)) return;

            HashSet<string> allowed = claims.TryGetOwner(playerId, out ClaimOwner owner) ? owner.Allowed : new HashSet<string>();
            List<AccessEntry> entries = registry.AccessEntries(playerId, allowed);
            if (clientId == networkManager.LocalClientId)
            {
                ShowAccess(entries);
                return;
            }

            Send(AccessListMessage, clientId, writer => AccessEntry.WriteList(writer, entries));
        }

        private void Refuse(ulong clientId, ClaimRefusal refusal, string ownerName)
        {
            if (clientId == networkManager.LocalClientId)
            {
                Refused?.Invoke(TextOf(refusal, ownerName));
                return;
            }

            Send(RefusedMessage, clientId, writer =>
            {
                writer.WriteValueSafe((byte)refusal);
                writer.WriteValueSafe(ownerName ?? "");
            });
        }

        private ShownOwner ShownOf(ClaimOwner owner, ulong clientId)
        {
            registry.TryGetPlayerId(clientId, out string playerId);
            ClaimRelation relation = owner.PlayerId == playerId ? ClaimRelation.Yours
                : playerId != null && owner.Allowed.Contains(playerId) ? ClaimRelation.Allowed
                : ClaimRelation.Other;
            return new ShownOwner { Name = registry.NameOf(owner.PlayerId), Relation = relation };
        }

        // The host's own view of the claims.
        private void ShowLocal()
        {
            shownOwners.Clear();
            foreach (ClaimOwner owner in claims.Owners)
            {
                shownOwners[owner.Index] = ShownOf(owner, networkManager.LocalClientId);
            }

            for (int i = 0; i < shownPlots.Length; i++)
            {
                shownPlots[i] = -1;
            }

            foreach ((int plotX, int plotZ, ClaimOwner owner) in claims.Claimed())
            {
                shownPlots[plotZ * PlotsX + plotX] = owner.Index;
            }

            PlotChanged?.Invoke(-1, -1);
        }

        // A joining player is sent every claimed plot and what they are to each owner.
        private void OnClientConnected(ulong clientId)
        {
            if (clientId == networkManager.LocalClientId)
            {
                // The host's player may be known only now.
                ShowLocal();
                return;
            }

            var claimed = new List<(int plot, int owner)>();
            var owners = new HashSet<int>();
            foreach ((int plotX, int plotZ, ClaimOwner owner) in claims.Claimed())
            {
                claimed.Add((plotZ * claims.PlotsX + plotX, owner.Index));
                owners.Add(owner.Index);
            }

            Send(AllMessage, clientId, writer =>
            {
                writer.WriteValueSafe(claims.PlotsX);
                writer.WriteValueSafe(claims.PlotsZ);
                writer.WriteValueSafe(owners.Count);
                foreach (int index in owners)
                {
                    writer.WriteValueSafe(index);
                    WriteOwner(writer, ShownOf(claims.Owners[index], clientId));
                }

                writer.WriteValueSafe(claimed.Count);
                foreach ((int plot, int owner) in claimed)
                {
                    writer.WriteValueSafe(plot);
                    writer.WriteValueSafe(owner);
                }
            });
        }

        private static void WriteOwner(FastBufferWriter writer, ShownOwner owner)
        {
            writer.WriteValueSafe(owner.Name ?? "");
            writer.WriteValueSafe((byte)owner.Relation);
        }

        private static ShownOwner ReadOwner(FastBufferReader reader)
        {
            reader.ReadValueSafe(out string name);
            reader.ReadValueSafe(out byte relation);
            return new ShownOwner { Name = name, Relation = relation <= (byte)ClaimRelation.Allowed ? (ClaimRelation)relation : ClaimRelation.Other };
        }

        // ---- Handlers ----

        private void OnRequest(ulong senderId, FastBufferReader reader)
        {
            reader.ReadValueSafe(out int plotX);
            reader.ReadValueSafe(out int plotZ);
            reader.ReadValueSafe(out bool claim);
            Toggle(senderId, plotX, plotZ, claim);
        }

        private void OnAccess(ulong senderId, FastBufferReader reader)
        {
            reader.ReadValueSafe(out string name);
            reader.ReadValueSafe(out bool allowed);
            SetAccess(senderId, name, allowed);
        }

        private void OnAccessRequest(ulong senderId, FastBufferReader reader) => SendAccessList(senderId);

        private void OnAll(ulong senderId, FastBufferReader reader)
        {
            reader.ReadValueSafe(out int plotsX);
            reader.ReadValueSafe(out int plotsZ);
            if (plotsX < 0 || plotsZ < 0 || (long)plotsX * plotsZ > 1 << 24) return;

            Resize(plotsX, plotsZ);
            reader.ReadValueSafe(out int ownerCount);
            for (int i = 0; i < ownerCount; i++)
            {
                reader.ReadValueSafe(out int index);
                shownOwners[index] = ReadOwner(reader);
            }

            reader.ReadValueSafe(out int count);
            for (int i = 0; i < count; i++)
            {
                reader.ReadValueSafe(out int plot);
                reader.ReadValueSafe(out int owner);
                if (plot >= 0 && plot < shownPlots.Length) shownPlots[plot] = owner;
            }

            PlotChanged?.Invoke(-1, -1);
        }

        private void OnChanged(ulong senderId, FastBufferReader reader)
        {
            reader.ReadValueSafe(out int plot);
            reader.ReadValueSafe(out int owner);
            ShownOwner shown = owner >= 0 ? ReadOwner(reader) : default;
            ShowPlot(plot, owner, shown);
        }

        private void OnRelation(ulong senderId, FastBufferReader reader)
        {
            reader.ReadValueSafe(out int owner);
            shownOwners[owner] = ReadOwner(reader);
            PlotChanged?.Invoke(-1, -1);
        }

        private void OnAccessList(ulong senderId, FastBufferReader reader) => ShowAccess(AccessEntry.ReadList(reader));

        private void OnRefused(ulong senderId, FastBufferReader reader)
        {
            reader.ReadValueSafe(out byte refusal);
            reader.ReadValueSafe(out string ownerName);
            Refused?.Invoke(TextOf((ClaimRefusal)refusal, ownerName));
        }

        // ---- Shown state ----

        private void Resize(int plotsX, int plotsZ)
        {
            PlotsX = plotsX;
            PlotsZ = plotsZ;
            shownPlots = new int[plotsX * plotsZ];
            for (int i = 0; i < shownPlots.Length; i++)
            {
                shownPlots[i] = -1;
            }

            shownOwners.Clear();
        }

        private void ShowPlot(int plot, int owner, ShownOwner shown)
        {
            if (plot < 0 || plot >= shownPlots.Length) return;

            shownPlots[plot] = owner;
            if (owner >= 0) shownOwners[owner] = shown;
            PlotChanged?.Invoke(plot % PlotsX, plot / PlotsX);
        }

        private void ShowAccess(List<AccessEntry> entries)
        {
            access.Clear();
            access.AddRange(entries);
            AccessChanged?.Invoke();
        }

        private bool TryGetPlotOwner(int plotX, int plotZ, out ShownOwner owner)
        {
            owner = default;
            if (!running || plotX < 0 || plotZ < 0 || plotX >= PlotsX || plotZ >= PlotsZ) return false;

            int index = shownPlots[plotZ * PlotsX + plotX];
            return index >= 0 && shownOwners.TryGetValue(index, out owner);
        }

        // The owner index of a plot as shown (-1 if free), for the map layer.
        internal int ShownOwnerIndex(int plotX, int plotZ)
        {
            if (plotX < 0 || plotZ < 0 || plotX >= PlotsX || plotZ >= PlotsZ) return -1;

            return shownPlots[plotZ * PlotsX + plotX];
        }

        internal bool TryGetShownOwner(int index, out string name, out ClaimRelation relation)
        {
            name = "";
            relation = ClaimRelation.Other;
            if (!shownOwners.TryGetValue(index, out ShownOwner owner)) return false;

            name = owner.Name;
            relation = owner.Relation;
            return true;
        }

        // ---- Sending ----

        private void Send(string message, ulong clientId, Action<FastBufferWriter> write)
        {
            using (var writer = new FastBufferWriter(256, Allocator.Temp, 1 << 20))
            {
                write(writer);
                Messaging.SendNamedMessage(message, clientId, writer, NetworkDelivery.ReliableFragmentedSequenced);
            }
        }

        private static Claims Load(string path, int columnsX, int columnsZ)
        {
            Claims fresh = Claims.ForWorld(columnsX, columnsZ);
            if (!File.Exists(path)) return fresh;

            try
            {
                Claims loaded = Claims.LoadFromFile(path);
                if (loaded.PlotsX == fresh.PlotsX && loaded.PlotsZ == fresh.PlotsZ) return loaded;

                Debug.LogWarning($"The claims were saved for a world of another size ({loaded.PlotsX} x {loaded.PlotsZ} plots): the world starts without any.");
            }
            catch (Exception e) when (e is IOException || e is InvalidDataException || e is UnauthorizedAccessException)
            {
                Debug.LogWarning($"The claims could not be loaded ({e.Message}): the world starts without any.");
            }

            return fresh;
        }
    }
}

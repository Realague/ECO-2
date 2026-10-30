using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using Voxel.Core;

namespace Voxel.Client
{
    // An order on the open station's queue, as its window shows it.
    public sealed class StationJobView
    {
        public int Id;
        public int Recipe;
        public int Count;
        public int Stocked;
        public float LabourLeft;
        public float SecondsLeft;
        public string Creator;

        // Its recipe, in the station's own list.
        public Recipe Made;

        // Whether the work of the one at the front of the order is all in.
        public bool UnitPaid => LabourLeft <= Made.Calories * (Count - 1) + 1e-3f;
    }

    // Stations (Station; StorageKind.Campfire, which cooks dishes, Recipes.Cooking, and
    // StorageKind.ButcherTable, which cuts carcasses up, Recipes.Butchery): placed, owned, opened and
    // picked up like storage. A player queues an order of so many of a recipe, the ingredients of as
    // many as can be taken at once from their bag then from the linked chests (the chests of the
    // station's claimed zone; off claimed land, the public chests within LinkRange), the others taken
    // from the linked chests when their turn comes; puts calories into it (LabourStep at a time, or all
    // it needs and they have); lays wood by a campfire and takes it back; and moves items between their
    // bag and the linked chests from the station's window. The server ticks every station each frame:
    // the first order whose work is in and whose ingredients are there is made while (on a campfire)
    // there is wood and the linked chests have room for all it makes, which goes into them when done.
    // Whoever has a station open gets its state when it changes (Fire.State) and counts the work down
    // itself; every machine knows whether it works (Store.State's flag), to draw a campfire's flames.
    public partial class StorageSync
    {
        // What a station of a kind makes.
        public static IReadOnlyList<Recipe> RecipesOf(StorageKind kind)
        {
            switch (kind)
            {
                case StorageKind.ButcherTable: return Recipes.Butchery;
                case StorageKind.CarpentryTable: return Recipes.Carpentry;
                default: return Recipes.Cooking;
            }
        }

        // The choices a queued order carries: one per ingredient, at most two (Fire.Queue's spare ints;
        // no station's recipe has a group past its second ingredient).
        public const int MaxChoices = 2;

        // Fire.Move's flags.
        private const int MoveDeposit = 1;
        private const int MoveByItem = 2;
        private const int MoveStockpiles = 4;

        private const string FuelMessage = "Fire.Fuel";
        private const string TakeFuelMessage = "Fire.TakeFuel";
        private const string QueueMessage = "Fire.Queue";
        private const string LabourMessage = "Fire.Labour";
        private const string CancelJobMessage = "Fire.Cancel";
        private const string FireMoveMessage = "Fire.Move";
        private const string FireStateMessage = "Fire.State";

        // How far from a station off claimed land the public storage it uses stands, in blocks (to the
        // nearest of its cells).
        public const float LinkRange = 8f;

        // The calories a click puts into an order (Shift: all it needs, as far as the player has them).
        public const float LabourStep = 100f;

        // Server side: what each station's viewers were last sent, to send it again only when it changed.
        private readonly Dictionary<int, long> fireSent = new Dictionary<int, long>();
        private readonly List<Storage> fires = new List<Storage>();

        // The open station as the server last sent it.
        private readonly List<StationJobView> openedJobs = new List<StationJobView>();
        private readonly Inventory openedLinked = new Inventory();
        private readonly List<StorageMember> openedLinkedMembers = new List<StorageMember>();
        private readonly Inventory openedLinkedPiled = new Inventory();
        private readonly List<StorageMember> openedLinkedStockpiles = new List<StorageMember>();
        private readonly Inventory openedLinkedAll = new Inventory();
        private readonly Inventory openedFuel = new Inventory();
        private double openedFireTime;

        public IReadOnlyList<StationJobView> OpenedJobs => openedJobs;

        // What the linked chests of the open station hold together, and each of them.
        public Inventory OpenedLinkedContents => openedLinked;

        public IReadOnlyList<StorageMember> OpenedLinkedMembers => openedLinkedMembers;

        public int OpenedLinkedChests => openedLinkedMembers.Count;

        // Its linked stockpiles, and what they hold together.
        public IReadOnlyList<StorageMember> OpenedLinkedStockpiles => openedLinkedStockpiles;

        public Inventory OpenedLinkedPiled => openedLinkedPiled;

        // What all its linked storage holds, for the ingredients it shows.
        public Inventory OpenedLinkedAll => openedLinkedAll;

        public int OpenedLinkedStorage => openedLinkedMembers.Count + openedLinkedStockpiles.Count;

        // The wood by the open campfire, by kind, and how many logs in all.
        public Inventory OpenedFuelContents => openedFuel;

        public int OpenedFuel { get; private set; }

        public StationStatus OpenedStatus { get; private set; }

        // How long the log alight still burns, counted down while it cooks.
        public float OpenedBurning => OpenedStatus == StationStatus.Working ? Mathf.Max(0f, openedBurning - SinceFireState) : openedBurning;

        // How long the log alight burns in all, for its bar.
        public float OpenedLogSeconds { get; private set; } = Fuels.WoodSeconds;

        // How long all the fuel by the open campfire would burn, the log alight included.
        public float OpenedFuelSeconds
        {
            get
            {
                float seconds = OpenedBurning;
                foreach (KeyValuePair<int, int> entry in openedFuel.Entries)
                {
                    seconds += Fuels.SecondsOf(entry.Key) * entry.Value;
                }

                return seconds;
            }
        }

        private float openedBurning;

        private float SinceFireState => (float)(Time.unscaledTimeAsDouble - openedFireTime);

        // How long the one at the front of an order of the open station still takes: counted down for
        // the one being made.
        public float SecondsLeftOf(StationJobView job)
        {
            if (OpenedStatus != StationStatus.Working || job != CookingJob) return job.SecondsLeft;

            return Mathf.Max(0f, job.SecondsLeft - SinceFireState);
        }

        // The order of the open station being made, or that would be (Station.Next): one that has
        // started, else the first whose front one is paid for and has its ingredients.
        public StationJobView CookingJob
        {
            get
            {
                foreach (StationJobView job in openedJobs)
                {
                    if (job.UnitPaid && job.Stocked > 0 && job.SecondsLeft < job.Made.Seconds - 1e-4f) return job;
                }

                foreach (StationJobView job in openedJobs)
                {
                    if (job.UnitPaid && job.Stocked > 0) return job;
                }

                return null;
            }
        }

        public bool TryKindOf(int storageId, out StorageKind kind)
        {
            kind = StorageKind.Chest;
            if (!running || !shown.TryGet(storageId, out Storage storage)) return false;

            kind = storage.Kind;
            return true;
        }

        // Whether a campfire burns now (it cooks), as every machine knows.
        public bool IsBurning(int storageId) => details.TryGetValue(storageId, out Shown detail) && detail.Open;

        // ---- Requests from the local player, about the open station ----

        public void RequestFuel(int wood, int count) => SendFire(FuelMessage, wood, count);

        public void RequestTakeFuel(int wood, int count) => SendFire(TakeFuelMessage, wood, count);

        // Queues an order, with what goes in for each ingredient of a group (Items.None: Auto).
        public void RequestQueue(int recipe, int count, IReadOnlyList<int> choices = null)
        {
            int first = choices != null && choices.Count > 0 ? choices[0] : Items.None;
            int second = choices != null && choices.Count > 1 ? choices[1] : Items.None;
            SendFire(QueueMessage, recipe, count, first, second);
        }

        public void RequestLabour(int jobId, bool all) => SendFire(LabourMessage, jobId, all ? 1 : 0);

        public void RequestCancelJob(int jobId) => SendFire(CancelJobMessage, jobId, 0);

        // Moves items between the bag and the open station's linked chests, or its linked stockpiles
        // (member: one of them, or 0 for them all; from stockpiles, itemOrPile is a pile unless byItem).
        public void RequestFireMove(bool deposit, int itemOrPile, int count, int member, bool stockpiles = false, bool byItem = true)
        {
            int flags = (deposit ? MoveDeposit : 0) | (byItem ? MoveByItem : 0) | (stockpiles ? MoveStockpiles : 0);
            SendFire(FireMoveMessage, itemOrPile, count, flags, member);
        }

        private void SendFire(string message, int a, int b, int c = 0, int d = 0)
        {
            if (!running || OpenedId == 0) return;

            if (IsServer)
            {
                OnFire(message, networkManager.LocalClientId, OpenedId, a, b, c, d);
                return;
            }

            int id = OpenedId;
            Send(message, NetworkManager.ServerClientId, writer =>
            {
                writer.WriteValueSafe(id);
                writer.WriteValueSafe(a);
                writer.WriteValueSafe(b);
                writer.WriteValueSafe(c);
                writer.WriteValueSafe(d);
            });
        }

        private void StartStationServer()
        {
            foreach (string message in new[] { FuelMessage, TakeFuelMessage, QueueMessage, LabourMessage, CancelJobMessage, FireMoveMessage })
            {
                Messaging.RegisterNamedMessageHandler(message, (sender, reader) => ReadFire(message, sender, reader));
            }
        }

        private void StartStationClient()
        {
            Messaging.RegisterNamedMessageHandler(FireStateMessage, OnFireState);
        }

        private void StopStations()
        {
            fireSent.Clear();
            openedJobs.Clear();
            openedLinked.Clear();
            openedLinkedMembers.Clear();
            openedLinkedStockpiles.Clear();
            openedLinkedPiled.Clear();
            openedLinkedAll.Clear();
            openedFuel.Clear();
        }

        private void ReadFire(string message, ulong senderId, FastBufferReader reader)
        {
            reader.ReadValueSafe(out int id);
            reader.ReadValueSafe(out int a);
            reader.ReadValueSafe(out int b);
            reader.ReadValueSafe(out int c);
            reader.ReadValueSafe(out int d);
            OnFire(message, senderId, id, a, b, c, d);
        }

        // ---- Server ----

        private void OnFire(string message, ulong clientId, int storageId, int a, int b, int c, int d)
        {
            if (!store.TryGet(storageId, out Storage fire) || !StorageRules.IsStation(fire.Kind)) return;
            if (!registry.TryGetPlayerId(clientId, out string playerId) || !registry.TryGetState(clientId, out PlayerState state)) return;
            if (!CanUse(playerId, fire) || !InReach(clientId, fire, 1f)) return;

            switch (message)
            {
                case FuelMessage:
                    if (fire.Station.BurnsFuel) AddFuel(clientId, state, fire, a, b);
                    break;
                case TakeFuelMessage:
                    if (fire.Station.BurnsFuel) TakeFuel(clientId, fire, a, b);
                    break;
                case QueueMessage:
                    QueueOrder(clientId, playerId, state, fire, a, b, new[] { c, d });
                    break;
                case LabourMessage:
                    AddLabour(clientId, state, fire, a, b != 0);
                    break;
                case CancelJobMessage:
                    CancelOrder(clientId, state, fire, a);
                    break;
                case FireMoveMessage:
                    // Into or out of the linked chests, or the linked stockpiles.
                    List<Storage> linked = LinkedStorage(fire);
                    Split(linked, out List<Storage> chests, out List<Storage> piles);
                    bool toPiles = (c & MoveStockpiles) != 0;
                    List<Storage> side = toPiles ? piles : chests;
                    if (side.Count > 0 && MoveWithin(clientId, side, !toPiles, (c & MoveDeposit) != 0, a, b, d, (c & MoveByItem) != 0)) Changed(side[0], linked);
                    break;
            }

            SendFireToViewers(fire, true);
        }

        private void AddFuel(ulong clientId, PlayerState state, Storage fire, int wood, int count)
        {
            if (!Fuels.IsFuel(wood) || count <= 0) return;

            int room = Station.MaxFuel - fire.Station.FuelCount;
            if (room <= 0)
            {
                Refuse(clientId, StorageRefusal.FuelFull);
                return;
            }

            int moved = Math.Min(count, Math.Min(room, state.Inventory.Count(wood)));
            if (moved <= 0 || !inventorySync.TryConsume(clientId, wood, moved)) return;

            fire.Station.AddFuel(wood, moved);
        }

        // Wood back to the bag, what it has room for.
        private void TakeFuel(ulong clientId, Storage fire, int wood, int count)
        {
            if (count <= 0 || fire.Station.Fuel.Count(wood) <= 0) return;

            int room = inventorySync.RoomFor(clientId, wood);
            if (room <= 0)
            {
                inventorySync.TellFull(clientId, InventorySync.ReasonFor(wood));
                return;
            }

            int moved = fire.Station.TakeFuel(wood, Math.Min(count, room));
            if (moved > 0) inventorySync.Give(clientId, wood, moved);
        }

        // The ingredients of as many of the order as can be had now, whole (Recipes.Pick over the bag
        // and the linked storage together, with the player's choices for groups), from the bag first,
        // then the linked chests, then the linked stockpiles; the others are taken from the linked
        // storage when their turn comes.
        private void QueueOrder(ulong clientId, string playerId, PlayerState state, Storage fire, int recipeIndex, int count, int[] choices)
        {
            if (recipeIndex < 0 || recipeIndex >= fire.Station.Recipes.Count || count < 1 || count > Station.MaxCount) return;
            if (fire.Station.Jobs.Count >= Station.MaxJobs)
            {
                Refuse(clientId, StorageRefusal.QueueFull);
                return;
            }

            Recipe recipe = fire.Station.Recipes[recipeIndex];
            for (int i = 0; i < choices.Length; i++)
            {
                if (choices[i] == Items.None) continue;
                if (i >= recipe.Ingredients.Count || !recipe.Ingredients[i].IsGroup || !recipe.Ingredients[i].Matches(choices[i])) return;
            }

            List<Storage> linked = LinkedStorage(fire);
            Inventory available = Available(linked);
            AddAll(available, state.Inventory);
            int stocked = count;
            List<(int Item, int Count)> taken = null;
            while (stocked > 0 && (taken = Recipes.Pick(available, recipe, choices, stocked)) == null)
            {
                stocked--;
            }

            bool storageChanged = false;
            if (taken != null)
            {
                foreach ((int item, int needed) in taken)
                {
                    int fromBag = Math.Min(needed, state.Inventory.Count(item));
                    if (fromBag > 0) inventorySync.TryConsume(clientId, item, fromBag);
                    if (needed > fromBag)
                    {
                        TakeFrom(linked, item, needed - fromBag);
                        storageChanged = true;
                    }
                }
            }

            fire.Station.Queue(recipeIndex, count, playerId, choices, stocked, taken);
            if (storageChanged) Changed(linked[0], linked);
        }

        // What linked storage holds together: the chests' items and the stockpiles' blocks.
        private static Inventory Available(List<Storage> linked)
        {
            var available = new Inventory();
            foreach (Storage storage in linked)
            {
                if (storage.Kind == StorageKind.Chest)
                {
                    AddAll(available, storage.Contents);
                    continue;
                }

                for (int pile = 0; pile < storage.Piles.Count; pile++)
                {
                    int count = storage.Piles.CountAt(pile);
                    if (count > 0) available.Add(storage.Piles.ItemAt(pile), count);
                }
            }

            return available;
        }

        // Takes an item from linked storage: the chests first, then the stockpiles.
        private static void TakeFrom(List<Storage> linked, int item, int count)
        {
            Split(linked, out List<Storage> chests, out List<Storage> piles);
            int left = count - StoragePool.TakeFromChests(chests, item, count);
            if (left > 0) StoragePool.TakeFromStockpiles(piles, item, left);
        }

        // Linked storage by kind: the chests, the stockpiles.
        private static void Split(List<Storage> linked, out List<Storage> chests, out List<Storage> piles)
        {
            chests = new List<Storage>();
            piles = new List<Storage>();
            foreach (Storage storage in linked)
            {
                if (storage.Kind == StorageKind.Chest) chests.Add(storage);
                else piles.Add(storage);
            }
        }

        // Whether linked storage has room for all of these together, each where it goes (a chest's
        // items in the chests, blocks and wood on the stockpiles).
        private bool HasRoomFor(List<Storage> linked, IEnumerable<(int Item, int Count)> items)
        {
            Split(linked, out List<Storage> chests, out List<Storage> piles);
            var forChests = new List<(int Item, int Count)>();
            var forPiles = new List<(int Item, int Count)>();
            foreach ((int item, int count) in items)
            {
                if (StorageRules.AcceptsInChest(item)) forChests.Add((item, count));
                else forPiles.Add((item, count));
            }

            return (forChests.Count == 0 || (chests.Count > 0 && StoragePool.ChestRoomFor(chests, forChests, ecosystemSync.ItemWeights)))
                && (forPiles.Count == 0 || (piles.Count > 0 && StoragePool.StockpileRoomFor(piles, forPiles)));
        }

        // Puts items into linked storage, each where it goes.
        private void PutInto(List<Storage> linked, int item, int count)
        {
            Split(linked, out List<Storage> chests, out List<Storage> piles);
            if (StorageRules.AcceptsInChest(item)) StoragePool.AddToChests(chests, item, count, ecosystemSync.ItemWeights);
            else StoragePool.AddToStockpiles(piles, item, count);
        }

        private void AddLabour(ulong clientId, PlayerState state, Storage fire, int jobId, bool all)
        {
            if (!fire.Station.TryGetJob(jobId, out StationJob job) || job.HasLabour) return;
            if (state.Stomach.IsStarving)
            {
                Refuse(clientId, StorageRefusal.TooHungry);
                return;
            }

            // A creative player's work costs them nothing.
            float wanted = all ? job.LabourLeft : Mathf.Min(LabourStep, job.LabourLeft);
            float added = fire.Station.AddLabour(jobId, state.Creative ? wanted : Mathf.Min(wanted, state.Stomach.Calories));
            if (added > 0f && !state.Creative) state.Stomach.Burn(added);
        }

        // What was taken and not made goes back to the bag, then to the linked storage; all of it, or
        // the order stays.
        private void CancelOrder(ulong clientId, PlayerState state, Storage fire, int jobId)
        {
            if (!fire.Station.TryGetJob(jobId, out StationJob job)) return;

            List<Storage> linked = LinkedStorage(fire);
            var toBag = new List<KeyValuePair<int, int>>();
            var toStorage = new List<(int Item, int Count)>();
            var bag = new Inventory();
            bag.CopyFrom(state.Inventory);
            foreach (KeyValuePair<int, int> item in job.Held())
            {
                int fits = Math.Min(item.Value, CarryLimits.Room(bag, item.Key, ecosystemSync.ItemWeights));
                if (fits > 0)
                {
                    bag.Add(item.Key, fits);
                    toBag.Add(new KeyValuePair<int, int>(item.Key, fits));
                }

                if (item.Value > fits) toStorage.Add((item.Key, item.Value - fits));
            }

            // What the bag cannot take must fit in the linked storage, checked on copies.
            if (toStorage.Count > 0 && !HasRoomFor(linked, toStorage))
            {
                Refuse(clientId, StorageRefusal.NoRoomToGiveBack);
                return;
            }

            fire.Station.Remove(jobId);
            foreach (KeyValuePair<int, int> item in toBag)
            {
                inventorySync.Give(clientId, item.Key, item.Value);
            }

            foreach ((int item, int count) in toStorage)
            {
                PutInto(linked, item, count);
            }

            if (toStorage.Count > 0 && linked.Count > 0) Changed(linked[0], linked);
        }

        // A station picked up: nothing may be queued, and a campfire's wood goes back to the bag, all of it.
        private bool TryEmptyStation(ulong clientId, Storage fire)
        {
            Station station = fire.Station;
            if (station.Jobs.Count > 0 || !registry.TryGetState(clientId, out PlayerState state)) return false;

            var bag = new Inventory();
            bag.CopyFrom(state.Inventory);
            bag.Add(StorageRules.ItemOf(fire.Kind), 1);
            foreach (KeyValuePair<int, int> wood in station.Fuel.Entries)
            {
                if (CarryLimits.Room(bag, wood.Key, ecosystemSync.ItemWeights) < wood.Value) return false;

                bag.Add(wood.Key, wood.Value);
            }

            var logs = new List<KeyValuePair<int, int>>(station.Fuel.Entries);
            station.Fuel.Clear();
            foreach (KeyValuePair<int, int> wood in logs)
            {
                inventorySync.Give(clientId, wood.Key, wood.Value);
            }

            return true;
        }

        // The storage a station takes ingredients from and puts what it makes into, chests and
        // stockpiles: those of its claimed zone; off claimed land, the public ones within LinkRange (of
        // their nearest cell). In id order.
        private List<Storage> LinkedStorage(Storage fire)
        {
            var linked = new List<Storage>();
            bool claimed = TryLandOf(fire, out int x, out int z);
            int zone = claimed ? claimSync.ZoneAt(x, z) : -1;
            foreach (Storage other in store.All)
            {
                if (StorageRules.IsStation(other.Kind)) continue;

                if (claimed)
                {
                    if (TryLandOf(other, out int ox, out int oz) && claimSync.ZoneAt(ox, oz) == zone) linked.Add(other);
                }
                else if (IsPublic(other) && IsWithinLinkRange(fire, other))
                {
                    linked.Add(other);
                }
            }

            linked.Sort((a, b) => a.Id.CompareTo(b.Id));
            return linked;
        }

        private static bool IsWithinLinkRange(Storage fire, Storage other)
        {
            for (int cell = 0; cell < other.CellCount; cell++)
            {
                BlockPos at = other.Cell(cell);
                float dx = at.X - fire.Position.X;
                float dy = at.Y - fire.Position.Y;
                float dz = at.Z - fire.Position.Z;
                if (dx * dx + dy * dy + dz * dz <= LinkRange * LinkRange) return true;
            }

            return false;
        }

        // One's ingredients from the linked storage, all or nothing (the order's choices pick them):
        // what was taken, or null.
        private static List<(int Item, int Count)> TakeIngredients(List<Storage> linked, StationJob job)
        {
            List<(int Item, int Count)> unit = Recipes.Pick(Available(linked), job.Made, job.Choices, 1);
            if (unit == null) return null;

            foreach ((int item, int count) in unit)
            {
                TakeFrom(linked, item, count);
            }

            return unit;
        }

        // Every frame: each station with something queued works; what it made goes into the linked
        // storage, each thing where it goes.
        private void TickStations(float seconds)
        {
            fires.Clear();
            foreach (Storage storage in store.All)
            {
                if (StorageRules.IsStation(storage.Kind) && (storage.Station.Jobs.Count > 0 || storage.Station.Status != StationStatus.Idle)) fires.Add(storage);
            }

            foreach (Storage fire in fires)
            {
                List<Storage> linked = LinkedStorage(fire);
                bool storageChanged = false;
                StationJob done = fire.Station.Tick(seconds,
                    made => HasRoomFor(linked, made.Outputs),
                    job =>
                    {
                        List<(int Item, int Count)> taken = TakeIngredients(linked, job);
                        storageChanged |= taken != null;
                        return taken;
                    });
                if (done != null)
                {
                    foreach ((int item, int count) in done.Made.Outputs)
                    {
                        PutInto(linked, item, count);
                    }

                    storageChanged = true;
                }

                if (storageChanged && linked.Count > 0) Changed(linked[0], linked);
                SetOpen(fire, fire.Station.Status == StationStatus.Working);
                SendFireToViewers(fire, false);
            }
        }

        // Stations open in a window whose linked storage is among these: their viewers get them again.
        private void RefreshStationsLinkedTo(List<Storage> storage)
        {
            foreach (KeyValuePair<int, HashSet<ulong>> watched in viewers)
            {
                if (watched.Value.Count == 0 || !store.TryGet(watched.Key, out Storage fire) || !StorageRules.IsStation(fire.Kind)) continue;

                List<Storage> linked = LinkedStorage(fire);
                if (linked.Exists(storage.Contains)) SendFireToViewers(fire, true);
            }
        }

        // Sends a station's state to whoever has it open: always (after a request), or when what they
        // see changed (status, wood, the queue).
        private void SendFireToViewers(Storage fire, bool always)
        {
            Station station = fire.Station;
            long key = (long)station.Status * 1_000_003 + station.FuelCount * 7919 + station.Jobs.Count * 31;
            foreach (StationJob job in station.Jobs)
            {
                key = key * 31 + job.Id;
                key = key * 31 + job.Count;
                key = key * 31 + job.Stocked;
            }

            if (!always && fireSent.TryGetValue(fire.Id, out long sent) && sent == key) return;

            fireSent[fire.Id] = key;
            if (!viewers.TryGetValue(fire.Id, out HashSet<ulong> watching)) return;

            foreach (ulong viewer in watching)
            {
                SendStation(viewer, fire);
            }
        }

        private void SendStation(ulong clientId, Storage fire)
        {
            Station station = fire.Station;
            bool owner = registry.TryGetPlayerId(clientId, out string playerId) && playerId == OwnerOf(fire);
            List<Storage> linked = LinkedStorage(fire);
            if (clientId == networkManager.LocalClientId)
            {
                var jobs = new List<StationJobView>();
                foreach (StationJob job in station.Jobs)
                {
                    jobs.Add(ViewOf(job));
                }

                ShowStation(fire.Id, fire.Kind, owner, station.Status, station.Fuel, station.Burning, station.LogSeconds, MembersOf(linked), jobs);
                return;
            }

            Send(FireStateMessage, clientId, writer =>
            {
                writer.WriteValueSafe(fire.Id);
                writer.WriteValueSafe(owner);
                writer.WriteValueSafe((byte)fire.Kind);
                writer.WriteValueSafe((byte)station.Status);
                writer.WriteValueSafe(station.Burning);
                writer.WriteValueSafe(station.LogSeconds);
                writer.WriteValueSafe(station.Fuel.DistinctCount);
                foreach (KeyValuePair<int, int> entry in station.Fuel.Entries)
                {
                    writer.WriteValueSafe(entry.Key);
                    writer.WriteValueSafe(entry.Value);
                }

                WriteMembers(writer, linked);
                writer.WriteValueSafe(station.Jobs.Count);
                foreach (StationJob job in station.Jobs)
                {
                    writer.WriteValueSafe(job.Id);
                    writer.WriteValueSafe((byte)job.Recipe);
                    writer.WriteValueSafe((byte)job.Count);
                    writer.WriteValueSafe((byte)job.Stocked);
                    writer.WriteValueSafe(job.LabourLeft);
                    writer.WriteValueSafe(job.SecondsLeft);
                    writer.WriteValueSafe(registry.NameOf(job.CreatorId));
                }
            });
        }

        private StationJobView ViewOf(StationJob job)
        {
            return new StationJobView
            {
                Id = job.Id,
                Recipe = job.Recipe,
                Made = job.Made,
                Count = job.Count,
                Stocked = job.Stocked,
                LabourLeft = job.LabourLeft,
                SecondsLeft = job.SecondsLeft,
                Creator = registry.NameOf(job.CreatorId)
            };
        }

        // ---- Client ----

        private void OnFireState(ulong senderId, FastBufferReader reader)
        {
            reader.ReadValueSafe(out int id);
            reader.ReadValueSafe(out bool owner);
            reader.ReadValueSafe(out byte kindByte);
            var kind = (StorageKind)kindByte;
            if (!StorageRules.IsStation(kind)) return;

            IReadOnlyList<Recipe> recipes = RecipesOf(kind);
            reader.ReadValueSafe(out byte status);
            reader.ReadValueSafe(out float burning);
            reader.ReadValueSafe(out float logSeconds);
            reader.ReadValueSafe(out int woods);
            if (woods < 0 || woods > 4096) return;

            var fuel = new Inventory();
            for (int i = 0; i < woods; i++)
            {
                reader.ReadValueSafe(out int wood);
                reader.ReadValueSafe(out int logs);
                if (logs > 0 && Fuels.IsFuel(wood)) fuel.Add(wood, logs);
            }

            List<StorageMember> linked = ReadMembers(reader);
            if (linked == null) return;

            reader.ReadValueSafe(out int jobCount);
            if (jobCount < 0 || jobCount > Station.MaxJobs) return;

            var jobs = new List<StationJobView>();
            for (int i = 0; i < jobCount; i++)
            {
                var job = new StationJobView();
                reader.ReadValueSafe(out job.Id);
                reader.ReadValueSafe(out byte recipe);
                reader.ReadValueSafe(out byte count);
                reader.ReadValueSafe(out byte stocked);
                reader.ReadValueSafe(out job.LabourLeft);
                reader.ReadValueSafe(out job.SecondsLeft);
                reader.ReadValueSafe(out job.Creator);
                job.Recipe = recipe;
                job.Count = count;
                job.Stocked = stocked;
                if (job.Recipe >= recipes.Count || job.Count <= 0) continue;

                job.Made = recipes[job.Recipe];
                jobs.Add(job);
            }

            ShowStation(id, kind, owner, (StationStatus)status, fuel, burning, logSeconds, linked, jobs);
        }

        private void ShowStation(int id, StorageKind kind, bool owner, StationStatus status, Inventory fuel, float burning, float logSeconds, List<StorageMember> linked, List<StationJobView> jobs)
        {
            OpenedId = id;
            OpenedKind = kind;
            OpenedIsOwner = owner;
            OpenedMembers = 1;
            OpenedStatus = status;
            openedFuel.CopyFrom(fuel);
            OpenedFuel = 0;
            foreach (KeyValuePair<int, int> entry in openedFuel.Entries)
            {
                OpenedFuel += entry.Value;
            }

            openedBurning = burning;
            OpenedLogSeconds = Mathf.Max(1f, logSeconds);
            // The linked chests and stockpiles apart; the stockpiles also as the opened ones, which the
            // pile slots read (PoolView).
            openedLinkedMembers.Clear();
            openedLinkedStockpiles.Clear();
            openedLinked.Clear();
            openedLinkedPiled.Clear();
            openedStockpiles.Clear();
            foreach (StorageMember member in linked)
            {
                if (member.Piles == null)
                {
                    openedLinkedMembers.Add(member);
                    AddAll(openedLinked, member.Contents);
                    continue;
                }

                openedLinkedStockpiles.Add(member);
                openedStockpiles.Add(member.Piles);
                for (int pile = 0; pile < member.Piles.Count; pile++)
                {
                    int count = member.Piles.CountAt(pile);
                    if (count > 0) openedLinkedPiled.Add(member.Piles.ItemAt(pile), count);
                }
            }

            openedLinkedAll.CopyFrom(openedLinked);
            AddAll(openedLinkedAll, openedLinkedPiled);

            openedJobs.Clear();
            openedJobs.AddRange(jobs);
            openedFireTime = Time.unscaledTimeAsDouble;
            openedMembers.Clear();
            openedContents.Clear();
            OpenedChanged?.Invoke();
        }
    }
}

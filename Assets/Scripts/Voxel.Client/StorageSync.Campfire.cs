using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using Voxel.Core;

namespace Voxel.Client
{
    // An order on the open campfire's queue, as its window shows it.
    public sealed class CookJobView
    {
        public int Id;
        public int Recipe;
        public int Count;
        public int Stocked;
        public float LabourLeft;
        public float SecondsLeft;
        public string Creator;

        // Whether the work of the dish at the front of the order is all in.
        public bool UnitPaid => LabourLeft <= Recipes.Cooking[Recipe].Calories * (Count - 1) + 1e-3f;
    }

    // Campfires (Campfire, StorageKind.Campfire): placed, owned, opened and picked up like storage, they
    // cook dishes (Recipes.Cooking). A player queues an order of so many of a dish, the ingredients of
    // as many as can be taken at once from their bag then from the linked chests (the chests of the
    // campfire's claimed zone; off claimed land, the public chests within LinkRange), the others taken
    // from the linked chests when their turn comes; puts calories into it (LabourStep at a time, or all
    // it needs and they have); lays wood by the fire and takes it back; and moves items between their
    // bag and the linked chests from the campfire's window. The server ticks every campfire each frame:
    // the first dish whose work is in and whose ingredients are there cooks while there is wood and a
    // linked chest has room for it, and goes into the linked chests when done. Whoever has a campfire
    // open gets its state when it changes (Fire.State) and counts the cooking down itself; every
    // machine knows whether it burns (Store.State's flag), to draw its flames.
    public partial class StorageSync
    {
        private const string FuelMessage = "Fire.Fuel";
        private const string TakeFuelMessage = "Fire.TakeFuel";
        private const string QueueMessage = "Fire.Queue";
        private const string LabourMessage = "Fire.Labour";
        private const string CancelJobMessage = "Fire.Cancel";
        private const string FireMoveMessage = "Fire.Move";
        private const string FireStateMessage = "Fire.State";

        // How far from a campfire off claimed land the public chests it uses stand, in blocks.
        public const float LinkRange = 8f;

        // The calories a click puts into an order (Shift: all it needs, as far as the player has them).
        public const float LabourStep = 100f;

        // Server side: what each campfire's viewers were last sent, to send it again only when it changed.
        private readonly Dictionary<int, long> fireSent = new Dictionary<int, long>();
        private readonly List<Storage> fires = new List<Storage>();

        // The open campfire as the server last sent it.
        private readonly List<CookJobView> openedJobs = new List<CookJobView>();
        private readonly Inventory openedLinked = new Inventory();
        private readonly List<StorageMember> openedLinkedMembers = new List<StorageMember>();
        private readonly Inventory openedFuel = new Inventory();
        private double openedFireTime;

        public IReadOnlyList<CookJobView> OpenedJobs => openedJobs;

        // What the linked chests of the open campfire hold together, for the ingredients it shows, and
        // each of them.
        public Inventory OpenedLinkedContents => openedLinked;

        public IReadOnlyList<StorageMember> OpenedLinkedMembers => openedLinkedMembers;

        public int OpenedLinkedChests => openedLinkedMembers.Count;

        // The wood by the open campfire, by kind, and how many logs in all.
        public Inventory OpenedFuelContents => openedFuel;

        public int OpenedFuel { get; private set; }

        public CampfireStatus OpenedStatus { get; private set; }

        // How long the log alight still burns, counted down while it cooks.
        public float OpenedBurning => OpenedStatus == CampfireStatus.Cooking ? Mathf.Max(0f, openedBurning - SinceFireState) : openedBurning;

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

        // How long the dish at the front of an order of the open campfire still cooks: counted down for
        // the one cooking.
        public float SecondsLeftOf(CookJobView job)
        {
            if (OpenedStatus != CampfireStatus.Cooking || job != CookingJob) return job.SecondsLeft;

            return Mathf.Max(0f, job.SecondsLeft - SinceFireState);
        }

        // The order of the open campfire that cooks, or would (Campfire.Next): one whose dish has
        // started, else the first whose dish is paid for and has its ingredients.
        public CookJobView CookingJob
        {
            get
            {
                foreach (CookJobView job in openedJobs)
                {
                    if (job.UnitPaid && job.Stocked > 0 && job.SecondsLeft < Recipes.Cooking[job.Recipe].Seconds - 1e-4f) return job;
                }

                foreach (CookJobView job in openedJobs)
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

        // ---- Requests from the local player, about the open campfire ----

        public void RequestFuel(int wood, int count) => SendFire(FuelMessage, wood, count);

        public void RequestTakeFuel(int wood, int count) => SendFire(TakeFuelMessage, wood, count);

        public void RequestQueue(int recipe, int count) => SendFire(QueueMessage, recipe, count);

        public void RequestLabour(int jobId, bool all) => SendFire(LabourMessage, jobId, all ? 1 : 0);

        public void RequestCancelJob(int jobId) => SendFire(CancelJobMessage, jobId, 0);

        // Moves items between the bag and the open campfire's linked chests (member: one of them, or 0
        // for them all).
        public void RequestFireMove(bool deposit, int item, int count, int member) => SendFire(FireMoveMessage, item, count, deposit ? 1 : 0, member);

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

        private void StartCampfireServer()
        {
            foreach (string message in new[] { FuelMessage, TakeFuelMessage, QueueMessage, LabourMessage, CancelJobMessage, FireMoveMessage })
            {
                Messaging.RegisterNamedMessageHandler(message, (sender, reader) => ReadFire(message, sender, reader));
            }
        }

        private void StartCampfireClient()
        {
            Messaging.RegisterNamedMessageHandler(FireStateMessage, OnFireState);
        }

        private void StopCampfires()
        {
            fireSent.Clear();
            openedJobs.Clear();
            openedLinked.Clear();
            openedLinkedMembers.Clear();
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
            if (!store.TryGet(storageId, out Storage fire) || fire.Kind != StorageKind.Campfire) return;
            if (!registry.TryGetPlayerId(clientId, out string playerId) || !registry.TryGetState(clientId, out PlayerState state)) return;
            if (!CanUse(playerId, fire) || !InReach(clientId, fire, 1f)) return;

            switch (message)
            {
                case FuelMessage:
                    AddFuel(clientId, state, fire, a, b);
                    break;
                case TakeFuelMessage:
                    TakeFuel(clientId, fire, a, b);
                    break;
                case QueueMessage:
                    QueueDish(clientId, playerId, state, fire, a, b);
                    break;
                case LabourMessage:
                    AddLabour(clientId, state, fire, a, b != 0);
                    break;
                case CancelJobMessage:
                    CancelDish(clientId, state, fire, a);
                    break;
                case FireMoveMessage:
                    List<Storage> linked = LinkedChests(fire);
                    if (linked.Count > 0 && MoveWithin(clientId, linked, true, c != 0, a, b, d, false)) Changed(linked[0], linked);
                    break;
            }

            SendFireToViewers(fire, true);
        }

        private void AddFuel(ulong clientId, PlayerState state, Storage fire, int wood, int count)
        {
            if (!Fuels.IsFuel(wood) || count <= 0) return;

            int room = Campfire.MaxFuel - fire.Campfire.FuelCount;
            if (room <= 0)
            {
                Refuse(clientId, StorageRefusal.FuelFull);
                return;
            }

            int moved = Math.Min(count, Math.Min(room, state.Inventory.Count(wood)));
            if (moved <= 0 || !inventorySync.TryConsume(clientId, wood, moved)) return;

            fire.Campfire.AddFuel(wood, moved);
        }

        // Wood back to the bag, what it has room for.
        private void TakeFuel(ulong clientId, Storage fire, int wood, int count)
        {
            if (count <= 0 || fire.Campfire.Fuel.Count(wood) <= 0) return;

            int room = inventorySync.RoomFor(clientId, wood);
            if (room <= 0)
            {
                inventorySync.TellFull(clientId, InventorySync.ReasonFor(wood));
                return;
            }

            int moved = fire.Campfire.TakeFuel(wood, Math.Min(count, room));
            if (moved > 0) inventorySync.Give(clientId, wood, moved);
        }

        // The ingredients of as many of the dishes as can be had now, whole, from the bag first, then
        // from the linked chests; the others are taken from the linked chests when their turn comes.
        private void QueueDish(ulong clientId, string playerId, PlayerState state, Storage fire, int recipeIndex, int count)
        {
            if (recipeIndex < 0 || recipeIndex >= Recipes.Cooking.Count || count < 1 || count > Campfire.MaxCount) return;
            if (fire.Campfire.Jobs.Count >= Campfire.MaxJobs)
            {
                Refuse(clientId, StorageRefusal.QueueFull);
                return;
            }

            Recipe recipe = Recipes.Cooking[recipeIndex];
            List<Storage> linked = LinkedChests(fire);
            int stocked = count;
            foreach (Ingredient ingredient in recipe.Ingredients)
            {
                int have = state.Inventory.Count(ingredient.Item) + StoragePool.Count(linked, ingredient.Item);
                stocked = Math.Min(stocked, have / ingredient.Count);
            }

            bool chestsChanged = false;
            if (stocked > 0)
            {
                foreach (Ingredient ingredient in recipe.Ingredients)
                {
                    int needed = ingredient.Count * stocked;
                    int fromBag = Math.Min(needed, state.Inventory.Count(ingredient.Item));
                    if (fromBag > 0) inventorySync.TryConsume(clientId, ingredient.Item, fromBag);
                    if (needed > fromBag)
                    {
                        StoragePool.TakeFromChests(linked, ingredient.Item, needed - fromBag);
                        chestsChanged = true;
                    }
                }
            }

            fire.Campfire.Queue(recipeIndex, count, playerId, stocked);
            if (chestsChanged) Changed(linked[0], linked);
        }

        private void AddLabour(ulong clientId, PlayerState state, Storage fire, int jobId, bool all)
        {
            if (!fire.Campfire.TryGetJob(jobId, out CookJob job) || job.HasLabour) return;
            if (state.Stomach.IsStarving)
            {
                Refuse(clientId, StorageRefusal.TooHungry);
                return;
            }

            float wanted = all ? job.LabourLeft : Mathf.Min(LabourStep, job.LabourLeft);
            float added = fire.Campfire.AddLabour(jobId, Mathf.Min(wanted, state.Stomach.Calories));
            if (added > 0f) state.Stomach.Burn(added);
        }

        // What was taken and not cooked goes back to the bag, then to the linked chests; all of it, or
        // the order stays.
        private void CancelDish(ulong clientId, PlayerState state, Storage fire, int jobId)
        {
            if (!fire.Campfire.TryGetJob(jobId, out CookJob job)) return;

            List<Storage> linked = LinkedChests(fire);
            var toBag = new List<KeyValuePair<int, int>>();
            var toChests = new List<KeyValuePair<int, int>>();
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

                if (item.Value > fits) toChests.Add(new KeyValuePair<int, int>(item.Key, item.Value - fits));
            }

            // What the bag cannot take must fit in the linked chests, checked on copies.
            if (toChests.Count > 0)
            {
                var copies = new List<Storage>();
                foreach (Storage chest in linked)
                {
                    var copy = new Storage(chest.Id, chest.Kind, chest.Position, chest.Facing, chest.OwnerId);
                    copy.Contents.CopyFrom(chest.Contents);
                    copies.Add(copy);
                }

                foreach (KeyValuePair<int, int> item in toChests)
                {
                    if (StoragePool.AddToChests(copies, item.Key, item.Value, ecosystemSync.ItemWeights) < item.Value)
                    {
                        Refuse(clientId, StorageRefusal.NoRoomToGiveBack);
                        return;
                    }
                }
            }

            fire.Campfire.Remove(jobId);
            foreach (KeyValuePair<int, int> item in toBag)
            {
                inventorySync.Give(clientId, item.Key, item.Value);
            }

            foreach (KeyValuePair<int, int> item in toChests)
            {
                StoragePool.AddToChests(linked, item.Key, item.Value, ecosystemSync.ItemWeights);
            }

            if (toChests.Count > 0 && linked.Count > 0) Changed(linked[0], linked);
        }

        // A campfire picked up: nothing may be queued, and its wood goes back to the bag, all of it.
        private bool TryEmptyCampfire(ulong clientId, Storage fire)
        {
            Campfire campfire = fire.Campfire;
            if (campfire.Jobs.Count > 0 || !registry.TryGetState(clientId, out PlayerState state)) return false;

            var bag = new Inventory();
            bag.CopyFrom(state.Inventory);
            bag.Add(Items.Campfire, 1);
            foreach (KeyValuePair<int, int> wood in campfire.Fuel.Entries)
            {
                if (CarryLimits.Room(bag, wood.Key, ecosystemSync.ItemWeights) < wood.Value) return false;

                bag.Add(wood.Key, wood.Value);
            }

            var logs = new List<KeyValuePair<int, int>>(campfire.Fuel.Entries);
            campfire.Fuel.Clear();
            foreach (KeyValuePair<int, int> wood in logs)
            {
                inventorySync.Give(clientId, wood.Key, wood.Value);
            }

            return true;
        }

        // The chests a campfire takes ingredients from and puts dishes into: those of its claimed zone;
        // off claimed land, the public chests within LinkRange.
        private List<Storage> LinkedChests(Storage fire)
        {
            var linked = new List<Storage>();
            bool claimed = TryLandOf(fire, out int x, out int z);
            int zone = claimed ? claimSync.ZoneAt(x, z) : -1;
            foreach (Storage other in store.All)
            {
                if (other.Kind != StorageKind.Chest) continue;

                if (claimed)
                {
                    if (TryLandOf(other, out int ox, out int oz) && claimSync.ZoneAt(ox, oz) == zone) linked.Add(other);
                }
                else if (IsPublic(other))
                {
                    float dx = other.Position.X - fire.Position.X;
                    float dy = other.Position.Y - fire.Position.Y;
                    float dz = other.Position.Z - fire.Position.Z;
                    if (dx * dx + dy * dy + dz * dz <= LinkRange * LinkRange) linked.Add(other);
                }
            }

            linked.Sort((a, b) => a.Id.CompareTo(b.Id));
            return linked;
        }

        // One dish's ingredients from the linked chests, all or nothing.
        private static bool TakeIngredients(List<Storage> linked, int recipeIndex)
        {
            Recipe recipe = Recipes.Cooking[recipeIndex];
            foreach (Ingredient ingredient in recipe.Ingredients)
            {
                if (StoragePool.Count(linked, ingredient.Item) < ingredient.Count) return false;
            }

            foreach (Ingredient ingredient in recipe.Ingredients)
            {
                StoragePool.TakeFromChests(linked, ingredient.Item, ingredient.Count);
            }

            return true;
        }

        // Every frame: each campfire with something queued cooks; a dish done goes into the linked chests.
        private void TickCampfires(float seconds)
        {
            fires.Clear();
            foreach (Storage storage in store.All)
            {
                if (storage.Kind == StorageKind.Campfire && (storage.Campfire.Jobs.Count > 0 || storage.Campfire.Status != CampfireStatus.Idle)) fires.Add(storage);
            }

            foreach (Storage fire in fires)
            {
                List<Storage> linked = LinkedChests(fire);
                bool chestsChanged = false;
                CookJob done = fire.Campfire.Tick(seconds,
                    (item, count) => linked.Count > 0 && StoragePool.ChestRoom(linked, item, ecosystemSync.ItemWeights) >= count,
                    recipe => chestsChanged = TakeIngredients(linked, recipe));
                if (done != null)
                {
                    Recipe recipe = Recipes.Cooking[done.Recipe];
                    StoragePool.AddToChests(linked, recipe.Output, recipe.OutputCount, ecosystemSync.ItemWeights);
                    chestsChanged = true;
                }

                if (chestsChanged && linked.Count > 0) Changed(linked[0], linked);
                SetOpen(fire, fire.Campfire.Status == CampfireStatus.Cooking);
                SendFireToViewers(fire, false);
            }
        }

        // Campfires open in a window whose linked chests are among these: their viewers get them again.
        private void RefreshFiresLinkedTo(List<Storage> chests)
        {
            foreach (KeyValuePair<int, HashSet<ulong>> watched in viewers)
            {
                if (watched.Value.Count == 0 || !store.TryGet(watched.Key, out Storage fire) || fire.Kind != StorageKind.Campfire) continue;

                List<Storage> linked = LinkedChests(fire);
                if (linked.Exists(chests.Contains)) SendFireToViewers(fire, true);
            }
        }

        // Sends a campfire's state to whoever has it open: always (after a request), or when what they
        // see changed (status, wood, the queue).
        private void SendFireToViewers(Storage fire, bool always)
        {
            Campfire campfire = fire.Campfire;
            long key = (long)campfire.Status * 1_000_003 + campfire.FuelCount * 7919 + campfire.Jobs.Count * 31;
            foreach (CookJob job in campfire.Jobs)
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
                SendCampfire(viewer, fire);
            }
        }

        private void SendCampfire(ulong clientId, Storage fire)
        {
            Campfire campfire = fire.Campfire;
            bool owner = registry.TryGetPlayerId(clientId, out string playerId) && playerId == OwnerOf(fire);
            List<Storage> linked = LinkedChests(fire);
            if (clientId == networkManager.LocalClientId)
            {
                var jobs = new List<CookJobView>();
                foreach (CookJob job in campfire.Jobs)
                {
                    jobs.Add(ViewOf(job));
                }

                ShowCampfire(fire.Id, owner, campfire.Status, campfire.Fuel, campfire.Burning, campfire.LogSeconds, MembersOf(linked), jobs);
                return;
            }

            Send(FireStateMessage, clientId, writer =>
            {
                writer.WriteValueSafe(fire.Id);
                writer.WriteValueSafe(owner);
                writer.WriteValueSafe((byte)campfire.Status);
                writer.WriteValueSafe(campfire.Burning);
                writer.WriteValueSafe(campfire.LogSeconds);
                writer.WriteValueSafe(campfire.Fuel.DistinctCount);
                foreach (KeyValuePair<int, int> entry in campfire.Fuel.Entries)
                {
                    writer.WriteValueSafe(entry.Key);
                    writer.WriteValueSafe(entry.Value);
                }

                WriteMembers(writer, linked);
                writer.WriteValueSafe(campfire.Jobs.Count);
                foreach (CookJob job in campfire.Jobs)
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

        private CookJobView ViewOf(CookJob job)
        {
            return new CookJobView
            {
                Id = job.Id,
                Recipe = job.Recipe,
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
            if (jobCount < 0 || jobCount > Campfire.MaxJobs) return;

            var jobs = new List<CookJobView>();
            for (int i = 0; i < jobCount; i++)
            {
                var job = new CookJobView();
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
                if (job.Recipe < Recipes.Cooking.Count && job.Count > 0) jobs.Add(job);
            }

            ShowCampfire(id, owner, (CampfireStatus)status, fuel, burning, logSeconds, linked, jobs);
        }

        private void ShowCampfire(int id, bool owner, CampfireStatus status, Inventory fuel, float burning, float logSeconds, List<StorageMember> linked, List<CookJobView> jobs)
        {
            OpenedId = id;
            OpenedKind = StorageKind.Campfire;
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
            openedLinkedMembers.Clear();
            openedLinkedMembers.AddRange(linked);
            openedLinked.Clear();
            foreach (StorageMember chest in linked)
            {
                AddAll(openedLinked, chest.Contents);
            }

            openedJobs.Clear();
            openedJobs.AddRange(jobs);
            openedFireTime = Time.unscaledTimeAsDouble;
            openedMembers.Clear();
            openedContents.Clear();
            openedStockpiles.Clear();
            OpenedChanged?.Invoke();
        }
    }
}

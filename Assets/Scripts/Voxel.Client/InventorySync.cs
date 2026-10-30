using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using Voxel.Core;

namespace Voxel.Client
{
    // Why a player could not take more (CarryLimits): too many blocks, too heavy, or the player they
    // were giving to is full.
    public enum FullReason : byte
    {
        Blocks,
        Weight,
        Receiver
    }

    // Players' inventory, hotbar and speciality. The server holds them (in each player's saved state)
    // and decides everything: choosing a speciality, giving, planting, arranging the hotbar. Each
    // client only knows its own, which the server sends back at every change. What a player can carry
    // is limited (CarryLimits: 40 blocks, 30 kg of the rest): items are only given while they fit, and
    // an action that would give nothing that fits is refused, the player being told why (Refused).
    // A player in creative mode (PlayerState.Creative, switched by anyone for themselves) takes any
    // item for free (RequestCreativeGive), carries without limit, owns every tool, never starves and
    // places blocks, doors, storage, seeds and arrows without spending them (TryConsumePlaced).
    public class InventorySync : MonoBehaviour
    {
        private const string FullMessage = "Inv.Full";
        private const string CreativeMessage = "Inv.Creative";
        private const string CreativeGiveMessage = "Inv.CreativeGive";
        private const byte CreativeFlag = 1;
        private const string SowRefusedMessage = "Inv.SowRefused";

        // Arrows in the starting kit, with the tools.
        private const int StartingArrows = 20;
        private const int StartingTorches = 20;
        private const string StateMessage = "Inv.State";
        private const string SpecialityMessage = "Inv.Speciality";
        private const string GiveMessage = "Inv.Give";
        private const string PlantMessage = "Inv.Plant";
        private const string HotbarMessage = "Inv.Hotbar";
        private const string ButcherMessage = "Inv.Butcher";

        [SerializeField] private NetworkManager networkManager;
        [SerializeField] private EcosystemSync ecosystemSync;

        // Local copy of the inventory of this machine's player, when it is not the server.
        private readonly Inventory received = new Inventory();
        private readonly Hotbar receivedHotbar = new Hotbar();
        private Speciality receivedSpeciality;
        private bool receivedCreative;
        private PlayerRegistry registry;
        private bool running;
        private HashSet<int> catalog;

        // Inventory, hotbar and speciality of the local player. On the host, they are the server's own.
        public Inventory Inventory => TryGetLocalState(out PlayerState state) ? state.Inventory : received;

        public Hotbar Hotbar => TryGetLocalState(out PlayerState state) ? state.Hotbar : receivedHotbar;

        public Speciality Speciality => TryGetLocalState(out PlayerState state) ? state.Speciality : receivedSpeciality;

        // Whether the local player plays in creative mode.
        public bool Creative => TryGetLocalState(out PlayerState state) ? state.Creative : receivedCreative;

        // The local player could not take more, and why.
        public event Action<FullReason> Refused;

        // Raised on the player's machine when a seed they sowed did not sprout, with why and its species.
        public event Action<SowRefusal, int> SowRefused;

        // How many more of an item the local player can carry.
        public int LocalRoomFor(int item) => Creative ? int.MaxValue : CarryLimits.Room(Inventory, item, ecosystemSync.ItemWeights);

        // Which limit an item runs into: blocks are counted, everything else weighed.
        public static FullReason ReasonFor(int item) => CarryLimits.CountsAsBlock(item) ? FullReason.Blocks : FullReason.Weight;

        // Tells the local player at once, without asking the server (an action checked beforehand).
        public void TellLocalFull(FullReason reason) => Refused?.Invoke(reason);

        private CustomMessagingManager Messaging => networkManager.CustomMessagingManager;

        // To call right after the server or host has started.
        internal void StartServer(PlayerRegistry playerRegistry)
        {
            running = true;
            registry = playerRegistry;
            Messaging.RegisterNamedMessageHandler(SpecialityMessage, OnSpeciality);
            Messaging.RegisterNamedMessageHandler(GiveMessage, OnGive);
            Messaging.RegisterNamedMessageHandler(PlantMessage, OnPlant);
            Messaging.RegisterNamedMessageHandler(HotbarMessage, OnHotbar);
            Messaging.RegisterNamedMessageHandler(ButcherMessage, OnButcher);
            Messaging.RegisterNamedMessageHandler(CreativeMessage, OnCreative);
            Messaging.RegisterNamedMessageHandler(CreativeGiveMessage, OnCreativeGive);
            networkManager.OnClientConnectedCallback += OnClientConnected;

            // The host's own player connected before this was listening.
            foreach (ulong clientId in networkManager.ConnectedClientsIds)
            {
                OnClientConnected(clientId);
            }
        }

        // To call right after a client's connection has been launched.
        public void StartClient()
        {
            running = true;
            Messaging.RegisterNamedMessageHandler(StateMessage, OnState);
            Messaging.RegisterNamedMessageHandler(FullMessage, OnFull);
            Messaging.RegisterNamedMessageHandler(SowRefusedMessage, OnSowRefused);
        }

        public void Stop()
        {
            if (!running) return;

            if (registry != null) networkManager.OnClientConnectedCallback -= OnClientConnected;

            registry = null;
            received.Clear();
            receivedHotbar.Clear();
            receivedSpeciality = Speciality.None;
            receivedCreative = false;
            running = false;
        }

        // Server side: whether a player plays in creative mode.
        public bool IsCreative(ulong clientId) => registry != null && registry.TryGetState(clientId, out PlayerState state) && state.Creative;

        // Server side: takes what a player places (a block, a door, storage, a seed, an arrow shot);
        // in creative mode they keep it, but must still have one.
        public bool TryConsumePlaced(ulong clientId, int item)
        {
            if (IsCreative(clientId)) return Owns(clientId, item);

            return TryConsume(clientId, item);
        }

        // Server side: takes items from a player. Returns false, changing nothing, if they do not have enough.
        public bool TryConsume(ulong clientId, int item, int count = 1)
        {
            if (registry == null || !registry.TryGetState(clientId, out PlayerState state)) return false;
            if (!state.Inventory.TryRemove(item, count)) return false;

            Publish(clientId);
            return true;
        }

        // Server side: gives items to a player, as many as they can carry. Returns how many were given.
        public int Give(ulong clientId, int item, int count)
        {
            if (item == Items.None || count <= 0) return 0;
            if (registry == null || !registry.TryGetState(clientId, out PlayerState state)) return 0;

            int given = Receive(state, item, count);
            if (given > 0) Publish(clientId);
            return given;
        }

        // Server side: whether a player's stomach is empty. A starving player does nothing that takes
        // effort (hits, placing, tilling, planting, shooting); eating, harvesting and picking up still work.
        public bool IsStarving(ulong clientId)
        {
            return registry != null && registry.TryGetState(clientId, out PlayerState state) && state.Stomach.IsStarving && !state.Creative;
        }

        // What a starving player is told when they try something that takes effort.
        public const string TooHungryMessage = "Too hungry: eat something";

        // Server side: whether a player has at least one of an item (Items.None: always true, bare
        // hands). In creative mode a player has every tool; anything else they must hold.
        public bool Owns(ulong clientId, int item)
        {
            if (item == Items.None) return true;
            if (registry == null || !registry.TryGetState(clientId, out PlayerState state)) return false;

            return state.Inventory.Has(item) || (state.Creative && Items.IsTool(item));
        }

        // Server side: how many more of an item a player can carry (no limit in creative mode).
        public int RoomFor(ulong clientId, int item)
        {
            if (registry == null || !registry.TryGetState(clientId, out PlayerState state)) return 0;

            return RoomIn(state, item);
        }

        private int RoomIn(PlayerState state, int item) =>
            state.Creative ? int.MaxValue : CarryLimits.Room(state.Inventory, item, ecosystemSync.ItemWeights);

        // Server side: whether an action giving these rewards may go ahead. One giving nothing at all
        // may; one giving something of which nothing fits may not, and the player is told why.
        public bool CanReceiveAny(ulong clientId, IReadOnlyList<(int item, int count)> rewards)
        {
            int firstFull = Items.None;
            foreach ((int item, int count) in rewards)
            {
                if (item == Items.None || count <= 0) continue;
                if (RoomFor(clientId, item) > 0) return true;
                if (firstFull == Items.None) firstFull = item;
            }

            if (firstFull == Items.None) return true;

            TellFull(clientId, ReasonFor(firstFull));
            return false;
        }

        // Server side: tells a player they could not take more.
        public void TellFull(ulong clientId, FullReason reason)
        {
            if (clientId == networkManager.LocalClientId)
            {
                Refused?.Invoke(reason);
                return;
            }

            using (var writer = new FastBufferWriter(sizeof(byte), Allocator.Temp))
            {
                writer.WriteValueSafe((byte)reason);
                Messaging.SendNamedMessage(FullMessage, clientId, writer, NetworkDelivery.ReliableSequenced);
            }
        }

        // Puts an item in one of the local player's hotbar slots (Items.None empties it). A client
        // shows it at once; the server's state comes back with the next change.
        public void RequestHotbarSlot(int slot, int item)
        {
            if (!running) return;

            if (registry != null)
            {
                SetHotbarSlot(networkManager.LocalClientId, slot, item);
                return;
            }

            receivedHotbar.Set(slot, item);
            Send(HotbarMessage, slot, item);
        }

        public void RequestSpeciality(Speciality speciality)
        {
            if (!running) return;

            if (registry != null) Choose(networkManager.LocalClientId, speciality);
            else Send(SpecialityMessage, (int)speciality);
        }

        // Switches the local player's creative mode on or off.
        public void RequestCreative(bool creative)
        {
            if (!running) return;

            if (registry != null) SetCreative(networkManager.LocalClientId, creative);
            else Send(CreativeMessage, creative ? 1 : 0);
        }

        // Takes items from the creative catalog (the local player must be in creative mode).
        public void RequestCreativeGive(int item, int count)
        {
            if (!running) return;

            if (registry != null) CreativeGive(networkManager.LocalClientId, item, count);
            else Send(CreativeGiveMessage, item, count);
        }

        // Gives items to the nearest player, if within reach.
        public void RequestGive(int item, int count)
        {
            if (!running) return;

            if (registry != null) GiveToNearest(networkManager.LocalClientId, item, count);
            else Send(GiveMessage, item, count);
        }

        // Plants a seed in the given block column. Gardeners only.
        public void RequestPlant(int seed, int x, int z)
        {
            if (!running) return;

            if (registry != null) Plant(networkManager.LocalClientId, seed, x, z);
            else Send(PlantMessage, seed, x, z);
        }

        // Butchers a carcass from the bag with a knife: half its meat (Butchery.FieldMeat).
        public void RequestButcher(int carcass)
        {
            if (!running) return;

            if (registry != null) ButcherFromBag(networkManager.LocalClientId, carcass);
            else Send(ButcherMessage, carcass);
        }

        // Server side: butchers a carcass of an animal species with a knife (Butchery.FieldMeat). The
        // player must own a knife, not be starving and have room for some of the meat (once the carcass,
        // when it is in their bag, is out of it); then take() removes the carcass, the meat goes into the
        // bag and the work burns their calories. Returns whether it was butchered.
        internal bool ButcherWithKnife(ulong clientId, int species, int carcassInBag, Func<bool> take)
        {
            if (registry == null || !registry.TryGetState(clientId, out PlayerState state) || !Butchery.TryGetAnimal(species, out ButcheredAnimal animal)) return false;
            if (!Owns(clientId, ToolRules.ToButcher) || IsStarving(clientId)) return false;

            int meat = Items.Meat(animal.Meat);
            var bag = new Inventory();
            bag.CopyFrom(state.Inventory);
            if (carcassInBag != Items.None && !bag.TryRemove(carcassInBag)) return false;

            int room = state.Creative ? int.MaxValue : CarryLimits.Room(bag, meat, ecosystemSync.ItemWeights);
            int count = Math.Min(Butchery.FieldMeat(animal), room);
            if (count <= 0)
            {
                TellFull(clientId, ReasonFor(meat));
                return false;
            }

            if (!take()) return false;

            Receive(state, meat, count);
            if (!state.Creative) state.Stomach.Burn(Butchery.LabourOf(animal));
            Publish(clientId);
            return true;
        }

        private void ButcherFromBag(ulong clientId, int carcass)
        {
            if (!Items.IsCarcass(carcass) || !registry.TryGetState(clientId, out PlayerState state)) return;

            ButcherWithKnife(clientId, Items.CarcassSpecies(carcass), carcass, () => state.Inventory.TryRemove(carcass));
        }

        private bool TryGetLocalState(out PlayerState state)
        {
            state = null;
            return registry != null && networkManager.IsHost && registry.TryGetState(networkManager.LocalClientId, out state);
        }

        // A speciality is chosen only once.
        private void Choose(ulong clientId, Speciality speciality)
        {
            if (!registry.TryGetState(clientId, out PlayerState state)) return;
            if (state.Speciality != Speciality.None || !Specialities.IsChoice(speciality)) return;

            state.Speciality = speciality;
            Publish(clientId);
        }

        private void GiveToNearest(ulong clientId, int item, int count)
        {
            if (!registry.TryGetState(clientId, out PlayerState giver)) return;
            if (!networkManager.ConnectedClients.TryGetValue(clientId, out NetworkClient client) || client.PlayerObject == null) return;

            Vector3 position = client.PlayerObject.transform.position;
            ulong nearestId = 0;
            float nearest = PlayerRules.Reach * PlayerRules.Reach;
            bool found = false;

            foreach (NetworkClient other in networkManager.ConnectedClientsList)
            {
                if (other.ClientId == clientId || other.PlayerObject == null) continue;

                float distance = (other.PlayerObject.transform.position - position).sqrMagnitude;
                if (distance <= nearest)
                {
                    nearest = distance;
                    nearestId = other.ClientId;
                    found = true;
                }
            }

            if (!found || !registry.TryGetState(nearestId, out PlayerState receiver)) return;

            // The receiver only takes what they can carry, and the giver only loses that.
            int given = Math.Min(Math.Min(count, giver.Inventory.Count(item)), RoomIn(receiver, item));
            if (given <= 0)
            {
                if (giver.Inventory.Has(item)) TellFull(clientId, FullReason.Receiver);
                return;
            }

            giver.Inventory.TryRemove(item, given);
            Receive(receiver, item, given);
            Publish(clientId);
            Publish(nearestId);
        }

        // Adds items to a player's inventory, as many as they can carry, and returns how many; an
        // item they did not have takes an empty hotbar slot.
        private int Receive(PlayerState state, int item, int count)
        {
            int given = Math.Min(count, RoomIn(state, item));
            if (given <= 0) return 0;

            bool had = state.Inventory.Has(item);
            state.Inventory.Add(item, given);
            if (!had) state.Hotbar.Place(item);
            return given;
        }

        // Creative mode on or off, for anyone; switched on, the stomach is filled (and stays so, since
        // creative players burn nothing, HungerSync).
        private void SetCreative(ulong clientId, bool creative)
        {
            if (!registry.TryGetState(clientId, out PlayerState state) || state.Creative == creative) return;

            state.Creative = creative;
            if (creative) state.Stomach.Fill();
            Publish(clientId);
        }

        // A creative player takes up to CreativeRules.MaxPerRequest of any item a player may hold.
        private void CreativeGive(ulong clientId, int item, int count)
        {
            if (!IsCreative(clientId) || count <= 0 || count > CreativeRules.MaxPerRequest) return;

            if (catalog == null) catalog = new HashSet<int>(ItemCatalog.AllItems(ecosystemSync));
            if (catalog.Contains(item)) Give(clientId, item, count);
        }

        private void OnCreative(ulong senderId, FastBufferReader reader)
        {
            reader.ReadValueSafe(out int creative);
            SetCreative(senderId, creative != 0);
        }

        private void OnCreativeGive(ulong senderId, FastBufferReader reader)
        {
            reader.ReadValueSafe(out int item);
            reader.ReadValueSafe(out int count);
            CreativeGive(senderId, item, count);
        }

        private void SetHotbarSlot(ulong clientId, int slot, int item)
        {
            if (!registry.TryGetState(clientId, out PlayerState state)) return;

            state.Hotbar.Set(slot, item);
            Publish(clientId);
        }

        // A player gets their starting tools, chest and small stockpile once (there is no crafting to make them), and one whose
        // save had no hotbar gets one filled with what they own.
        private void OnClientConnected(ulong clientId)
        {
            if (registry.TryGetState(clientId, out PlayerState state))
            {
                // Whatever they carry already: without tools a player can do nothing.
                if (!state.ReceivedTools)
                {
                    foreach (int tool in Items.Tools)
                    {
                        if (!state.Inventory.Has(tool)) state.Hotbar.Place(tool);
                        state.Inventory.Add(tool, 1);
                    }

                    if (!state.Inventory.Has(Items.Arrow)) state.Hotbar.Place(Items.Arrow);
                    state.Inventory.Add(Items.Arrow, StartingArrows);
                    state.ReceivedTools = true;
                    state.ReceivedKnife = true;
                    state.ReceivedHammer = true;
                }

                // And a knife once, players from before knives.
                if (!state.ReceivedKnife)
                {
                    if (!state.Inventory.Has(Items.Knife)) state.Hotbar.Place(Items.Knife);
                    state.Inventory.Add(Items.Knife, 1);
                    state.ReceivedKnife = true;
                }

                // And a hammer once, players from before hammers.
                if (!state.ReceivedHammer)
                {
                    if (!state.Inventory.Has(Items.Hammer)) state.Hotbar.Place(Items.Hammer);
                    state.Inventory.Add(Items.Hammer, 1);
                    state.ReceivedHammer = true;
                }

                // And a chest and a small stockpile once, players from before storage too (those who
                // had the large stockpile before keep it).
                if (!state.ReceivedStorage)
                {
                    foreach (int storage in new[] { Items.Chest, Items.SmallStockpile })
                    {
                        if (!state.Inventory.Has(storage)) state.Hotbar.Place(storage);
                        state.Inventory.Add(storage, 1);
                    }

                    state.ReceivedStorage = true;
                }

                // And torches once, players from before torches too.
                if (!state.ReceivedTorches)
                {
                    if (!state.Inventory.Has(Blocks.Torch)) state.Hotbar.Place(Blocks.Torch);
                    state.Inventory.Add(Blocks.Torch, StartingTorches);
                    state.ReceivedTorches = true;
                }

                if (state.Hotbar.IsEmpty) state.Hotbar.FillFrom(state.Inventory);
            }

            Publish(clientId);
        }

        private void Plant(ulong clientId, int seed, int x, int z)
        {
            if (!registry.TryGetState(clientId, out PlayerState state)) return;
            if (!Items.IsSeed(seed) || !state.Inventory.Has(seed)) return;
            if (IsStarving(clientId)) return;

            // Anyone plants a tree; only gardeners plant crops.
            int species = Items.SeedSpecies(seed);
            if (!ecosystemSync.IsTree(species) && state.Speciality != Speciality.Gardener)
            {
                TellSowRefused(clientId, SowRefusal.NotGardener, species);
                return;
            }

            // The seed is only spent if it actually sprouted; otherwise the player is told why.
            if (ecosystemSync.TryPlantSeed(clientId, species, x, z, out SowRefusal refusal)) TryConsumePlaced(clientId, seed);
            else if (refusal != SowRefusal.None) TellSowRefused(clientId, refusal, species);
        }

        private void TellSowRefused(ulong clientId, SowRefusal refusal, int species)
        {
            if (clientId == networkManager.LocalClientId)
            {
                SowRefused?.Invoke(refusal, species);
                return;
            }

            using (var writer = new FastBufferWriter(sizeof(byte) + sizeof(int), Allocator.Temp))
            {
                writer.WriteValueSafe((byte)refusal);
                writer.WriteValueSafe(species);
                Messaging.SendNamedMessage(SowRefusedMessage, clientId, writer, NetworkDelivery.ReliableSequenced);
            }
        }

        private void OnSowRefused(ulong senderId, FastBufferReader reader)
        {
            reader.ReadValueSafe(out byte refusal);
            reader.ReadValueSafe(out int species);
            SowRefused?.Invoke((SowRefusal)refusal, species);
        }

        private void OnSpeciality(ulong senderId, FastBufferReader reader)
        {
            reader.ReadValueSafe(out int speciality);
            Choose(senderId, (Speciality)speciality);
        }

        private void OnGive(ulong senderId, FastBufferReader reader)
        {
            reader.ReadValueSafe(out int item);
            reader.ReadValueSafe(out int count);
            GiveToNearest(senderId, item, count);
        }

        private void OnPlant(ulong senderId, FastBufferReader reader)
        {
            reader.ReadValueSafe(out int seed);
            reader.ReadValueSafe(out int x);
            reader.ReadValueSafe(out int z);
            Plant(senderId, seed, x, z);
        }

        private void OnButcher(ulong senderId, FastBufferReader reader)
        {
            reader.ReadValueSafe(out int carcass);
            ButcherFromBag(senderId, carcass);
        }

        private void OnHotbar(ulong senderId, FastBufferReader reader)
        {
            reader.ReadValueSafe(out int slot);
            reader.ReadValueSafe(out int item);
            SetHotbarSlot(senderId, slot, item);
        }

        // Sends a player their inventory, hotbar, speciality and creative mode. The host reads its own directly.
        internal void Publish(ulong clientId)
        {
            if (clientId == networkManager.LocalClientId) return;
            if (!registry.TryGetState(clientId, out PlayerState state)) return;

            int size = 2 * sizeof(byte) + sizeof(int) + state.Inventory.DistinctCount * 2 * sizeof(int) + Hotbar.SlotCount * sizeof(int);
            using (var writer = new FastBufferWriter(size, Allocator.Temp))
            {
                writer.WriteValueSafe((byte)state.Speciality);
                writer.WriteValueSafe(state.Creative ? CreativeFlag : (byte)0);
                writer.WriteValueSafe(state.Inventory.DistinctCount);
                foreach (System.Collections.Generic.KeyValuePair<int, int> entry in state.Inventory.Entries)
                {
                    writer.WriteValueSafe(entry.Key);
                    writer.WriteValueSafe(entry.Value);
                }

                for (int slot = 0; slot < Hotbar.SlotCount; slot++)
                {
                    writer.WriteValueSafe(state.Hotbar[slot]);
                }

                Messaging.SendNamedMessage(StateMessage, clientId, writer, NetworkDelivery.ReliableFragmentedSequenced);
            }
        }

        private void OnState(ulong senderId, FastBufferReader reader)
        {
            reader.ReadValueSafe(out byte speciality);
            reader.ReadValueSafe(out byte flags);
            reader.ReadValueSafe(out int entries);

            receivedSpeciality = (Speciality)speciality;
            receivedCreative = (flags & CreativeFlag) != 0;
            received.Clear();
            for (int i = 0; i < entries; i++)
            {
                reader.ReadValueSafe(out int item);
                reader.ReadValueSafe(out int count);
                received.Add(item, count);
            }

            for (int slot = 0; slot < Hotbar.SlotCount; slot++)
            {
                reader.ReadValueSafe(out int item);
                receivedHotbar.Set(slot, item);
            }
        }

        private void OnFull(ulong senderId, FastBufferReader reader)
        {
            reader.ReadValueSafe(out byte reason);
            if (reason <= (byte)FullReason.Receiver) Refused?.Invoke((FullReason)reason);
        }

        private void Send(string message, params int[] values)
        {
            using (var writer = new FastBufferWriter(values.Length * sizeof(int), Allocator.Temp))
            {
                foreach (int value in values)
                {
                    writer.WriteValueSafe(value);
                }

                Messaging.SendNamedMessage(message, NetworkManager.ServerClientId, writer, NetworkDelivery.ReliableFragmentedSequenced);
            }
        }
    }
}

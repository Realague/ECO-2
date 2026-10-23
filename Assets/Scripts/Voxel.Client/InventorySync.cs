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
    public class InventorySync : MonoBehaviour
    {
        private const string FullMessage = "Inv.Full";
        private const string SowRefusedMessage = "Inv.SowRefused";

        // Arrows in the starting kit, with the tools.
        private const int StartingArrows = 20;
        private const string StateMessage = "Inv.State";
        private const string SpecialityMessage = "Inv.Speciality";
        private const string GiveMessage = "Inv.Give";
        private const string PlantMessage = "Inv.Plant";
        private const string HotbarMessage = "Inv.Hotbar";

        [SerializeField] private NetworkManager networkManager;
        [SerializeField] private EcosystemSync ecosystemSync;

        // Local copy of the inventory of this machine's player, when it is not the server.
        private readonly Inventory received = new Inventory();
        private readonly Hotbar receivedHotbar = new Hotbar();
        private Speciality receivedSpeciality;
        private PlayerRegistry registry;
        private bool running;

        // Inventory, hotbar and speciality of the local player. On the host, they are the server's own.
        public Inventory Inventory => TryGetLocalState(out PlayerState state) ? state.Inventory : received;

        public Hotbar Hotbar => TryGetLocalState(out PlayerState state) ? state.Hotbar : receivedHotbar;

        public Speciality Speciality => TryGetLocalState(out PlayerState state) ? state.Speciality : receivedSpeciality;

        // The local player could not take more, and why.
        public event Action<FullReason> Refused;

        // Raised on the player's machine when a seed they sowed did not sprout, with why and its species.
        public event Action<SowRefusal, int> SowRefused;

        // How many more of an item the local player can carry.
        public int LocalRoomFor(int item) => CarryLimits.Room(Inventory, item, ecosystemSync.ItemWeights);

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
            running = false;
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

        // Server side: whether a player has at least one of an item (Items.None: always true, bare hands).
        public bool Owns(ulong clientId, int item)
        {
            if (item == Items.None) return true;

            return registry != null && registry.TryGetState(clientId, out PlayerState state) && state.Inventory.Has(item);
        }

        // Server side: how many more of an item a player can carry.
        public int RoomFor(ulong clientId, int item)
        {
            if (registry == null || !registry.TryGetState(clientId, out PlayerState state)) return 0;

            return CarryLimits.Room(state.Inventory, item, ecosystemSync.ItemWeights);
        }

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
            int given = Math.Min(Math.Min(count, giver.Inventory.Count(item)), CarryLimits.Room(receiver.Inventory, item, ecosystemSync.ItemWeights));
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
            int given = Math.Min(count, CarryLimits.Room(state.Inventory, item, ecosystemSync.ItemWeights));
            if (given <= 0) return 0;

            bool had = state.Inventory.Has(item);
            state.Inventory.Add(item, given);
            if (!had) state.Hotbar.Place(item);
            return given;
        }

        private void SetHotbarSlot(ulong clientId, int slot, int item)
        {
            if (!registry.TryGetState(clientId, out PlayerState state)) return;

            state.Hotbar.Set(slot, item);
            Publish(clientId);
        }

        // A player gets their starting tools, chest and stockpile once (there is no crafting to make them), and one whose
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
                }

                // And a chest and a stockpile once, players from before storage too.
                if (!state.ReceivedStorage)
                {
                    foreach (int storage in new[] { Items.Chest, Items.Stockpile })
                    {
                        if (!state.Inventory.Has(storage)) state.Hotbar.Place(storage);
                        state.Inventory.Add(storage, 1);
                    }

                    state.ReceivedStorage = true;
                }

                if (state.Hotbar.IsEmpty) state.Hotbar.FillFrom(state.Inventory);
            }

            Publish(clientId);
        }

        private void Plant(ulong clientId, int seed, int x, int z)
        {
            if (!registry.TryGetState(clientId, out PlayerState state)) return;
            if (!Items.IsSeed(seed) || !state.Inventory.Has(seed)) return;

            // Anyone plants a tree; only gardeners plant crops.
            int species = Items.SeedSpecies(seed);
            if (!ecosystemSync.IsTree(species) && state.Speciality != Speciality.Gardener)
            {
                TellSowRefused(clientId, SowRefusal.NotGardener, species);
                return;
            }

            // The seed is only spent if it actually sprouted; otherwise the player is told why.
            if (ecosystemSync.TryPlantSeed(clientId, species, x, z, out SowRefusal refusal)) TryConsume(clientId, seed);
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

        private void OnHotbar(ulong senderId, FastBufferReader reader)
        {
            reader.ReadValueSafe(out int slot);
            reader.ReadValueSafe(out int item);
            SetHotbarSlot(senderId, slot, item);
        }

        // Sends a player their inventory, hotbar and speciality. The host reads its own directly.
        private void Publish(ulong clientId)
        {
            if (clientId == networkManager.LocalClientId) return;
            if (!registry.TryGetState(clientId, out PlayerState state)) return;

            int size = sizeof(byte) + sizeof(int) + state.Inventory.DistinctCount * 2 * sizeof(int) + Hotbar.SlotCount * sizeof(int);
            using (var writer = new FastBufferWriter(size, Allocator.Temp))
            {
                writer.WriteValueSafe((byte)state.Speciality);
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
            reader.ReadValueSafe(out int entries);

            receivedSpeciality = (Speciality)speciality;
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

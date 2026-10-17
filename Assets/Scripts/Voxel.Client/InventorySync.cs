using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using Voxel.Core;

namespace Voxel.Client
{
    // Players' inventory and speciality. The server holds them (in each player's saved state) and
    // decides everything: choosing a speciality, giving, planting. Each client only
    // knows its own inventory, which the server sends back at every change.
    public class InventorySync : MonoBehaviour
    {
        private const string StateMessage = "Inv.State";
        private const string SpecialityMessage = "Inv.Speciality";
        private const string GiveMessage = "Inv.Give";
        private const string PlantMessage = "Inv.Plant";

        [SerializeField] private NetworkManager networkManager;
        [SerializeField] private EcosystemSync ecosystemSync;

        // Local copy of the inventory of this machine's player, when it is not the server.
        private readonly Inventory received = new Inventory();
        private Speciality receivedSpeciality;
        private PlayerRegistry registry;
        private bool running;

        // Inventory and speciality of the local player. On the host, they are the server's own.
        public Inventory Inventory => TryGetLocalState(out PlayerState state) ? state.Inventory : received;

        public Speciality Speciality => TryGetLocalState(out PlayerState state) ? state.Speciality : receivedSpeciality;

        private CustomMessagingManager Messaging => networkManager.CustomMessagingManager;

        // To call right after the server or host has started.
        internal void StartServer(PlayerRegistry playerRegistry)
        {
            running = true;
            registry = playerRegistry;
            Messaging.RegisterNamedMessageHandler(SpecialityMessage, OnSpeciality);
            Messaging.RegisterNamedMessageHandler(GiveMessage, OnGive);
            Messaging.RegisterNamedMessageHandler(PlantMessage, OnPlant);
            networkManager.OnClientConnectedCallback += Publish;
        }

        // To call right after a client's connection has been launched.
        public void StartClient()
        {
            running = true;
            Messaging.RegisterNamedMessageHandler(StateMessage, OnState);
        }

        public void Stop()
        {
            if (!running) return;

            if (registry != null) networkManager.OnClientConnectedCallback -= Publish;

            registry = null;
            received.Clear();
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

        // Server side: gives items to a player.
        public void Give(ulong clientId, int item, int count)
        {
            if (item == Items.None || count <= 0) return;
            if (registry == null || !registry.TryGetState(clientId, out PlayerState state)) return;

            state.Inventory.Add(item, count);
            Publish(clientId);
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
            if (!Inventory.Transfer(giver.Inventory, receiver.Inventory, item, count)) return;

            Publish(clientId);
            Publish(nearestId);
        }

        private void Plant(ulong clientId, int seed, int x, int z)
        {
            if (!registry.TryGetState(clientId, out PlayerState state)) return;
            if (state.Speciality != Speciality.Gardener || !Items.IsSeed(seed) || !state.Inventory.Has(seed)) return;

            // The seed is only spent if it actually sprouted.
            if (ecosystemSync.TryPlantSeed(clientId, Items.SeedSpecies(seed), x, z)) TryConsume(clientId, seed);
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

        // Sends a player their inventory and speciality. The host reads its own directly.
        private void Publish(ulong clientId)
        {
            if (clientId == networkManager.LocalClientId) return;
            if (!registry.TryGetState(clientId, out PlayerState state)) return;

            int size = sizeof(byte) + sizeof(int) + state.Inventory.DistinctCount * 2 * sizeof(int);
            using (var writer = new FastBufferWriter(size, Allocator.Temp))
            {
                writer.WriteValueSafe((byte)state.Speciality);
                writer.WriteValueSafe(state.Inventory.DistinctCount);
                foreach (System.Collections.Generic.KeyValuePair<int, int> entry in state.Inventory.Entries)
                {
                    writer.WriteValueSafe(entry.Key);
                    writer.WriteValueSafe(entry.Value);
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

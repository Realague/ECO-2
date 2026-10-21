using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using Voxel.Core;

namespace Voxel.Client
{
    // Players' stomachs. The server holds them (in each player's saved state): it burns their
    // calories by real time and effort (rest, walking, climbing, breaking and placing blocks:
    // HungerRules), and decides every meal. Each client only knows its own stomach, which the server
    // sends when it has changed enough to show.
    public class HungerSync : MonoBehaviour
    {
        private const string StateMessage = "Food.State";
        private const string EatMessage = "Food.Eat";

        // A stomach is sent again once its calories have moved by this share of the capacity, and at
        // most this often, unless a meal or an empty stomach makes it urgent.
        private const float SendShare = 0.005f;
        private const float SendInterval = 2f;
        private const int StateSize = (1 + Nutrition.NutrientCount) * sizeof(float);

        [SerializeField] private NetworkManager networkManager;
        [SerializeField] private InventorySync inventorySync;
        [SerializeField] private EcosystemSync ecosystemSync;
        [SerializeField] private WorldSync worldSync;

        // Server side: where each player stood last frame, what their client was last sent and when.
        private readonly Dictionary<ulong, Vector3> lastPositions = new Dictionary<ulong, Vector3>();
        private readonly Dictionary<ulong, float> sentCalories = new Dictionary<ulong, float>();
        private readonly Dictionary<ulong, float> sentTimes = new Dictionary<ulong, float>();
        private readonly List<ulong> departed = new List<ulong>();

        // Local copy of the stomach of this machine's player, when it is not the server.
        private readonly Stomach received = new Stomach();
        private PlayerRegistry registry;
        private bool running;

        // The local player's stomach. On the host, it is the server's own.
        public Stomach Stomach => TryGetLocalState(out PlayerState state) ? state.Stomach : received;

        private CustomMessagingManager Messaging => networkManager.CustomMessagingManager;

        // To call right after the server or host has started.
        internal void StartServer(PlayerRegistry playerRegistry)
        {
            running = true;
            registry = playerRegistry;
            Messaging.RegisterNamedMessageHandler(EatMessage, OnEat);
            networkManager.OnClientConnectedCallback += OnClientConnected;
            worldSync.EditApplied += OnEditApplied;
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

            if (registry != null)
            {
                networkManager.OnClientConnectedCallback -= OnClientConnected;
                worldSync.EditApplied -= OnEditApplied;
            }

            registry = null;
            lastPositions.Clear();
            sentCalories.Clear();
            sentTimes.Clear();
            received.Reset();
            running = false;
        }

        // Eats one of an item, from the game (right click with it held) or the inventory (right click
        // on it). Returns false if it is not food the local player has. Food that does not fit in
        // their stomach is not sent to the server (tooFull), so that they can be told at once.
        public bool TryEat(int item, out bool tooFull)
        {
            tooFull = false;
            Nutrition food = ecosystemSync.FoodOf(item);
            if (!food.IsFood || !inventorySync.Inventory.Has(item)) return false;

            tooFull = !Stomach.CanEat(food);
            if (!tooFull) RequestEat(item);
            return true;
        }

        // Eats one of an item the local player holds. The server checks they have it, that it is food
        // and that it fits; the item and the stomach come back changed.
        public void RequestEat(int item)
        {
            if (!running) return;

            if (registry != null) Eat(networkManager.LocalClientId, item);
            else Send(item);
        }

        private bool TryGetLocalState(out PlayerState state)
        {
            state = null;
            return registry != null && networkManager.IsHost && registry.TryGetState(networkManager.LocalClientId, out state);
        }

        private void Update()
        {
            if (registry == null) return;

            float seconds = Time.unscaledDeltaTime;
            foreach (NetworkClient client in networkManager.ConnectedClientsList)
            {
                if (client.PlayerObject == null || !registry.TryGetState(client.ClientId, out PlayerState state)) continue;

                Vector3 position = client.PlayerObject.transform.position;
                float spent = HungerRules.RestPerSecond * seconds;
                if (lastPositions.TryGetValue(client.ClientId, out Vector3 last))
                {
                    Vector3 step = position - last;
                    float walked = new Vector2(step.x, step.z).magnitude;
                    if (walked + Mathf.Abs(step.y) <= HungerRules.LongestStep)
                    {
                        spent += walked * HungerRules.PerBlockWalked + Mathf.Max(0f, step.y) * HungerRules.PerBlockClimbed;
                    }
                }

                lastPositions[client.ClientId] = position;
                state.Stomach.Burn(spent);
                PublishIfChanged(client.ClientId, state.Stomach);
            }

            // Forget players who left, so that a returning one does not count the way back as walked.
            departed.Clear();
            foreach (ulong clientId in lastPositions.Keys)
            {
                if (!networkManager.ConnectedClients.ContainsKey(clientId)) departed.Add(clientId);
            }

            foreach (ulong clientId in departed)
            {
                lastPositions.Remove(clientId);
                sentCalories.Remove(clientId);
                sentTimes.Remove(clientId);
            }
        }

        private void OnEditApplied(ulong clientId, bool broke)
        {
            if (!registry.TryGetState(clientId, out PlayerState state)) return;

            state.Stomach.Burn(broke ? HungerRules.PerBlockBroken : HungerRules.PerBlockPlaced);
        }

        private void Eat(ulong clientId, int item)
        {
            if (!registry.TryGetState(clientId, out PlayerState state)) return;

            Nutrition food = ecosystemSync.FoodOf(item);
            if (!state.Stomach.CanEat(food) || !inventorySync.TryConsume(clientId, item)) return;

            state.Stomach.Eat(food);
            Publish(clientId, state.Stomach);
        }

        private void OnClientConnected(ulong clientId)
        {
            if (registry.TryGetState(clientId, out PlayerState state)) Publish(clientId, state.Stomach);
        }

        private void OnEat(ulong senderId, FastBufferReader reader)
        {
            reader.ReadValueSafe(out int item);
            Eat(senderId, item);
        }

        private void PublishIfChanged(ulong clientId, Stomach stomach)
        {
            if (clientId == networkManager.LocalClientId) return;
            if (!sentCalories.TryGetValue(clientId, out float sent)) return;

            bool emptied = stomach.IsStarving && sent > 0f;
            bool due = Time.unscaledTime - sentTimes[clientId] >= SendInterval
                       && Mathf.Abs(stomach.Calories - sent) >= SendShare * Stomach.Capacity;
            if (emptied || due) Publish(clientId, stomach);
        }

        // Sends a player their stomach. The host reads its own directly.
        private void Publish(ulong clientId, Stomach stomach)
        {
            if (clientId == networkManager.LocalClientId) return;

            sentCalories[clientId] = stomach.Calories;
            sentTimes[clientId] = Time.unscaledTime;
            using (var writer = new FastBufferWriter(StateSize, Allocator.Temp))
            {
                writer.WriteValueSafe(stomach.Calories);
                for (int i = 0; i < Nutrition.NutrientCount; i++)
                {
                    writer.WriteValueSafe(stomach.Nutrient(i));
                }

                Messaging.SendNamedMessage(StateMessage, clientId, writer, NetworkDelivery.ReliableSequenced);
            }
        }

        // The same bytes as the save, so the same checks apply.
        private void OnState(ulong senderId, FastBufferReader reader)
        {
            var bytes = new byte[StateSize];
            for (int i = 0; i < bytes.Length; i++)
            {
                reader.ReadValueSafe(out bytes[i]);
            }

            using (var stream = new System.IO.MemoryStream(bytes))
            using (var binary = new System.IO.BinaryReader(stream))
            {
                try
                {
                    received.Read(binary);
                }
                catch (System.IO.InvalidDataException exception)
                {
                    Debug.LogWarning($"Ignored a stomach from the server: {exception.Message}");
                }
            }
        }

        private void Send(int item)
        {
            using (var writer = new FastBufferWriter(sizeof(int), Allocator.Temp))
            {
                writer.WriteValueSafe(item);
                Messaging.SendNamedMessage(EatMessage, NetworkManager.ServerClientId, writer, NetworkDelivery.ReliableSequenced);
            }
        }
    }
}

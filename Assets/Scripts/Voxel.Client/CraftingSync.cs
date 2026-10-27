using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using Voxel.Core;

namespace Voxel.Client
{
    // An order on the local player's crafting queue, as the server last told it.
    public sealed class CraftOrderView
    {
        public int Id;
        public int Recipe;
        public int Count;
        public bool Running;
        public CraftRefusal Waiting;
        public float SecondsLeft;
    }

    // Crafting from the bag (Recipes) through a queue per player (CraftQueue), in real time (never the
    // simulation's clock). Orders of so many of a recipe are queued with or without their ingredients;
    // the server runs one item at a time, the first order that can start, and only when its time is up
    // takes the ingredients, gives what it makes and burns its calories, so an order cancelled, or left
    // by a disconnection (the queue is not kept), costs nothing, and an item whose ingredients went
    // elsewhere meanwhile waits for them again. The player's machine is sent the queue whenever it
    // changes (Craft.State) and counts the item under way down itself.
    public class CraftingSync : MonoBehaviour
    {
        private const string QueueMessage = "Craft.Queue";
        private const string CancelMessage = "Craft.Cancel";
        private const string StateMessage = "Craft.State";
        private const string DoneMessage = "Craft.Done";
        private const string RefusedMessage = "Craft.Refused";

        // How often orders that wait are looked at again (the bag may have changed), in seconds.
        private const float RecheckInterval = 0.25f;

        [SerializeField] private NetworkManager networkManager;
        [SerializeField] private InventorySync inventorySync;
        [SerializeField] private HungerSync hungerSync;
        [SerializeField] private EcosystemSync ecosystemSync;

        // Server side: each player's queue, and what they were last sent of it.
        private sealed class Player
        {
            public readonly CraftQueue Queue = new CraftQueue();
            public long SentKey = long.MinValue;
            public float SinceCheck;
        }

        private readonly Dictionary<ulong, Player> players = new Dictionary<ulong, Player>();
        private readonly List<ulong> emptied = new List<ulong>();
        private PlayerRegistry registry;
        private bool running;

        // The local player's queue as this machine knows it.
        private readonly List<CraftOrderView> orders = new List<CraftOrderView>();
        private double stateTime;

        private bool IsServer => registry != null;

        private CustomMessagingManager Messaging => networkManager.CustomMessagingManager;

        public IReadOnlyList<CraftOrderView> Orders => orders;

        public bool IsFull => orders.Count >= CraftQueue.MaxOrders;

        // The order whose item is under way, if any.
        public CraftOrderView Active => orders.Find(o => o.Running);

        // Raised when the local player's queue changed.
        public event Action Changed;

        // Raised on the player's machine with the recipe of which they just crafted one.
        public event Action<int> Crafted;

        // Raised on the player's machine with why an order was refused.
        public event Action<CraftRefusal> Refused;

        public static string TextOf(CraftRefusal refusal)
        {
            switch (refusal)
            {
                case CraftRefusal.Missing: return "Missing ingredients";
                case CraftRefusal.NoRoom: return "No room in your bag for it";
                case CraftRefusal.Busy: return "Already crafting";
                case CraftRefusal.TooHungry: return InventorySync.TooHungryMessage;
                case CraftRefusal.QueueFull: return "The queue is full";
                default: return "You can't craft that";
            }
        }

        // How long the item under way of an order still takes, counted down since the state came.
        public float SecondsLeftOf(CraftOrderView order)
        {
            if (!order.Running) return order.SecondsLeft;

            return Mathf.Max(0f, order.SecondsLeft - (float)(Time.unscaledTimeAsDouble - stateTime));
        }

        // How far the item under way of an order has gone, 0 to 1.
        public float ProgressOf(CraftOrderView order)
        {
            float seconds = Recipes.All[order.Recipe].Seconds;
            return !order.Running || seconds <= 0f ? 0f : Mathf.Clamp01(1f - SecondsLeftOf(order) / seconds);
        }

        // To call right after the server or host has started.
        internal void StartServer(PlayerRegistry playerRegistry)
        {
            running = true;
            registry = playerRegistry;
            Messaging.RegisterNamedMessageHandler(QueueMessage, OnQueue);
            Messaging.RegisterNamedMessageHandler(CancelMessage, OnCancel);
            networkManager.OnClientDisconnectCallback += OnClientDisconnected;
        }

        // To call right after a client's connection has been launched.
        public void StartClient()
        {
            running = true;
            Messaging.RegisterNamedMessageHandler(StateMessage, OnState);
            Messaging.RegisterNamedMessageHandler(DoneMessage, OnDone);
            Messaging.RegisterNamedMessageHandler(RefusedMessage, OnRefused);
        }

        public void Stop()
        {
            if (!running) return;

            if (IsServer) networkManager.OnClientDisconnectCallback -= OnClientDisconnected;
            registry = null;
            players.Clear();
            running = false;
            ShowState(new List<CraftOrderView>());
        }

        // Whether the local player could queue an order now, as far as this machine knows: missing
        // ingredients do not stop it (the order waits for them).
        public CraftRefusal LocalCheck(int recipeIndex, IReadOnlyList<int> choices, int count)
        {
            if (!Recipes.TryGet(recipeIndex, out Recipe recipe) || count < 1 || count > CraftQueue.MaxCount) return CraftRefusal.Unknown;
            if (IsFull) return CraftRefusal.QueueFull;

            for (int i = 0; choices != null && i < choices.Count && i < recipe.Ingredients.Count; i++)
            {
                Ingredient ingredient = recipe.Ingredients[i];
                if (choices[i] != Items.None && (!ingredient.IsGroup || !ingredient.Matches(choices[i]))) return CraftRefusal.BadChoice;
            }

            return CraftRefusal.None;
        }

        public void RequestQueue(int recipeIndex, IReadOnlyList<int> choices, int count)
        {
            if (!running || !Recipes.TryGet(recipeIndex, out Recipe recipe)) return;

            CraftRefusal refusal = LocalCheck(recipeIndex, choices, count);
            if (refusal != CraftRefusal.None)
            {
                Refused?.Invoke(refusal);
                return;
            }

            int[] sent = new int[recipe.Ingredients.Count];
            for (int i = 0; i < sent.Length && choices != null && i < choices.Count; i++)
            {
                sent[i] = choices[i];
            }

            if (IsServer)
            {
                Queue(networkManager.LocalClientId, recipeIndex, sent, count);
                return;
            }

            Send(QueueMessage, NetworkManager.ServerClientId, writer =>
            {
                writer.WriteValueSafe((byte)recipeIndex);
                writer.WriteValueSafe((byte)count);
                writer.WriteValueSafe((byte)sent.Length);
                foreach (int choice in sent)
                {
                    writer.WriteValueSafe(choice);
                }
            });
        }

        public void RequestCancel(int orderId)
        {
            if (!running) return;

            if (IsServer) Cancel(networkManager.LocalClientId, orderId);
            else Send(CancelMessage, NetworkManager.ServerClientId, writer => writer.WriteValueSafe(orderId));
        }

        private void Update()
        {
            if (!IsServer || players.Count == 0) return;

            float seconds = Time.unscaledDeltaTime;
            emptied.Clear();
            foreach (KeyValuePair<ulong, Player> entry in players)
            {
                Step(entry.Key, entry.Value, seconds);
                if (entry.Value.Queue.IsEmpty) emptied.Add(entry.Key);
            }

            foreach (ulong clientId in emptied)
            {
                players.Remove(clientId);
            }
        }

        // ---- Server ----

        // One player's queue for a frame: the item under way runs; with none, waiting orders are looked
        // at again now and then.
        private void Step(ulong clientId, Player player, float seconds)
        {
            CraftQueue queue = player.Queue;
            player.SinceCheck += seconds;
            if (queue.Active == null && player.SinceCheck < RecheckInterval && player.SentKey != long.MinValue) return;

            player.SinceCheck = 0f;
            if (!registry.TryGetState(clientId, out PlayerState state))
            {
                queue.Clear();
                return;
            }

            CraftOrder made = queue.Update(seconds, order => CheckOrder(state, order), order => Make(clientId, state, order));
            if (made != null)
            {
                if (clientId == networkManager.LocalClientId) Crafted?.Invoke(made.Recipe);
                else Send(DoneMessage, clientId, writer => writer.WriteValueSafe((byte)made.Recipe));
            }

            if (queue.StateKey == player.SentKey) return;

            player.SentKey = queue.StateKey;
            SendState(clientId, queue);
        }

        private CraftRefusal CheckOrder(PlayerState state, CraftOrder order)
        {
            if (state.Stomach.IsStarving) return CraftRefusal.TooHungry;

            return Recipes.Check(state.Inventory, Recipes.All[order.Recipe], order.Choices, ecosystemSync.ItemWeights);
        }

        // An item's time is up: made now, if everything is still there.
        private CraftRefusal Make(ulong clientId, PlayerState state, CraftOrder order)
        {
            Recipe recipe = Recipes.All[order.Recipe];
            bool had = state.Inventory.Has(recipe.Output);
            CraftRefusal refusal = state.Stomach.IsStarving ? CraftRefusal.TooHungry : Recipes.TryCraft(state.Inventory, recipe, order.Choices, ecosystemSync.ItemWeights);
            if (refusal != CraftRefusal.None) return refusal;

            // Something new goes into the hotbar's first empty slot, as anything received.
            if (!had) state.Hotbar.Place(recipe.Output);
            hungerSync.BurnEffort(clientId, recipe.Calories);
            inventorySync.Publish(clientId);
            return CraftRefusal.None;
        }

        private void Queue(ulong clientId, int recipeIndex, int[] choices, int count)
        {
            if (!registry.TryGetState(clientId, out _)) return;

            if (!players.TryGetValue(clientId, out Player player))
            {
                player = new Player();
                players[clientId] = player;
            }

            if (player.Queue.Add(recipeIndex, choices, count) == null)
            {
                Refuse(clientId, player.Queue.IsFull ? CraftRefusal.QueueFull : CraftRefusal.Unknown);
                return;
            }

            // Looked at in this frame's update, which sends the new queue.
            player.SinceCheck = RecheckInterval;
        }

        private void Cancel(ulong clientId, int orderId)
        {
            if (!players.TryGetValue(clientId, out Player player) || !player.Queue.Remove(orderId)) return;

            player.SentKey = player.Queue.StateKey;
            SendState(clientId, player.Queue);
        }

        private void Refuse(ulong clientId, CraftRefusal refusal)
        {
            if (clientId == networkManager.LocalClientId) Refused?.Invoke(refusal);
            else Send(RefusedMessage, clientId, writer => writer.WriteValueSafe((byte)refusal));
        }

        private void SendState(ulong clientId, CraftQueue queue)
        {
            if (clientId == networkManager.LocalClientId)
            {
                var views = new List<CraftOrderView>();
                foreach (CraftOrder order in queue.Orders)
                {
                    views.Add(new CraftOrderView { Id = order.Id, Recipe = order.Recipe, Count = order.Count, Running = order.Running, Waiting = order.Waiting, SecondsLeft = order.SecondsLeft });
                }

                ShowState(views);
                return;
            }

            Send(StateMessage, clientId, writer =>
            {
                writer.WriteValueSafe((byte)queue.Orders.Count);
                foreach (CraftOrder order in queue.Orders)
                {
                    writer.WriteValueSafe(order.Id);
                    writer.WriteValueSafe((byte)order.Recipe);
                    writer.WriteValueSafe((byte)order.Count);
                    writer.WriteValueSafe(order.Running);
                    writer.WriteValueSafe((byte)order.Waiting);
                    writer.WriteValueSafe(order.SecondsLeft);
                }
            });
        }

        private void OnClientDisconnected(ulong clientId) => players.Remove(clientId);

        private void OnQueue(ulong senderId, FastBufferReader reader)
        {
            reader.ReadValueSafe(out byte recipeIndex);
            reader.ReadValueSafe(out byte count);
            reader.ReadValueSafe(out byte choiceCount);
            if (!Recipes.TryGet(recipeIndex, out Recipe recipe) || choiceCount != recipe.Ingredients.Count) return;

            var choices = new int[choiceCount];
            for (int i = 0; i < choiceCount; i++)
            {
                reader.ReadValueSafe(out choices[i]);
            }

            Queue(senderId, recipeIndex, choices, count);
        }

        private void OnCancel(ulong senderId, FastBufferReader reader)
        {
            reader.ReadValueSafe(out int orderId);
            Cancel(senderId, orderId);
        }

        // ---- Client ----

        private void ShowState(List<CraftOrderView> views)
        {
            orders.Clear();
            orders.AddRange(views);
            stateTime = Time.unscaledTimeAsDouble;
            Changed?.Invoke();
        }

        private void OnState(ulong senderId, FastBufferReader reader)
        {
            reader.ReadValueSafe(out byte count);
            if (count > CraftQueue.MaxOrders) return;

            var views = new List<CraftOrderView>();
            for (int i = 0; i < count; i++)
            {
                var view = new CraftOrderView();
                reader.ReadValueSafe(out view.Id);
                reader.ReadValueSafe(out byte recipe);
                reader.ReadValueSafe(out byte left);
                reader.ReadValueSafe(out view.Running);
                reader.ReadValueSafe(out byte waiting);
                reader.ReadValueSafe(out view.SecondsLeft);
                view.Recipe = recipe;
                view.Count = left;
                view.Waiting = (CraftRefusal)waiting;
                if (view.Recipe < Recipes.All.Count) views.Add(view);
            }

            ShowState(views);
        }

        private void OnDone(ulong senderId, FastBufferReader reader)
        {
            reader.ReadValueSafe(out byte recipeIndex);
            if (recipeIndex < Recipes.All.Count) Crafted?.Invoke(recipeIndex);
        }

        private void OnRefused(ulong senderId, FastBufferReader reader)
        {
            reader.ReadValueSafe(out byte refusal);
            Refused?.Invoke((CraftRefusal)refusal);
        }

        private void Send(string message, ulong clientId, Action<FastBufferWriter> write)
        {
            using (var writer = new FastBufferWriter(256, Allocator.Temp))
            {
                write(writer);
                Messaging.SendNamedMessage(message, clientId, writer, NetworkDelivery.ReliableSequenced);
            }
        }
    }
}

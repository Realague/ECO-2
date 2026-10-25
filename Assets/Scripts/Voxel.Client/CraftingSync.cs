using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using Voxel.Core;

namespace Voxel.Client
{
    // Crafting from the bag (Recipes). A player crafts one thing at a time, in real time (never the
    // simulation's clock): the server checks the recipe when it starts and again when its time is up,
    // only then taking the ingredients, giving what it makes and burning its calories, so a craft
    // cancelled, or left by a disconnection, costs nothing, and one whose ingredients went elsewhere
    // meanwhile is refused at the end. The player's machine is told when a craft starts and stops
    // (Craft.State) and counts down itself; it checks a request first, as far as it knows.
    public class CraftingSync : MonoBehaviour
    {
        private const string StartMessage = "Craft.Start";
        private const string CancelMessage = "Craft.Cancel";
        private const string StateMessage = "Craft.State";
        private const string DoneMessage = "Craft.Done";
        private const string RefusedMessage = "Craft.Refused";
        private const byte NoRecipe = 255;

        [SerializeField] private NetworkManager networkManager;
        [SerializeField] private InventorySync inventorySync;
        [SerializeField] private HungerSync hungerSync;
        [SerializeField] private EcosystemSync ecosystemSync;

        // Server side: each player's craft under way.
        private sealed class Job
        {
            public int Recipe;
            public int[] Choices;
            public double End;
        }

        private readonly Dictionary<ulong, Job> jobs = new Dictionary<ulong, Job>();
        private readonly List<ulong> due = new List<ulong>();
        private PlayerRegistry registry;
        private bool running;

        // The local player's craft as this machine knows it.
        private int activeRecipe = -1;
        private float activeSeconds;
        private double activeEnd;

        private bool IsServer => registry != null;

        private CustomMessagingManager Messaging => networkManager.CustomMessagingManager;

        // The recipe the local player is crafting (-1 for none), how far along and how long is left.
        public int ActiveRecipe => activeRecipe;

        public float SecondsLeft => activeRecipe < 0 ? 0f : Mathf.Max(0f, (float)(activeEnd - Time.unscaledTimeAsDouble));

        public float Progress => activeRecipe < 0 || activeSeconds <= 0f ? 0f : Mathf.Clamp01(1f - SecondsLeft / activeSeconds);

        // Raised when the local player's craft started or stopped.
        public event Action Changed;

        // Raised on the player's machine with the recipe they just crafted.
        public event Action<int> Crafted;

        // Raised on the player's machine with why a craft was refused.
        public event Action<CraftRefusal> Refused;

        public static string TextOf(CraftRefusal refusal)
        {
            switch (refusal)
            {
                case CraftRefusal.Missing: return "Missing ingredients";
                case CraftRefusal.NoRoom: return "No room in your bag for it";
                case CraftRefusal.Busy: return "Already crafting";
                case CraftRefusal.TooHungry: return InventorySync.TooHungryMessage;
                default: return "You can't craft that";
            }
        }

        // To call right after the server or host has started.
        internal void StartServer(PlayerRegistry playerRegistry)
        {
            running = true;
            registry = playerRegistry;
            Messaging.RegisterNamedMessageHandler(StartMessage, OnStart);
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
            jobs.Clear();
            running = false;
            ShowState(-1, 0f, 0f);
        }

        // Whether the local player could start crafting a recipe now, as far as this machine knows.
        public CraftRefusal LocalCheck(int recipeIndex, IReadOnlyList<int> choices)
        {
            if (!Recipes.TryGet(recipeIndex, out Recipe recipe)) return CraftRefusal.Unknown;
            if (activeRecipe >= 0) return CraftRefusal.Busy;
            if (hungerSync.Stomach.IsStarving) return CraftRefusal.TooHungry;

            return Recipes.Check(inventorySync.Inventory, recipe, choices, ecosystemSync.ItemWeights);
        }

        public void RequestCraft(int recipeIndex, IReadOnlyList<int> choices)
        {
            if (!running || !Recipes.TryGet(recipeIndex, out Recipe recipe)) return;

            CraftRefusal refusal = LocalCheck(recipeIndex, choices);
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
                Begin(networkManager.LocalClientId, recipeIndex, sent);
                return;
            }

            Send(StartMessage, NetworkManager.ServerClientId, writer =>
            {
                writer.WriteValueSafe((byte)recipeIndex);
                writer.WriteValueSafe((byte)sent.Length);
                foreach (int choice in sent)
                {
                    writer.WriteValueSafe(choice);
                }
            });
        }

        public void RequestCancel()
        {
            if (!running || activeRecipe < 0) return;

            if (IsServer) Cancel(networkManager.LocalClientId);
            else Send(CancelMessage, NetworkManager.ServerClientId, _ => { });
        }

        private void Update()
        {
            if (!IsServer || jobs.Count == 0) return;

            double now = Time.unscaledTimeAsDouble;
            due.Clear();
            foreach (KeyValuePair<ulong, Job> entry in jobs)
            {
                if (entry.Value.End <= now) due.Add(entry.Key);
            }

            foreach (ulong clientId in due)
            {
                Finish(clientId);
            }
        }

        // ---- Server ----

        private void Begin(ulong clientId, int recipeIndex, int[] choices)
        {
            if (!Recipes.TryGet(recipeIndex, out Recipe recipe) || !registry.TryGetState(clientId, out PlayerState state)) return;

            CraftRefusal refusal = jobs.ContainsKey(clientId) ? CraftRefusal.Busy
                : state.Stomach.IsStarving ? CraftRefusal.TooHungry
                : Recipes.Check(state.Inventory, recipe, choices, ecosystemSync.ItemWeights);
            if (refusal != CraftRefusal.None)
            {
                Refuse(clientId, refusal);
                return;
            }

            jobs[clientId] = new Job { Recipe = recipeIndex, Choices = choices, End = Time.unscaledTimeAsDouble + recipe.Seconds };
            SendState(clientId, recipeIndex, recipe.Seconds, recipe.Seconds);
        }

        private void Cancel(ulong clientId)
        {
            if (jobs.Remove(clientId)) SendState(clientId, -1, 0f, 0f);
        }

        // Time is up: made now, if everything is still there.
        private void Finish(ulong clientId)
        {
            if (!jobs.TryGetValue(clientId, out Job job)) return;

            jobs.Remove(clientId);
            SendState(clientId, -1, 0f, 0f);
            if (!registry.TryGetState(clientId, out PlayerState state) || !Recipes.TryGet(job.Recipe, out Recipe recipe)) return;

            bool had = state.Inventory.Has(recipe.Output);
            CraftRefusal refusal = state.Stomach.IsStarving ? CraftRefusal.TooHungry : Recipes.TryCraft(state.Inventory, recipe, job.Choices, ecosystemSync.ItemWeights);
            if (refusal != CraftRefusal.None)
            {
                Refuse(clientId, refusal);
                return;
            }

            // Something new goes into the hotbar's first empty slot, as anything received.
            if (!had) state.Hotbar.Place(recipe.Output);
            hungerSync.BurnEffort(clientId, recipe.Calories);
            inventorySync.Publish(clientId);
            if (clientId == networkManager.LocalClientId) Crafted?.Invoke(job.Recipe);
            else Send(DoneMessage, clientId, writer => writer.WriteValueSafe((byte)job.Recipe));
        }

        private void Refuse(ulong clientId, CraftRefusal refusal)
        {
            if (clientId == networkManager.LocalClientId) Refused?.Invoke(refusal);
            else Send(RefusedMessage, clientId, writer => writer.WriteValueSafe((byte)refusal));
        }

        private void SendState(ulong clientId, int recipeIndex, float seconds, float left)
        {
            if (clientId == networkManager.LocalClientId)
            {
                ShowState(recipeIndex, seconds, left);
                return;
            }

            Send(StateMessage, clientId, writer =>
            {
                writer.WriteValueSafe(recipeIndex < 0 ? NoRecipe : (byte)recipeIndex);
                writer.WriteValueSafe(seconds);
                writer.WriteValueSafe(left);
            });
        }

        private void OnClientDisconnected(ulong clientId) => jobs.Remove(clientId);

        private void OnStart(ulong senderId, FastBufferReader reader)
        {
            reader.ReadValueSafe(out byte recipeIndex);
            reader.ReadValueSafe(out byte count);
            if (!Recipes.TryGet(recipeIndex, out Recipe recipe) || count != recipe.Ingredients.Count) return;

            var choices = new int[count];
            for (int i = 0; i < count; i++)
            {
                reader.ReadValueSafe(out choices[i]);
            }

            Begin(senderId, recipeIndex, choices);
        }

        private void OnCancel(ulong senderId, FastBufferReader reader) => Cancel(senderId);

        // ---- Client ----

        private void ShowState(int recipeIndex, float seconds, float left)
        {
            activeRecipe = recipeIndex;
            activeSeconds = seconds;
            activeEnd = Time.unscaledTimeAsDouble + left;
            Changed?.Invoke();
        }

        private void OnState(ulong senderId, FastBufferReader reader)
        {
            reader.ReadValueSafe(out byte recipeIndex);
            reader.ReadValueSafe(out float seconds);
            reader.ReadValueSafe(out float left);
            ShowState(recipeIndex == NoRecipe || recipeIndex >= Recipes.All.Count ? -1 : recipeIndex, seconds, left);
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
            using (var writer = new FastBufferWriter(64, Allocator.Temp))
            {
                write(writer);
                Messaging.SendNamedMessage(message, clientId, writer, NetworkDelivery.ReliableSequenced);
            }
        }
    }
}

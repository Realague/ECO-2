using System;
using System.Collections.Generic;
using System.IO;
using Eco.Sim;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using Voxel.Core;

namespace Voxel.Client
{
    // Runs the ecosystem on the server and shows it to players.
    // Server: runs simulation steps at the clock's pace, validates player requests (felling a
    // plant, setting the speed of time) and broadcasts changes. Client: keeps the received list of
    // plants up to date. Every machine with a display draws the nearby plants.
    public class EcosystemSync : MonoBehaviour
    {
        private const string UpdateMessage = "Eco.Update";
        private const string FellMessage = "Eco.Fell";
        private const string CommandMessage = "Eco.Command";
        private const string AnimalsMessage = "Eco.Animals";
        private const string KillMessage = "Eco.Kill";
        private const string SaveFileName = "ecosystem.dat";

        private const byte SetMultiplierCommand = 0;
        private const byte AdvanceDaysCommand = 1;
        private const int MaxDaysPerCommand = 365;

        private const int UpdateHeaderSize = sizeof(long) + sizeof(float) + sizeof(bool) + 2 * sizeof(int);
        private const int PlantEntrySize = 4 * sizeof(int) + 2 * sizeof(byte);

        // A joining player receives the existing plants in batches, one per network tick, so as not to
        // flood their connection: 500 plants are about 9 KB.
        private const int SnapshotPlantsPerTick = 500;

        // Computing time granted to the simulation per frame: beyond it, the remaining steps wait.
        private const double StepBudgetMilliseconds = 8.0;
        private const float ClockSendInterval = 0.5f;
        private const int ColumnsPerInitialPlant = 64;

        // Each player receives, at most twice a second, the full list of the animals around them,
        // a little beyond what they see. The cost therefore does not depend on the size of the world.
        private const int AnimalEntrySize = 4 * sizeof(int) + sizeof(byte);
        private const float AnimalFeedInterval = 0.5f;
        private const float AnimalInterestMargin = 32f;
        private const float AnimalFeedMoveThreshold = 8f;
        private const float MinAnimalTravelSeconds = 0.25f;

        [SerializeField] private NetworkManager networkManager;
        [SerializeField] private InventorySync inventorySync;
        [SerializeField] private SpeciesAsset[] speciesAssets;
        [SerializeField] private AnimalSpeciesAsset[] animalAssets;
        [SerializeField] private float plantViewDistance = 128f;

        // Plants known to this machine: used for display and aiming.
        private readonly Dictionary<int, PlantView> plants = new Dictionary<int, PlantView>();
        private readonly HashSet<int> changedPlants = new HashSet<int>();
        private readonly HashSet<int> removedPlants = new HashSet<int>();
        private readonly List<Plant> outgoing = new List<Plant>();
        private readonly System.Diagnostics.Stopwatch stepTimer = new System.Diagnostics.Stopwatch();
        private List<Species> species;
        private List<AnimalSpecies> animalSpecies;
        private PlantRenderer plantRenderer;
        private AnimalViews animalViews;
        private bool running;

        // Server role only.
        private Ecosystem ecosystem;
        private SimClock clock;
        private Func<ulong, bool> isAdmin;
        private string savePath;
        private float nextClockSend;
        private bool clockChanged;

        // For each joining player, the id of the last plant sent to them.
        private readonly Dictionary<ulong, int> snapshots = new Dictionary<ulong, int>();
        private readonly List<ulong> snapshotClients = new List<ulong>();

        // For each player, the version of the animal world at the time of their last send.
        private readonly Dictionary<ulong, AnimalFeed> animalFeeds = new Dictionary<ulong, AnimalFeed>();
        private readonly List<Animal> nearbyAnimals = new List<Animal>();
        private int animalRevision;

        // Simulation clock as this machine knows it.
        private long stepCount;
        private float multiplier = 1f;
        private bool behind;

        public float Multiplier => multiplier;

        public string ClockText
        {
            get
            {
                long day = stepCount / SimClock.StepsPerDay + 1;
                int minutes = (int)(stepCount % SimClock.StepsPerDay) * SimClock.MinutesPerStep;
                string speed = multiplier > 0f ? $"speed ×{multiplier:0.##}" : "time paused";
                return $"Day {day}, {minutes / 60:00}:{minutes % 60:00}   {speed}{(behind ? " (the machine cannot keep up)" : "")}";
            }
        }

        private CustomMessagingManager Messaging => networkManager.CustomMessagingManager;

        private void Awake()
        {
            species = new List<Species>(speciesAssets.Length);
            foreach (SpeciesAsset asset in speciesAssets)
            {
                species.Add(asset.ToSpecies());
            }

            animalSpecies = new List<AnimalSpecies>(animalAssets.Length);
            foreach (AnimalSpeciesAsset asset in animalAssets)
            {
                animalSpecies.Add(asset.ToSpecies(speciesAssets, animalAssets));
            }

            // A server without a display simulates plants and animals but does not draw them.
            bool rendering = !Application.isBatchMode;
            if (rendering) plantRenderer = new PlantRenderer(speciesAssets);
            animalViews = new AnimalViews(animalAssets, animalSpecies, rendering);
        }

        private void Update()
        {
            if (ecosystem == null) return;

            clock.Advance(Time.unscaledDeltaTime);
            stepTimer.Restart();
            while (clock.TryTakeStep())
            {
                ecosystem.Step();
                if (stepTimer.Elapsed.TotalMilliseconds >= StepBudgetMilliseconds)
                {
                    clock.DropBacklog();
                    break;
                }
            }

            if (stepCount != ecosystem.StepCount || multiplier != clock.Multiplier || behind != clock.IsBehind)
            {
                stepCount = ecosystem.StepCount;
                multiplier = clock.Multiplier;
                behind = clock.IsBehind;
                clockChanged = true;
            }
        }

        private void LateUpdate()
        {
            if (plantRenderer == null) return;

            Camera viewer = Camera.main;
            if (viewer != null) plantRenderer.Render(viewer.transform.position, plantViewDistance);
            animalViews.Render();
        }

        // To call right after the server or host has started, once its world is loaded.
        public void StartServer(World world, string directory, float startMultiplier, Func<ulong, bool> isAdmin)
        {
            running = true;
            this.isAdmin = isAdmin;
            savePath = Path.Combine(directory, SaveFileName);
            ecosystem = LoadOrCreate(world);
            clock = new SimClock { Multiplier = startMultiplier };

            foreach (Plant plant in ecosystem.Plants)
            {
                SetView(plant.Id, ViewOf(plant));
            }

            ecosystem.PlantAdded += OnPlantUpserted;
            ecosystem.PlantChanged += OnPlantUpserted;
            ecosystem.PlantRemoved += OnPlantRemoved;

            // The host is a player too: it receives nearby animals like the others, without going through the network.
            if (networkManager.IsHost) animalFeeds[networkManager.LocalClientId] = new AnimalFeed();

            Messaging.RegisterNamedMessageHandler(FellMessage, OnFellRequest);
            Messaging.RegisterNamedMessageHandler(KillMessage, OnKillRequest);
            Messaging.RegisterNamedMessageHandler(CommandMessage, OnCommand);
            networkManager.OnClientConnectedCallback += OnClientConnected;
            networkManager.OnClientDisconnectCallback += OnClientDisconnected;
            networkManager.NetworkTickSystem.Tick += SendChanges;
        }

        // To call right after a client's connection has been launched.
        public void StartClient()
        {
            running = true;
            Messaging.RegisterNamedMessageHandler(UpdateMessage, OnUpdate);
            Messaging.RegisterNamedMessageHandler(AnimalsMessage, OnAnimals);
        }

        public void Stop()
        {
            if (!running) return;

            if (ecosystem != null)
            {
                networkManager.OnClientConnectedCallback -= OnClientConnected;
                networkManager.OnClientDisconnectCallback -= OnClientDisconnected;
                if (networkManager.NetworkTickSystem != null) networkManager.NetworkTickSystem.Tick -= SendChanges;
            }

            ecosystem = null;
            clock = null;
            plants.Clear();
            plantRenderer?.Clear();
            animalViews.Clear();
            changedPlants.Clear();
            removedPlants.Clear();
            snapshots.Clear();
            animalFeeds.Clear();
            running = false;
            stepCount = 0;
            multiplier = 1f;
            behind = false;
        }

        // Writes the ecosystem to disk. No effect outside the server role.
        public void Save()
        {
            if (ecosystem == null) return;

            try
            {
                ecosystem.SaveToFile(savePath);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                Debug.LogError($"Failed to save the ecosystem: {e.Message} ({savePath})");
            }
        }

        // Strikes what the local player is aiming at, at most maxDistance away: kills the animal or fells
        // the plant nearest along the ray. Returns false if the ray hits neither. The direction
        // must be normalized.
        public bool TryStrike(System.Numerics.Vector3 origin, System.Numerics.Vector3 direction, float maxDistance)
        {
            bool plantHit = TryPickPlant(origin, direction, maxDistance, out int plantId, out float plantDistance);
            if (animalViews.TryPick(origin, direction, plantHit ? plantDistance : maxDistance, out int animalId, out _))
            {
                RequestKill(animalId);
                return true;
            }

            if (plantHit) RequestFell(plantId);
            return plantHit;
        }

        private bool TryPickPlant(
            System.Numerics.Vector3 origin, System.Numerics.Vector3 direction, float maxDistance, out int plantId, out float nearest)
        {
            plantId = 0;
            nearest = maxDistance;
            bool found = false;

            foreach (KeyValuePair<int, PlantView> entry in plants)
            {
                PlantView plant = entry.Value;
                PlantShape.GetBounds(
                    species[plant.SpeciesIndex], plant.X, plant.Y, plant.Z, plant.Stage,
                    out System.Numerics.Vector3 min, out System.Numerics.Vector3 max);

                float distance = PlantShape.RayDistance(origin, direction, min, max);
                if (distance >= 0f && distance <= nearest)
                {
                    nearest = distance;
                    plantId = entry.Key;
                    found = true;
                }
            }

            return found;
        }

        // Felling requested by the local player. The server decides; a client removes the plant right
        // away for responsiveness, and the server sends it back if it refuses.
        public void RequestFell(int plantId)
        {
            if (!running) return;

            if (ecosystem != null)
            {
                TryFell(networkManager.LocalClientId, plantId);
                return;
            }

            RemoveView(plantId);
            using (var writer = new FastBufferWriter(sizeof(int), Allocator.Temp))
            {
                writer.WriteValueSafe(plantId);
                Messaging.SendNamedMessage(FellMessage, NetworkManager.ServerClientId, writer, NetworkDelivery.ReliableFragmentedSequenced);
            }
        }

        // Kill requested by the local player, on the same principle as felling.
        public void RequestKill(int animalId)
        {
            if (!running) return;

            if (ecosystem != null)
            {
                TryKill(networkManager.LocalClientId, animalId);
                return;
            }

            animalViews.Remove(animalId);
            using (var writer = new FastBufferWriter(sizeof(int), Allocator.Temp))
            {
                writer.WriteValueSafe(animalId);
                Messaging.SendNamedMessage(KillMessage, NetworkManager.ServerClientId, writer, NetworkDelivery.ReliableFragmentedSequenced);
            }
        }

        // Admins only: the server ignores the request from other players.
        public void RequestMultiplier(float value)
        {
            RequestCommand(SetMultiplierCommand, value);
        }

        public void RequestAdvanceDays(int days)
        {
            RequestCommand(AdvanceDaysCommand, days);
        }

        private void RequestCommand(byte command, float value)
        {
            if (!running) return;

            if (ecosystem != null)
            {
                ApplyCommand(networkManager.LocalClientId, command, value);
                return;
            }

            using (var writer = new FastBufferWriter(sizeof(byte) + sizeof(float), Allocator.Temp))
            {
                writer.WriteValueSafe(command);
                writer.WriteValueSafe(value);
                Messaging.SendNamedMessage(CommandMessage, NetworkManager.ServerClientId, writer, NetworkDelivery.ReliableFragmentedSequenced);
            }
        }

        private Ecosystem LoadOrCreate(World world)
        {
            if (File.Exists(savePath))
            {
                try
                {
                    return Ecosystem.LoadFromFile(savePath, world, species, animalSpecies);
                }
                catch (Exception e) when (e is IOException || e is InvalidDataException)
                {
                    Debug.LogWarning(
                        $"Unreadable ecosystem save, a new ecosystem will replace it at the next save: {e.Message} ({savePath})");
                }
            }

            var created = new Ecosystem(world, species, animalSpecies, world.Seed);
            created.Populate(world.SizeX * world.SizeZ / ColumnsPerInitialPlant);
            created.PopulateAnimals();
            return created;
        }

        private void SetView(int plantId, PlantView view)
        {
            plants[plantId] = view;
            plantRenderer?.Set(plantId, view);
        }

        private void RemoveView(int plantId)
        {
            if (!plants.TryGetValue(plantId, out PlantView view)) return;

            plants.Remove(plantId);
            plantRenderer?.Remove(plantId, view);
        }

        private void OnPlantUpserted(Plant plant)
        {
            SetView(plant.Id, ViewOf(plant));
            changedPlants.Add(plant.Id);
        }

        private void OnPlantRemoved(int plantId)
        {
            RemoveView(plantId);
            changedPlants.Remove(plantId);
            removedPlants.Add(plantId);
        }

        private bool TryFell(ulong clientId, int plantId)
        {
            if (!ecosystem.TryGet(plantId, out Plant plant)) return false;
            if (!networkManager.ConnectedClients.TryGetValue(clientId, out NetworkClient client)) return false;
            if (client.PlayerObject == null) return false;

            Vector3 eyes = client.PlayerObject.transform.position + Vector3.up * PlayerRules.EyeHeight;
            PlantShape.GetBounds(
                species[plant.SpeciesIndex], plant.X, plant.Y, plant.Z, plant.Stage,
                out System.Numerics.Vector3 min, out System.Numerics.Vector3 max);

            float maxDistance = PlayerRules.Reach + BlockEditRules.ReachTolerance;
            if (PlantShape.DistanceSquared(eyes.ToNumerics(), min, max) > maxDistance * maxDistance) return false;
            if (!ecosystem.Remove(plantId)) return false;

            // Felling yields wood according to the plant's size, and a seed if it was adult.
            int wood = Mathf.RoundToInt(speciesAssets[plant.SpeciesIndex].WoodYield * PlantShape.Scale(plant.Stage));
            inventorySync.Give(clientId, Items.Wood, wood);
            if (plant.Growth >= 1f) inventorySync.Give(clientId, Items.Seed(plant.SpeciesIndex), 1);
            return true;
        }

        // Server side: sprouts a seed in the column a player is aiming at, if it is within their
        // reach and the ground, the room and the fertility allow it.
        public bool TryPlantSeed(ulong clientId, int speciesIndex, int x, int z)
        {
            if (ecosystem == null || speciesIndex < 0 || speciesIndex >= species.Count) return false;
            if (!networkManager.ConnectedClients.TryGetValue(clientId, out NetworkClient client)) return false;
            if (client.PlayerObject == null) return false;

            Vector3 position = client.PlayerObject.transform.position;
            float dx = x + 0.5f - position.x;
            float dz = z + 0.5f - position.z;
            float maxDistance = PlayerRules.Reach + BlockEditRules.ReachTolerance;
            if (dx * dx + dz * dz > maxDistance * maxDistance) return false;

            return ecosystem.TryPlant(speciesIndex, x, z);
        }

        // Name of a plant species, for display (for instance that of a seed).
        public string PlantName(int speciesIndex)
        {
            return speciesIndex >= 0 && speciesIndex < speciesAssets.Length ? speciesAssets[speciesIndex].name : "?";
        }

        private void OnFellRequest(ulong senderId, FastBufferReader reader)
        {
            reader.ReadValueSafe(out int plantId);
            if (TryFell(senderId, plantId)) return;

            // Refusal: if the plant still exists, it is sent back to the requester, who had removed it in anticipation.
            if (ecosystem.TryGet(plantId, out Plant plant))
            {
                outgoing.Clear();
                outgoing.Add(plant);
                SendUpdate(senderId, outgoing, null);
            }
        }

        private void OnCommand(ulong senderId, FastBufferReader reader)
        {
            reader.ReadValueSafe(out byte command);
            reader.ReadValueSafe(out float value);
            ApplyCommand(senderId, command, value);
        }

        private void ApplyCommand(ulong clientId, byte command, float value)
        {
            if (!isAdmin(clientId) || float.IsNaN(value)) return;

            if (command == SetMultiplierCommand)
            {
                clock.Multiplier = value;
                Debug.Log($"Simulation speed set to ×{clock.Multiplier}.");
            }
            else if (command == AdvanceDaysCommand)
            {
                int days = Mathf.Clamp(Mathf.RoundToInt(value), 0, MaxDaysPerCommand);
                clock.QueueDays(days);
                Debug.Log($"Simulation advanced by {days} day(s).");
            }
        }

        private void OnClientConnected(ulong clientId)
        {
            if (clientId != networkManager.LocalClientId) snapshots[clientId] = 0;
            if (!animalFeeds.ContainsKey(clientId)) animalFeeds[clientId] = new AnimalFeed();
        }

        private void OnClientDisconnected(ulong clientId)
        {
            snapshots.Remove(clientId);
            animalFeeds.Remove(clientId);
        }

        private bool TryKill(ulong clientId, int animalId)
        {
            if (!ecosystem.TryGetAnimal(animalId, out Animal animal)) return false;
            if (!networkManager.ConnectedClients.TryGetValue(clientId, out NetworkClient client)) return false;
            if (client.PlayerObject == null) return false;

            AnimalSpecies kind = animalSpecies[animal.SpeciesIndex];
            Vector3 eyes = client.PlayerObject.transform.position + Vector3.up * PlayerRules.EyeHeight;
            float half = kind.Width * 0.5f;
            var min = new System.Numerics.Vector3(animal.X + 0.5f - half, animal.Y, animal.Z + 0.5f - half);
            var max = new System.Numerics.Vector3(animal.X + 0.5f + half, animal.Y + kind.Height, animal.Z + 0.5f + half);

            // The animal may have moved one step since what the player saw.
            float maxDistance = PlayerRules.Reach + BlockEditRules.ReachTolerance + kind.MoveRange;
            if (PlantShape.DistanceSquared(eyes.ToNumerics(), min, max) > maxDistance * maxDistance) return false;

            ecosystem.RemoveAnimal(animalId);
            animalRevision++;
            return true;
        }

        private void OnKillRequest(ulong senderId, FastBufferReader reader)
        {
            reader.ReadValueSafe(out int animalId);
            if (TryKill(senderId, animalId)) return;

            // Refusal: the next animal list sent to this player will give back the one they removed in anticipation.
            if (animalFeeds.TryGetValue(senderId, out AnimalFeed feed)) feed.Revision = -1;
        }

        // Sends each player the list of the animals around them, when it may have changed: a simulation
        // step ran, an animal was killed, or the player moved.
        private void SendAnimalFeeds()
        {
            float now = Time.unscaledTime;
            float radius = plantViewDistance + AnimalInterestMargin;

            foreach (KeyValuePair<ulong, AnimalFeed> entry in animalFeeds)
            {
                AnimalFeed feed = entry.Value;
                if (now < feed.NextTime) continue;
                if (!networkManager.ConnectedClients.TryGetValue(entry.Key, out NetworkClient client)) continue;
                if (client.PlayerObject == null) continue;

                Vector3 position = client.PlayerObject.transform.position;
                bool moved = (position - feed.Position).sqrMagnitude > AnimalFeedMoveThreshold * AnimalFeedMoveThreshold;
                if (feed.Step == ecosystem.StepCount && feed.Revision == animalRevision && !moved) continue;

                feed.Step = ecosystem.StepCount;
                feed.Revision = animalRevision;
                feed.Position = position;
                feed.NextTime = now + AnimalFeedInterval;

                nearbyAnimals.Clear();
                foreach (Animal animal in ecosystem.Animals)
                {
                    float dx = animal.X + 0.5f - position.x;
                    float dz = animal.Z + 0.5f - position.z;
                    if (dx * dx + dz * dz <= radius * radius) nearbyAnimals.Add(animal);
                }

                if (entry.Key == networkManager.LocalClientId) ShowNearbyAnimals();
                else SendNearbyAnimals(entry.Key);
            }
        }

        private void ShowNearbyAnimals()
        {
            animalViews.BeginList();
            foreach (Animal animal in nearbyAnimals)
            {
                animalViews.Add(animal.Id, animal.SpeciesIndex, new Vector3(animal.X + 0.5f, animal.Y, animal.Z + 0.5f), AnimalTravelSeconds());
            }

            animalViews.EndList();
        }

        private void SendNearbyAnimals(ulong clientId)
        {
            using (var writer = new FastBufferWriter(sizeof(int) + nearbyAnimals.Count * AnimalEntrySize, Allocator.Temp))
            {
                writer.WriteValueSafe(nearbyAnimals.Count);
                foreach (Animal animal in nearbyAnimals)
                {
                    writer.WriteValueSafe(animal.Id);
                    writer.WriteValueSafe((byte)animal.SpeciesIndex);
                    writer.WriteValueSafe(animal.X);
                    writer.WriteValueSafe(animal.Y);
                    writer.WriteValueSafe(animal.Z);
                }

                Messaging.SendNamedMessage(AnimalsMessage, clientId, writer, NetworkDelivery.ReliableFragmentedSequenced);
            }
        }

        private void OnAnimals(ulong senderId, FastBufferReader reader)
        {
            reader.ReadValueSafe(out int count);
            float travelSeconds = AnimalTravelSeconds();

            animalViews.BeginList();
            for (int i = 0; i < count; i++)
            {
                reader.ReadValueSafe(out int animalId);
                reader.ReadValueSafe(out byte speciesIndex);
                reader.ReadValueSafe(out int x);
                reader.ReadValueSafe(out int y);
                reader.ReadValueSafe(out int z);
                animalViews.Add(animalId, speciesIndex, new Vector3(x + 0.5f, y, z + 0.5f), travelSeconds);
            }

            animalViews.EndList();
        }

        // Duration of the displayed move between two positions: that of one simulation step at the current speed.
        private float AnimalTravelSeconds()
        {
            float stepSeconds = SimClock.RealSecondsPerStep / Mathf.Max(multiplier, 0.01f);
            return Mathf.Clamp(stepSeconds, MinAnimalTravelSeconds, SimClock.RealSecondsPerStep);
        }

        // Once per network tick: only the final state of each changed plant is sent, even if the
        // accelerated simulation changed it several times in the meantime.
        private void SendChanges()
        {
            SendSnapshots();
            SendAnimalFeeds();

            bool clockDue = clockChanged && Time.unscaledTime >= nextClockSend;
            if (changedPlants.Count == 0 && removedPlants.Count == 0 && !clockDue) return;

            outgoing.Clear();
            foreach (int plantId in changedPlants)
            {
                if (ecosystem.TryGet(plantId, out Plant plant)) outgoing.Add(plant);
            }

            foreach (ulong clientId in networkManager.ConnectedClientsIds)
            {
                if (clientId != networkManager.LocalClientId) SendUpdate(clientId, outgoing, removedPlants);
            }

            changedPlants.Clear();
            removedPlants.Clear();
            clockChanged = false;
            nextClockSend = Time.unscaledTime + ClockSendInterval;
        }

        // Sends each joining player the next batch of existing plants. Plants are ordered by
        // increasing id: resuming after the last id sent stays correct even if plants were born
        // or died between two batches.
        private void SendSnapshots()
        {
            if (snapshots.Count == 0) return;

            IReadOnlyList<Plant> all = ecosystem.Plants;
            snapshotClients.Clear();
            snapshotClients.AddRange(snapshots.Keys);

            foreach (ulong clientId in snapshotClients)
            {
                int start = FirstIndexAfter(all, snapshots[clientId]);
                int end = Mathf.Min(all.Count, start + SnapshotPlantsPerTick);

                outgoing.Clear();
                for (int i = start; i < end; i++)
                {
                    outgoing.Add(all[i]);
                }

                // The first batch is sent even when empty: it gives the player the clock.
                SendUpdate(clientId, outgoing, null);

                if (end >= all.Count) snapshots.Remove(clientId);
                else snapshots[clientId] = all[end - 1].Id;
            }
        }

        private static int FirstIndexAfter(IReadOnlyList<Plant> sortedById, int plantId)
        {
            int low = 0;
            int high = sortedById.Count;
            while (low < high)
            {
                int middle = (low + high) / 2;
                if (sortedById[middle].Id <= plantId) low = middle + 1;
                else high = middle;
            }

            return low;
        }

        private void SendUpdate(ulong clientId, List<Plant> upserted, HashSet<int> removed)
        {
            int removedCount = removed != null ? removed.Count : 0;
            int size = UpdateHeaderSize + upserted.Count * PlantEntrySize + removedCount * sizeof(int);
            using (var writer = new FastBufferWriter(size, Allocator.Temp))
            {
                writer.WriteValueSafe(ecosystem.StepCount);
                writer.WriteValueSafe(clock.Multiplier);
                writer.WriteValueSafe(clock.IsBehind);

                writer.WriteValueSafe(upserted.Count);
                foreach (Plant plant in upserted)
                {
                    writer.WriteValueSafe(plant.Id);
                    writer.WriteValueSafe((byte)plant.SpeciesIndex);
                    writer.WriteValueSafe(plant.X);
                    writer.WriteValueSafe(plant.Y);
                    writer.WriteValueSafe(plant.Z);
                    writer.WriteValueSafe((byte)plant.Stage);
                }

                writer.WriteValueSafe(removedCount);
                if (removed != null)
                {
                    foreach (int plantId in removed)
                    {
                        writer.WriteValueSafe(plantId);
                    }
                }

                Messaging.SendNamedMessage(UpdateMessage, clientId, writer, NetworkDelivery.ReliableFragmentedSequenced);
            }
        }

        private void OnUpdate(ulong senderId, FastBufferReader reader)
        {
            reader.ReadValueSafe(out stepCount);
            reader.ReadValueSafe(out multiplier);
            reader.ReadValueSafe(out behind);

            reader.ReadValueSafe(out int upsertedCount);
            for (int i = 0; i < upsertedCount; i++)
            {
                reader.ReadValueSafe(out int plantId);
                reader.ReadValueSafe(out byte speciesIndex);
                reader.ReadValueSafe(out int x);
                reader.ReadValueSafe(out int y);
                reader.ReadValueSafe(out int z);
                reader.ReadValueSafe(out byte stage);

                // A species unknown to this client cannot be displayed: it is ignored.
                if (speciesIndex >= species.Count) continue;

                SetView(plantId, new PlantView { SpeciesIndex = speciesIndex, X = x, Y = y, Z = z, Stage = stage });
            }

            reader.ReadValueSafe(out int removedCount);
            for (int i = 0; i < removedCount; i++)
            {
                reader.ReadValueSafe(out int plantId);
                RemoveView(plantId);
            }
        }

        private static PlantView ViewOf(Plant plant)
        {
            return new PlantView { SpeciesIndex = plant.SpeciesIndex, X = plant.X, Y = plant.Y, Z = plant.Z, Stage = plant.Stage };
        }

        private sealed class AnimalFeed
        {
            public long Step = -1;
            public int Revision = -1;
            public Vector3 Position;
            public float NextTime;
        }
    }
}

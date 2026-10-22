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
    // Server: runs simulation steps at the clock's pace, validates player requests (harvesting,
    // setting the speed of time), finishes plants felled by hits (MiningSync: a tree falls, leaving
    // its stump and its logs) and broadcasts changes. Client: keeps the received list of plants up to
    // date. Every machine with a display draws the nearby plants.
    public class EcosystemSync : MonoBehaviour
    {
        private const string UpdateMessage = "Eco.Update";
        private const string FelledMessage = "Eco.Felled";
        private const string HarvestMessage = "Eco.Harvest";
        private const string CommandMessage = "Eco.Command";
        private const string AnimalsMessage = "Eco.Animals";
        private const string CensusRequestMessage = "Eco.CensusRequest";
        private const string CensusMessage = "Eco.Census";
        private const string SaveFileName = "ecosystem.dat";

        private const byte SetMultiplierCommand = 0;
        private const byte AdvanceDaysCommand = 1;
        private const int MaxDaysPerCommand = 365;

        private const int UpdateHeaderSize = sizeof(long) + sizeof(float) + sizeof(bool) + 2 * sizeof(int);
        private const int PlantEntrySize = 4 * sizeof(int) + 2 * sizeof(byte);
        private const int FelledSize = 4 * sizeof(int) + 3 * sizeof(byte) + sizeof(float);

        // A falling tree's logs show up once it has hit the ground.
        private const float LogsAppearAfter = PlantRenderer.FallSeconds + 0.2f;

        // The first log lies this far from the tree's foot along its fall, past the stump.
        private const float FirstLogDistance = PlantShape.StumpHeight + 0.6f;

        // A joining player receives the existing plants in batches, one per network tick, so as not to
        // flood their connection: 500 plants are about 9 KB.
        private const int SnapshotPlantsPerTick = 500;

        // Computing time granted to the simulation per frame: beyond it, the remaining steps wait.
        private const double StepBudgetMilliseconds = 8.0;
        private const float ClockSendInterval = 0.5f;

        // A new world is sown with one attempt per species for this many block columns, so that
        // adding species does not thin the others out (with the first 16 species, one plant attempt
        // per 64 columns).
        private const int ColumnsPerInitialAttempt = 1024;

        // Each player receives, at most twice a second, the full list of the animals around them,
        // a little beyond what they see. The cost therefore does not depend on the size of the world.
        private const int AnimalEntrySize = 4 * sizeof(int) + sizeof(byte);
        private const int CarcassEntrySize = 3 * sizeof(int) + sizeof(byte);
        private const float AnimalFeedInterval = 0.5f;
        private const float AnimalInterestMargin = 32f;
        private const float AnimalFeedMoveThreshold = 8f;

        // The map asks for a census every 2 s while it is open; a player asking faster is ignored, and a
        // census grid larger than this many cells (a world of 8,192 columns a side) is refused.
        private const float CensusInterval = 1f;
        private const int MaxCensusCells = 1 << 20;

        [SerializeField] private NetworkManager networkManager;
        [SerializeField] private InventorySync inventorySync;
        [SerializeField] private LogSync logSync;

        // The world of this machine, on which the display works out the way each animal walks.
        [SerializeField] private WorldSync worldSync;
        [SerializeField] private SpeciesAsset[] speciesAssets;
        [SerializeField] private AnimalSpeciesAsset[] animalAssets;
        [SerializeField] private float plantViewDistance = 128f;

        // Plants known to this machine: used for display and aiming.
        private readonly Dictionary<int, PlantView> plants = new Dictionary<int, PlantView>();

        // The plant standing in each column (at most one), so that aiming only looks at the columns a ray crosses.
        private readonly Dictionary<long, int> plantAtColumn = new Dictionary<long, int>();
        private readonly HashSet<int> changedPlants = new HashSet<int>();
        private readonly HashSet<int> removedPlants = new HashSet<int>();
        private readonly List<Plant> outgoing = new List<Plant>();
        private readonly System.Diagnostics.Stopwatch stepTimer = new System.Diagnostics.Stopwatch();
        private List<Species> species;
        private List<AnimalSpecies> animalSpecies;
        private PlantRenderer plantRenderer;
        private ItemWeights itemWeights;
        private World serverWorld;

        // Server side: what a felling or a harvest gives.
        private readonly List<(int item, int count)> rewards = new List<(int item, int count)>();
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

        // Server: when each player last had a census, to refuse requests coming faster than CensusInterval.
        private readonly Dictionary<ulong, float> lastCensus = new Dictionary<ulong, float>();
        private readonly List<Animal> nearbyAnimals = new List<Animal>();
        private readonly List<Carcass> nearbyCarcasses = new List<Carcass>();
        private int animalRevision;

        // Simulation clock as this machine knows it.
        private long stepCount;
        private float multiplier = 1f;
        private bool behind;
        private float clockKnownAt;

        public float Multiplier => multiplier;

        // The server cannot run steps as fast as the speed asks.
        public bool IsBehind => behind;

        // The simulation's time, in steps, as this machine sees it now: the last count it knows,
        // carried on at the current speed until the next one arrives, by at most what the clock's
        // messages are apart. Smooth enough to turn the sun; display only.
        public double StepTime
        {
            get
            {
                double ahead = (Time.unscaledTime - clockKnownAt) * multiplier / SimClock.RealSecondsPerStep;
                double mostAhead = Math.Max(1.0, 2.0 * ClockSendInterval * multiplier / SimClock.RealSecondsPerStep);
                return stepCount + Math.Min(ahead, mostAhead);
            }
        }

        // The plants this machine knows, for the minimap: one added or grown, one gone, all gone.
        internal event Action<int, PlantView> PlantShown;
        internal event Action<int, PlantView> PlantHidden;
        internal event Action PlantsCleared;

        internal IReadOnlyList<SpeciesAsset> PlantAssets => speciesAssets;
        internal IReadOnlyList<AnimalSpeciesAsset> AnimalAssets => animalAssets;

        // The displayed animals: species index and current position of each.
        internal void GetAnimalPositions(List<KeyValuePair<int, Vector3>> into)
        {
            animalViews.GetPositions(into);
        }

        public string ClockText
        {
            get
            {
                long total = (long)SimClock.MinutesAt(stepCount);
                long day = total / SimClock.MinutesPerDay + 1;
                int minutes = (int)(total % SimClock.MinutesPerDay);
                string speed = multiplier > 0f ? $"speed ×{multiplier:0.##}" : "time paused";
                return $"Day {day}, {minutes / 60:00}:{minutes % 60:00}   {speed}{(behind ? " (the machine cannot keep up)" : "")}";
            }
        }

        private CustomMessagingManager Messaging => networkManager.CustomMessagingManager;

        // Items' weights for the carrying limit, crops' from their species.
        public ItemWeights ItemWeights => itemWeights ??= new ItemWeights(Array.ConvertAll(speciesAssets, asset => asset.ProduceWeight));

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
                if (stepCount != ecosystem.StepCount || multiplier != clock.Multiplier) clockKnownAt = Time.unscaledTime;
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
            serverWorld = world;
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

            Messaging.RegisterNamedMessageHandler(HarvestMessage, OnHarvestRequest);
            Messaging.RegisterNamedMessageHandler(CommandMessage, OnCommand);
            Messaging.RegisterNamedMessageHandler(CensusRequestMessage, OnCensusRequest);
            networkManager.OnClientConnectedCallback += OnClientConnected;
            networkManager.OnClientDisconnectCallback += OnClientDisconnected;
            networkManager.NetworkTickSystem.Tick += SendChanges;
        }

        // To call right after a client's connection has been launched.
        public void StartClient()
        {
            running = true;
            Messaging.RegisterNamedMessageHandler(UpdateMessage, OnUpdate);
            Messaging.RegisterNamedMessageHandler(FelledMessage, OnFelled);
            Messaging.RegisterNamedMessageHandler(AnimalsMessage, OnAnimals);
            Messaging.RegisterNamedMessageHandler(CensusMessage, OnCensus);
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
            serverWorld = null;
            clock = null;
            plants.Clear();
            plantAtColumn.Clear();
            plantRenderer?.Clear();
            PlantsCleared?.Invoke();
            animalViews.Clear();
            changedPlants.Clear();
            removedPlants.Clear();
            snapshots.Clear();
            animalFeeds.Clear();
            lastCensus.Clear();
            Census = new SpeciesCensus();
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

        // What a strike of the local player hits, at most maxDistance away, holding held: the plant
        // nearest along the ray, to hit (plantId, at distance along the ray) if held is its tool (an
        // axe for a plant that gives wood, a scythe for another, a shovel for a stump). Animals are
        // hunted with the bow, never struck. Returns false if the ray hits neither; missingTool is
        // then, or when the strike cannot hit, the tool it would have needed (Items.None when it can).
        // The direction must be normalized.
        public bool TryStrike(System.Numerics.Vector3 origin, System.Numerics.Vector3 direction, float maxDistance, int held, out int missingTool, out int plantId, out float distance)
        {
            missingTool = Items.None;
            bool plantHit = TryPickPlant(origin, direction, maxDistance, out plantId, out distance);
            if (animalViews.TryPick(origin, direction, plantHit ? distance : maxDistance, out _, out _))
            {
                missingTool = Items.Bow;
                plantId = 0;
                return true;
            }

            if (!plantHit) return false;

            int tool = ToolFor(plants[plantId]);
            if (held != tool)
            {
                missingTool = tool;
                plantId = 0;
            }

            return true;
        }

        // The tool a plant of this species is felled with.
        private int FellingTool(int speciesIndex) => ToolRules.ToFell(speciesAssets[speciesIndex].WoodYield > 0);

        // The tool a plant shown here is hit with: its felling tool, or a shovel to dig a stump out.
        internal int ToolFor(PlantView view) => view.Stump ? Items.Shovel : FellingTool(view.SpeciesIndex);

        internal bool TryGetView(int plantId, out PlantView view) => plants.TryGetValue(plantId, out view);

        // A shown tree's trunk where its stump will be cut (PlantRenderer.TryGetTrunk), for the notch
        // the axe cuts there. False for a stump, or a plant whose model leaves none.
        internal bool TryGetTrunk(PlantView view, out Vector3 centre, out Vector2 halfSize, out Material cutMaterial)
        {
            centre = default;
            halfSize = default;
            cutMaterial = null;
            return plantRenderer != null && !view.Stump && plantRenderer.TryGetTrunk(view, out centre, out halfSize, out cutMaterial);
        }

        // The wood a plant gives when felled, by its species and size.
        private int WoodOf(int speciesIndex, int stage) => MiningRules.WoodOf(speciesAssets[speciesIndex].WoodYield, PlantShape.Scale(stage));

        // Hits a plant takes: by the wood in a tree, one cut for a plant without wood, a few shovel
        // hits for a stump.
        internal int HitsNeeded(PlantView view) => view.Stump ? MiningRules.StumpHits : MiningRules.HitsToFell(WoodOf(view.SpeciesIndex, view.Stage));

        // The calories of one hit on a plant.
        internal float HitCalories(PlantView view)
        {
            if (view.Stump) return HungerRules.PerShovelHitOnStump;
            return speciesAssets[view.SpeciesIndex].WoodYield > 0 ? HungerRules.PerAxeHit : HungerRules.PerScytheHit;
        }

        // The colour of the chips a hit on a plant of this species sends flying: its bark's.
        internal Color ChipColour(int speciesIndex)
        {
            Material bark = (uint)speciesIndex < (uint)speciesAssets.Length ? speciesAssets[speciesIndex].BarkMaterial : null;
            if (bark == null) return new Color(0.45f, 0.33f, 0.2f);

            return bark.HasProperty("_BaseColor") ? bark.GetColor("_BaseColor") : bark.color;
        }

        // The aiming box of a plant shown here.
        internal void BoundsOf(PlantView view, out Vector3 min, out Vector3 max)
        {
            PlantShape.GetBounds(species[view.SpeciesIndex], view.X, view.Y, view.Z, view.Stage, view.Stump, out System.Numerics.Vector3 low, out System.Numerics.Vector3 high);
            min = low.ToUnity();
            max = high.ToUnity();
        }

        // Client side: a plant the local player's last hit cuts (one cut, no fall), shown gone at once;
        // the server sends it back (RestorePlantTo) if it refuses.
        internal void PredictRemove(int plantId)
        {
            if (running && ecosystem == null) RemoveView(plantId);
        }

        // Server side: sends a plant as it is to a client that anticipated its removal wrongly.
        internal void RestorePlantTo(ulong clientId, int plantId)
        {
            if (ecosystem == null || clientId == networkManager.LocalClientId || !ecosystem.TryGet(plantId, out Plant plant)) return;

            outgoing.Clear();
            outgoing.Add(plant);
            SendUpdate(clientId, outgoing, null);
        }

        // Server side: whether a player's hit on a plant counts: it exists, it is within their reach,
        // and they own the tool it takes (the server cannot see the one in hand). needed is the hits
        // it takes, calories what a hit costs, min and max its aiming box.
        internal bool CheckPlantHit(ulong clientId, int plantId, out int needed, out float calories, out Vector3 min, out Vector3 max)
        {
            needed = 0;
            calories = 0f;
            min = max = Vector3.zero;
            if (ecosystem == null || !ecosystem.TryGet(plantId, out Plant plant) || !IsWithinReach(clientId, plant)) return false;

            PlantView view = ViewOf(plant);
            if (!inventorySync.Owns(clientId, ToolFor(view))) return false;

            needed = HitsNeeded(view);
            calories = HitCalories(view);
            BoundsOf(view, out min, out max);
            return true;
        }

        // The tool a crop of this species is harvested with (Items.None: by hand).
        private int HarvestTool(int speciesIndex) => ToolRules.ToHarvest(speciesAssets[speciesIndex].Harvest == HarvestKind.Annual);

        // Which shown animal a flying arrow's segment hits first (AnimalViews.TryHitArrow).
        internal bool TryHitArrow(Vector3 a, Vector3 b, out int animalId, out bool inHead, out float distance)
        {
            animalId = 0;
            inHead = false;
            distance = 0f;
            return animalViews != null && animalViews.TryHitArrow(a, b, out animalId, out inHead, out distance);
        }

        // What the local player is aiming at, at most maxDistance away: the name of the animal or plant
        // nearest along the ray, the one a strike would hit. Returns false if there is neither. Cheap
        // enough to run every frame.
        internal bool TryAim(System.Numerics.Vector3 origin, System.Numerics.Vector3 direction, float maxDistance, out string name, out int tool)
        {
            name = null;
            tool = Items.None;
            if (animalNames == null)
            {
                animalNames = Array.ConvertAll(animalAssets, asset => DisplayName(asset.name));
                plantNames = Array.ConvertAll(speciesAssets, asset => DisplayName(asset.name));
                youngPlantNames = Array.ConvertAll(plantNames, plantName => "Young " + char.ToLowerInvariant(plantName[0]) + plantName.Substring(1));
                ripePlantNames = Array.ConvertAll(plantNames, plantName => new[]
                {
                    plantName, plantName + " (unripe)", plantName + " (ripe)", plantName + " (fully ripe)"
                });
            }

            bool plantHit = TryPickPlant(origin, direction, maxDistance, out int plantId, out float plantDistance);
            if (animalViews != null && animalViews.TryPick(origin, direction, plantHit ? plantDistance : maxDistance, out int animalId, out _))
            {
                int kind = animalViews.SpeciesOf(animalId);
                if (kind >= 0) name = animalNames[kind];
                tool = Items.Bow;
                return name != null;
            }

            if (!plantHit) return false;

            PlantView plant = plants[plantId];
            tool = ToolFor(plant);
            if (plant.Stump) name = plantNames[plant.SpeciesIndex] + " stump";
            else if (plant.Stage < Plant.StageCount - 1) name = youngPlantNames[plant.SpeciesIndex];
            else name = ripePlantNames[plant.SpeciesIndex][Mathf.Clamp(plant.RipeStage, 0, Plant.RipeStageCount)];
            return true;
        }

        // Names shown when aiming, worked out once; a crop's name says how ripe it is. Not kept across
        // a script reload: the Editor would keep the flat arrays but not the jagged one.
        [NonSerialized] private string[] animalNames;
        [NonSerialized] private string[] plantNames;
        [NonSerialized] private string[] youngPlantNames;
        [NonSerialized] private string[][] ripePlantNames;

        // The plant nearest along the ray. A plant's aiming box stays within its own column, so only
        // the columns the ray crosses are looked at, walked one after the other (Amanatides and Woo, in
        // the horizontal plane; distances along the ray, whose direction is normalized).
        private bool TryPickPlant(
            System.Numerics.Vector3 origin, System.Numerics.Vector3 direction, float maxDistance, out int plantId, out float nearest)
        {
            plantId = 0;
            nearest = maxDistance;
            bool found = false;

            int x = Mathf.FloorToInt(origin.X);
            int z = Mathf.FloorToInt(origin.Z);
            int stepX = direction.X > 0f ? 1 : -1;
            int stepZ = direction.Z > 0f ? 1 : -1;
            float deltaX = direction.X != 0f ? Mathf.Abs(1f / direction.X) : float.PositiveInfinity;
            float deltaZ = direction.Z != 0f ? Mathf.Abs(1f / direction.Z) : float.PositiveInfinity;
            float nextX = direction.X != 0f ? (direction.X > 0f ? x + 1 - origin.X : origin.X - x) * deltaX : float.PositiveInfinity;
            float nextZ = direction.Z != 0f ? (direction.Z > 0f ? z + 1 - origin.Z : origin.Z - z) * deltaZ : float.PositiveInfinity;

            float travelled = 0f;
            while (travelled <= nearest)
            {
                if (plantAtColumn.TryGetValue(ColumnKey(x, z), out int id) && plants.TryGetValue(id, out PlantView plant))
                {
                    PlantShape.GetBounds(
                        species[plant.SpeciesIndex], plant.X, plant.Y, plant.Z, plant.Stage, plant.Stump,
                        out System.Numerics.Vector3 min, out System.Numerics.Vector3 max);

                    float distance = PlantShape.RayDistance(origin, direction, min, max);
                    if (distance >= 0f && distance <= nearest)
                    {
                        nearest = distance;
                        plantId = id;
                        found = true;
                    }
                }

                if (nextX < nextZ)
                {
                    travelled = nextX;
                    nextX += deltaX;
                    x += stepX;
                }
                else
                {
                    travelled = nextZ;
                    nextZ += deltaZ;
                    z += stepZ;
                }
            }

            return found;
        }

        private static long ColumnKey(int x, int z) => ((long)x << 32) | (uint)z;

        // ---- Commands (server only) ----

        // The animal or plant species whose asset name matches, compared as Command.Normalise does
        // ("snowhare" is SnowHare).
        internal bool TryFindSpecies(string normalisedName, out bool isAnimal, out int index)
        {
            for (index = 0; index < animalAssets.Length; index++)
            {
                isAnimal = true;
                if (Command.Normalise(animalAssets[index].name) == normalisedName) return true;
            }

            for (index = 0; index < speciesAssets.Length; index++)
            {
                isAnimal = false;
                if (Command.Normalise(speciesAssets[index].name) == normalisedName) return true;
            }

            isAnimal = false;
            index = -1;
            return false;
        }

        internal string SpeciesList()
        {
            return "Animals: " + string.Join(", ", Array.ConvertAll(animalAssets, asset => DisplayName(asset.name)))
                + ". Plants: " + string.Join(", ", Array.ConvertAll(speciesAssets, asset => DisplayName(asset.name))) + ".";
        }

        internal string SpeciesName(bool isAnimal, int index) =>
            DisplayName(isAnimal ? animalAssets[index].name : speciesAssets[index].name);

        // Places up to count adults (or grown plants) of a species in the columns around (x, z),
        // nearest first, where the species can stand or grow and the column is free. Returns how many.
        internal int Spawn(bool isAnimal, int index, int count, int x, int z)
        {
            World world = worldSync.World;
            if (ecosystem == null || world == null) return 0;

            const int MaxRing = 6;
            int placed = 0;
            for (int ring = 0; ring <= MaxRing && placed < count; ring++)
            {
                for (int dz = -ring; dz <= ring && placed < count; dz++)
                {
                    for (int dx = -ring; dx <= ring && placed < count; dx++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dz)) != ring) continue;

                        int cx = x + dx, cz = z + dz;
                        if (!world.InBounds(cx, 0, cz)) continue;

                        bool done = isAnimal ? ecosystem.TryPlaceAnimal(index, cx, cz) : ecosystem.TryPlaceGrownPlant(index, cx, cz);
                        if (done) placed++;
                    }
                }
            }

            if (isAnimal && placed > 0) animalRevision++;
            return placed;
        }

        // An asset's name as players read it: "TallGrass" becomes "Tall grass".
        internal static string DisplayName(string assetName)
        {
            var text = new System.Text.StringBuilder(assetName.Length + 4);
            for (int i = 0; i < assetName.Length; i++)
            {
                char c = assetName[i];
                if (i > 0 && char.IsUpper(c))
                {
                    text.Append(' ');
                    c = char.ToLowerInvariant(c);
                }

                text.Append(c);
            }

            return text.ToString();
        }

        // Right click: picks the crop of the plant aimed at, at most maxDistance away: an annual with a
        // scythe held, a perennial's fruit by hand. Returns false if the ray hits no ripe crop, the
        // click then using the held item; missingTool is the scythe when an annual was aimed at without it.
        public bool TryHarvestAimed(System.Numerics.Vector3 origin, System.Numerics.Vector3 direction, float maxDistance, int held, out int missingTool)
        {
            missingTool = Items.None;
            if (!TryPickPlant(origin, direction, maxDistance, out int plantId, out _)) return false;
            if (plants[plantId].RipeStage < 1 || speciesAssets[plants[plantId].SpeciesIndex].Harvest == HarvestKind.None) return false;

            int tool = HarvestTool(plants[plantId].SpeciesIndex);
            if (tool != Items.None && held != tool)
            {
                missingTool = tool;
                return true;
            }

            RequestHarvest(plantId);
            return true;
        }

        // Harvest requested by the local player. The server decides; a client shows the result right
        // away (an annual pulled up, a perennial without its fruit), and the server sends
        // the plant back if it refuses.
        public void RequestHarvest(int plantId)
        {
            if (!running) return;

            if (ecosystem != null)
            {
                TryHarvest(networkManager.LocalClientId, plantId);
                return;
            }

            if (plants.TryGetValue(plantId, out PlantView view))
            {
                if (speciesAssets[view.SpeciesIndex].Harvest == HarvestKind.Annual)
                {
                    RemoveView(plantId);
                }
                else
                {
                    view.RipeStage = 0;
                    SetView(plantId, view);
                }
            }

            using (var writer = new FastBufferWriter(sizeof(int), Allocator.Temp))
            {
                writer.WriteValueSafe(plantId);
                Messaging.SendNamedMessage(HarvestMessage, NetworkManager.ServerClientId, writer, NetworkDelivery.ReliableFragmentedSequenced);
            }
        }

        // The last census received (SpeciesCensus), for the map's species layers.
        public SpeciesCensus Census { get; private set; } = new SpeciesCensus();

        // Asks for a census, with the density grid of one species (Kind.None: the counts only).
        // The host works it out at once; a client asks the server, which answers if its last
        // census is at least CensusInterval old.
        public void RequestCensus(SpeciesCensus.Kind kind, int species)
        {
            if (!running) return;

            if (ecosystem != null)
            {
                Census = TakeCensus(kind, species, Census.Version + 1);
                return;
            }

            using (var writer = new FastBufferWriter(sizeof(byte) + sizeof(int), Allocator.Temp))
            {
                writer.WriteValueSafe((byte)kind);
                writer.WriteValueSafe(species);
                Messaging.SendNamedMessage(CensusRequestMessage, NetworkManager.ServerClientId, writer, NetworkDelivery.ReliableFragmentedSequenced);
            }
        }

        // Server side: the counts of every species and the density grid of one.
        private SpeciesCensus TakeCensus(SpeciesCensus.Kind kind, int species, int version)
        {
            var census = new SpeciesCensus
            {
                PlantCounts = ecosystem.PlantCounts(),
                AnimalCounts = ecosystem.AnimalCounts(),
                Version = version
            };

            for (int n = 0; n < Ecosystem.NutrientCount; n++)
            {
                census.SoilMeans[n] = ecosystem.MeanSoil((Nutrient)n);
            }

            if (kind == SpeciesCensus.Kind.None) return census;
            if (kind == SpeciesCensus.Kind.Soil && (species < 0 || species >= Ecosystem.NutrientCount)) return census;

            census.GridKind = kind;
            census.GridSpecies = species;
            census.CellsX = ecosystem.CensusCellsX(SpeciesCensus.CellSize);
            census.CellsZ = ecosystem.CensusCellsZ(SpeciesCensus.CellSize);
            if (kind == SpeciesCensus.Kind.Soil)
            {
                float[] levels = ecosystem.SoilLevels((Nutrient)species, SpeciesCensus.CellSize);
                census.Grid = new byte[levels.Length];
                for (int i = 0; i < levels.Length; i++)
                {
                    census.Grid[i] = (byte)Mathf.Clamp(Mathf.RoundToInt(levels[i] * SpeciesCensus.SoilLevelScale), 0, byte.MaxValue);
                }

                return census;
            }

            int[] cells = kind == SpeciesCensus.Kind.Plant
                ? ecosystem.PlantDensity(species, SpeciesCensus.CellSize)
                : ecosystem.AnimalDensity(species, SpeciesCensus.CellSize);
            census.Grid = new byte[cells.Length];
            for (int i = 0; i < cells.Length; i++)
            {
                census.Grid[i] = (byte)Mathf.Min(cells[i], byte.MaxValue);
            }

            return census;
        }

        private void OnCensusRequest(ulong senderId, FastBufferReader reader)
        {
            reader.ReadValueSafe(out byte kind);
            reader.ReadValueSafe(out int species);
            if (kind > (byte)SpeciesCensus.Kind.Soil) return;

            // The map asks every 2 s while it is open: anything faster is ignored.
            float now = Time.unscaledTime;
            if (lastCensus.TryGetValue(senderId, out float last) && now - last < CensusInterval) return;

            lastCensus[senderId] = now;
            SpeciesCensus census = TakeCensus((SpeciesCensus.Kind)kind, species, 0);
            int size = 4 * sizeof(int) + (census.PlantCounts.Length + census.AnimalCounts.Length) * sizeof(int)
                + census.SoilMeans.Length * sizeof(float) + sizeof(byte) + 4 * sizeof(int) + census.Grid.Length;
            using (var writer = new FastBufferWriter(size, Allocator.Temp))
            {
                WriteCounts(writer, census.PlantCounts);
                WriteCounts(writer, census.AnimalCounts);
                foreach (float mean in census.SoilMeans)
                {
                    writer.WriteValueSafe(mean);
                }

                writer.WriteValueSafe((byte)census.GridKind);
                writer.WriteValueSafe(census.GridSpecies);
                writer.WriteValueSafe(census.CellsX);
                writer.WriteValueSafe(census.CellsZ);
                writer.WriteValueSafe(census.Grid.Length);
                writer.WriteBytesSafe(census.Grid);
                Messaging.SendNamedMessage(CensusMessage, senderId, writer, NetworkDelivery.ReliableFragmentedSequenced);
            }
        }

        private void OnCensus(ulong senderId, FastBufferReader reader)
        {
            var census = new SpeciesCensus
            {
                PlantCounts = ReadCounts(reader),
                AnimalCounts = ReadCounts(reader),
                Version = Census.Version + 1
            };

            for (int n = 0; n < census.SoilMeans.Length; n++)
            {
                reader.ReadValueSafe(out census.SoilMeans[n]);
            }

            reader.ReadValueSafe(out byte kind);
            reader.ReadValueSafe(out census.GridSpecies);
            reader.ReadValueSafe(out census.CellsX);
            reader.ReadValueSafe(out census.CellsZ);
            reader.ReadValueSafe(out int length);
            if (length < 0 || length != census.CellsX * census.CellsZ || length > MaxCensusCells) return;

            census.GridKind = (SpeciesCensus.Kind)kind;
            census.Grid = new byte[length];
            if (length > 0) reader.ReadBytesSafe(ref census.Grid, length);
            Census = census;
        }

        private static void WriteCounts(FastBufferWriter writer, int[] counts)
        {
            writer.WriteValueSafe(counts.Length);
            foreach (int count in counts)
            {
                writer.WriteValueSafe(count);
            }
        }

        private static int[] ReadCounts(FastBufferReader reader)
        {
            reader.ReadValueSafe(out int length);
            var counts = new int[Mathf.Clamp(length, 0, 1024)];
            for (int i = 0; i < counts.Length; i++)
            {
                reader.ReadValueSafe(out counts[i]);
            }

            return counts;
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
                    Ecosystem loaded = Ecosystem.LoadFromFile(savePath, world, species, animalSpecies);
                    if (loaded.SavedSpeciesCount < species.Count)
                    {
                        // Species added since the save are sown as in a new world, or they would never appear.
                        int sown = loaded.Populate(world.SizeX * world.SizeZ / ColumnsPerInitialAttempt * species.Count, loaded.SavedSpeciesCount);
                        Debug.Log($"{species.Count - loaded.SavedSpeciesCount} plant species added since the ecosystem was saved: {sown} plants sown.");
                    }

                    return loaded;
                }
                catch (Exception e) when (e is IOException || e is InvalidDataException)
                {
                    Debug.LogWarning(
                        $"Unreadable ecosystem save, a new ecosystem will replace it at the next save: {e.Message} ({savePath})");
                }
            }

            var created = new Ecosystem(world, species, animalSpecies, world.Seed);
            created.Populate(world.SizeX * world.SizeZ / ColumnsPerInitialAttempt * species.Count);
            created.PopulateAnimals();
            return created;
        }

        private void SetView(int plantId, PlantView view)
        {
            plants[plantId] = view;
            plantAtColumn[ColumnKey(view.X, view.Z)] = plantId;
            plantRenderer?.Set(plantId, view);
            PlantShown?.Invoke(plantId, view);
        }

        private void RemoveView(int plantId)
        {
            if (!plants.TryGetValue(plantId, out PlantView view)) return;

            plants.Remove(plantId);
            long column = ColumnKey(view.X, view.Z);
            if (plantAtColumn.TryGetValue(column, out int standing) && standing == plantId) plantAtColumn.Remove(column);
            plantRenderer?.Remove(plantId, view);
            PlantHidden?.Invoke(plantId, view);
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

        // Server side: the last hit on a plant (MiningSync). A stump is dug out, leaving a log. A
        // tree falls (every machine plays its fall: Eco.Felled), leaving its stump, which sprouts again
        // later, and its wood as logs along where it fell, to pick up; a plant without wood is cut. A
        // seed if it was adult and its crop if it was ripe go to the player, what they can carry of it,
        // so nothing is lost by felling rather than picking it.
        internal void FinishPlant(ulong clientId, int plantId)
        {
            if (ecosystem == null || !ecosystem.TryGet(plantId, out Plant plant)) return;

            var foot = new Vector3(plant.X + 0.5f, plant.Y, plant.Z + 0.5f);
            if (plant.IsStump)
            {
                ecosystem.Remove(plantId);
                logSync.DropAt(foot, UnityEngine.Random.Range(0f, 2f * Mathf.PI), plant.SpeciesIndex);
                return;
            }

            SpeciesAsset asset = speciesAssets[plant.SpeciesIndex];
            rewards.Clear();
            if (plant.Growth >= 1f) rewards.Add((Items.Seed(plant.SpeciesIndex), 1));
            if (asset.Harvest != HarvestKind.None) AddCrop(plant.SpeciesIndex, plant.RipeStage, false);
            GiveRewards(clientId);

            int wood = WoodOf(plant.SpeciesIndex, plant.Stage);
            if (wood <= 0)
            {
                ecosystem.Remove(plantId);
                return;
            }

            // It falls away from the player.
            Vector3 away = foot - PlayerPosition(clientId, foot);
            away.y = 0f;
            Vector3 direction = away.sqrMagnitude > 1e-4f ? away.normalized : Vector3.right;
            float yaw = Mathf.Atan2(direction.x, direction.z);

            PlantView view = ViewOf(plant);
            bool stump = asset.HasStump;
            BroadcastFelled(plantId, view, yaw, stump);
            plantRenderer?.StartFall(view, yaw);
            if (stump) ecosystem.CutToStump(plantId);
            else ecosystem.Remove(plantId);

            float length = Mathf.Max(FirstLogDistance, species[plant.SpeciesIndex].Height * PlantShape.Scale(plant.Stage));
            logSync.DropAlongFall(foot, direction, FirstLogDistance, length, MiningRules.LogsOf(wood), plant.SpeciesIndex, LogsAppearAfter);
        }

        // Where a player stands, or fallback if they are gone.
        private Vector3 PlayerPosition(ulong clientId, Vector3 fallback)
        {
            return networkManager.ConnectedClients.TryGetValue(clientId, out NetworkClient client) && client.PlayerObject != null
                ? client.PlayerObject.transform.position
                : fallback;
        }

        // Tells every other machine a tree fell, so that they play its fall before showing its stump.
        private void BroadcastFelled(int plantId, PlantView view, float yaw, bool stump)
        {
            using (var writer = new FastBufferWriter(FelledSize, Allocator.Temp))
            {
                writer.WriteValueSafe(plantId);
                writer.WriteValueSafe((byte)view.SpeciesIndex);
                writer.WriteValueSafe(view.X);
                writer.WriteValueSafe(view.Y);
                writer.WriteValueSafe(view.Z);
                writer.WriteValueSafe(PackStages(view.Stage, view.RipeStage, false));
                writer.WriteValueSafe(yaw);
                writer.WriteValueSafe(stump ? (byte)1 : (byte)0);
                foreach (ulong clientId in networkManager.ConnectedClientsIds)
                {
                    if (clientId != networkManager.LocalClientId) Messaging.SendNamedMessage(FelledMessage, clientId, writer, NetworkDelivery.ReliableFragmentedSequenced);
                }
            }
        }

        // A tree fell: its fall is played, and it is replaced at once by its stump (or nothing), which
        // the next update confirms.
        private void OnFelled(ulong senderId, FastBufferReader reader)
        {
            reader.ReadValueSafe(out int plantId);
            reader.ReadValueSafe(out byte speciesIndex);
            reader.ReadValueSafe(out int x);
            reader.ReadValueSafe(out int y);
            reader.ReadValueSafe(out int z);
            reader.ReadValueSafe(out byte stages);
            reader.ReadValueSafe(out float yaw);
            reader.ReadValueSafe(out byte stump);
            if (speciesIndex >= species.Count || !float.IsFinite(yaw)) return;

            PlantView view = UnpackView(speciesIndex, x, y, z, stages);
            plantRenderer?.StartFall(view, yaw);
            if (stump != 0)
            {
                view.Stump = true;
                view.RipeStage = 0;
                SetView(plantId, view);
            }
            else
            {
                RemoveView(plantId);
            }
        }

        // Server side: a player picks a ripe crop. An annual is pulled up and gives seeds too.
        private bool TryHarvest(ulong clientId, int plantId)
        {
            if (!ecosystem.TryGet(plantId, out Plant plant) || !IsWithinReach(clientId, plant)) return false;

            int tool = HarvestTool(plant.SpeciesIndex);
            if (tool != Items.None && !inventorySync.Owns(clientId, tool)) return false;

            rewards.Clear();
            AddCrop(plant.SpeciesIndex, plant.RipeStage, speciesAssets[plant.SpeciesIndex].Harvest == HarvestKind.Annual);
            if (!inventorySync.CanReceiveAny(clientId, rewards) || !ecosystem.TryHarvest(plantId, out _)) return false;

            GiveRewards(clientId);
            return true;
        }

        // What a harvest at this stage of ripeness gives: a share of the crop and of the straw, and,
        // with seeds, a share of an annual's seeds (none when picked unripe).
        private void AddCrop(int speciesIndex, int ripeStage, bool withSeeds)
        {
            SpeciesAsset asset = speciesAssets[speciesIndex];
            rewards.Add((Items.Produce(speciesIndex), CropYield.Share(asset.ProduceYield, ripeStage)));
            rewards.Add((Items.Straw, CropYield.Share(asset.StrawYield, ripeStage)));
            if (withSeeds) rewards.Add((Items.Seed(speciesIndex), CropYield.Seeds(asset.SeedYield, ripeStage)));
        }

        // Gives what fits; the rest is left behind.
        private void GiveRewards(ulong clientId)
        {
            foreach ((int item, int count) in rewards)
            {
                inventorySync.Give(clientId, item, count);
            }
        }

        // Whether a player's eyes are within reach of a plant, with the same tolerance as for blocks.
        private bool IsWithinReach(ulong clientId, Plant plant)
        {
            if (!networkManager.ConnectedClients.TryGetValue(clientId, out NetworkClient client)) return false;
            if (client.PlayerObject == null) return false;

            Vector3 eyes = client.PlayerObject.transform.position + Vector3.up * PlayerRules.EyeHeight;
            PlantShape.GetBounds(
                species[plant.SpeciesIndex], plant.X, plant.Y, plant.Z, plant.Stage, plant.IsStump,
                out System.Numerics.Vector3 min, out System.Numerics.Vector3 max);

            float maxDistance = PlayerRules.Reach + BlockEditRules.ReachTolerance;
            return PlantShape.DistanceSquared(eyes.ToNumerics(), min, max) <= maxDistance * maxDistance;
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

            // A player sows only on soil they tilled (the bed, for a plant rooted in shallow water).
            if (!IsTilled(serverWorld, x, z)) return false;

            return ecosystem.TryPlant(speciesIndex, x, z);
        }

        // Whether the topmost solid block of a column is tilled soil.
        internal static bool IsTilled(World world, int x, int z)
        {
            int surface = world.GetSurfaceHeight(x, z);
            return surface > 0 && Blocks.TypeOf(world.GetBlock(x, surface - 1, z)) == Blocks.TilledSoil;
        }

        // Server side: whether an animal's body takes up this block, which then cannot be built on.
        public bool IsAnimalInside(BlockPos pos)
        {
            return ecosystem != null && ecosystem.IsAnimalInside(pos.X, pos.Y, pos.Z);
        }

        // Name of a plant species, for display (for instance that of a seed).
        public string PlantName(int speciesIndex)
        {
            return speciesIndex >= 0 && speciesIndex < speciesAssets.Length ? speciesAssets[speciesIndex].name : "?";
        }

        // Name of what a plant species' harvest gives (Items.Produce), for display.
        public string ProduceName(int speciesIndex)
        {
            return speciesIndex >= 0 && speciesIndex < speciesAssets.Length ? speciesAssets[speciesIndex].ProduceName : "?";
        }

        // What eating an item gives: a crop's food values, nothing for anything else.
        public Nutrition FoodOf(int item)
        {
            if (!Items.IsProduce(item)) return default;

            int speciesIndex = Items.ProduceSpecies(item);
            return speciesIndex < speciesAssets.Length ? speciesAssets[speciesIndex].Food : default;
        }

        private void OnHarvestRequest(ulong senderId, FastBufferReader reader)
        {
            reader.ReadValueSafe(out int plantId);
            if (TryHarvest(senderId, plantId)) return;

            // Refusal: the plant, if it still exists, is sent back to the requester, who had changed it in anticipation.
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

        // Server side: an arrow whose flight followed this path says it hit an animal. The animal must
        // be near the path (it may have moved one step since the shooter saw it, plus a block); it then
        // takes the damage, and dies once its health is spent, leaving its carcass. feet is where the
        // arrow falls, at the animal's feet. Returns false, changing nothing, if the hit is not plausible.
        internal bool TryWoundAnimal(int animalId, IReadOnlyList<System.Numerics.Vector3> path, float damage, out Vector3 feet)
        {
            feet = default;
            if (ecosystem == null || !ecosystem.TryGetAnimal(animalId, out Animal animal)) return false;

            AnimalSpecies kind = animalSpecies[animal.SpeciesIndex];
            float half = kind.Width * 0.5f;
            var min = new System.Numerics.Vector3(animal.X + 0.5f - half, animal.Y, animal.Z + 0.5f - half);
            var max = new System.Numerics.Vector3(animal.X + 0.5f + half, animal.Y + kind.Height, animal.Z + 0.5f + half);
            if (ArrowFlight.DistanceToBox(path, min, max) > kind.MoveRange + 1f) return false;

            feet = new Vector3(animal.X + 0.5f, animal.Y, animal.Z + 0.5f);
            ecosystem.Damage(animalId, damage);
            animalRevision++;
            return true;
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

                nearbyCarcasses.Clear();
                foreach (Carcass carcass in ecosystem.Carcasses)
                {
                    float dx = carcass.X + 0.5f - position.x;
                    float dz = carcass.Z + 0.5f - position.z;
                    if (dx * dx + dz * dz <= radius * radius) nearbyCarcasses.Add(carcass);
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
                animalViews.Add(animal.Id, animal.SpeciesIndex, new Vector3(animal.X + 0.5f, animal.Y, animal.Z + 0.5f), worldSync.World, AnimalListSeconds());
            }

            animalViews.EndList();

            animalViews.BeginCarcasses();
            foreach (Carcass carcass in nearbyCarcasses)
            {
                animalViews.AddCarcass(carcass.SpeciesIndex, new Vector3(carcass.X + 0.5f, carcass.Y, carcass.Z + 0.5f));
            }
        }

        private void SendNearbyAnimals(ulong clientId)
        {
            using (var writer = new FastBufferWriter(2 * sizeof(int) + nearbyAnimals.Count * AnimalEntrySize + nearbyCarcasses.Count * CarcassEntrySize, Allocator.Temp))
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

                writer.WriteValueSafe(nearbyCarcasses.Count);
                foreach (Carcass carcass in nearbyCarcasses)
                {
                    writer.WriteValueSafe((byte)carcass.SpeciesIndex);
                    writer.WriteValueSafe(carcass.X);
                    writer.WriteValueSafe(carcass.Y);
                    writer.WriteValueSafe(carcass.Z);
                }

                Messaging.SendNamedMessage(AnimalsMessage, clientId, writer, NetworkDelivery.ReliableFragmentedSequenced);
            }
        }

        private void OnAnimals(ulong senderId, FastBufferReader reader)
        {
            reader.ReadValueSafe(out int count);
            float listSeconds = AnimalListSeconds();

            animalViews.BeginList();
            for (int i = 0; i < count; i++)
            {
                reader.ReadValueSafe(out int animalId);
                reader.ReadValueSafe(out byte speciesIndex);
                reader.ReadValueSafe(out int x);
                reader.ReadValueSafe(out int y);
                reader.ReadValueSafe(out int z);
                animalViews.Add(animalId, speciesIndex, new Vector3(x + 0.5f, y, z + 0.5f), worldSync.World, listSeconds);
            }

            animalViews.EndList();

            reader.ReadValueSafe(out int carcassCount);
            animalViews.BeginCarcasses();
            for (int i = 0; i < carcassCount; i++)
            {
                reader.ReadValueSafe(out byte speciesIndex);
                reader.ReadValueSafe(out int x);
                reader.ReadValueSafe(out int y);
                reader.ReadValueSafe(out int z);
                animalViews.AddCarcass(speciesIndex, new Vector3(x + 0.5f, y, z + 0.5f));
            }
        }

        // About how long until the next animal list: one simulation step at the current speed, but
        // lists come at most every AnimalFeedInterval, so at high speed a list covers several steps.
        // AnimalViews spreads each move over it (so at speed 1 animals do not dash then stand still
        // for most of the step), or quickens it to fit (so at high speed they move on without stopping).
        private float AnimalListSeconds()
        {
            float stepSeconds = SimClock.RealSecondsPerStep / Mathf.Max(multiplier, 0.01f);
            return Mathf.Min(Mathf.Max(stepSeconds, AnimalFeedInterval), 4f * SimClock.RealSecondsPerStep);
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
                    writer.WriteValueSafe(PackStages(plant.Stage, plant.RipeStage, plant.IsStump));
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
            long previousCount = stepCount;
            reader.ReadValueSafe(out stepCount);
            float previousMultiplier = multiplier;
            reader.ReadValueSafe(out multiplier);
            if (stepCount != previousCount || multiplier != previousMultiplier) clockKnownAt = Time.unscaledTime;
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

                SetView(plantId, UnpackView(speciesIndex, x, y, z, stage));
            }

            reader.ReadValueSafe(out int removedCount);
            for (int i = 0; i < removedCount; i++)
            {
                reader.ReadValueSafe(out int plantId);
                RemoveView(plantId);
            }
        }

        // A plant's growth stage (0 to Plant.StageCount - 1), whether it is a stump and its stage of
        // ripeness (0 to Plant.RipeStageCount) travel in one byte: growth in the low three bits, the
        // stump in the next, ripeness in the top two.
        private const int StageBits = 3;
        private const int StageMask = (1 << StageBits) - 1;
        private const int StumpBit = 1 << StageBits;
        private const int RipeShift = 6;

        private static byte PackStages(int stage, int ripeStage, bool stump) => (byte)(stage | (stump ? StumpBit : 0) | (ripeStage << RipeShift));

        private static PlantView UnpackView(int speciesIndex, int x, int y, int z, byte stages)
        {
            return new PlantView
            {
                SpeciesIndex = speciesIndex, X = x, Y = y, Z = z,
                Stage = stages & StageMask, Stump = (stages & StumpBit) != 0, RipeStage = stages >> RipeShift
            };
        }

        private static PlantView ViewOf(Plant plant)
        {
            return new PlantView
            {
                SpeciesIndex = plant.SpeciesIndex, X = plant.X, Y = plant.Y, Z = plant.Z,
                Stage = plant.Stage, RipeStage = plant.RipeStage, Stump = plant.IsStump
            };
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

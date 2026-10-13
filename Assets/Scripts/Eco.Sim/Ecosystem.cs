using System;
using System.Collections.Generic;
using Voxel.Core;

namespace Eco.Sim
{
    // Living state of the world: soil fertility and plants. It only changes through Step, called by
    // the server, and through player actions (Remove). No access to real time or to Unity:
    // two ecosystems started from the same seed and given the same calls stay identical.
    public sealed partial class Ecosystem
    {
        // Fertility is stored on a coarse grid: one cell for CellSize × CellSize block columns.
        public const int CellSize = 4;

        private const float FertilityRegenPerStep = 0.004f;
        private const float StarvationChancePerStep = 0.02f;
        private const float MinCapacity = 0.4f;

        // Width, in fertility cells, of the patches of rich or poor soil.
        private const int CapacityPatchCells = 4;

        private readonly World world;
        private readonly Species[] species;
        private readonly SimRandom random;
        private readonly int cellsX;
        private readonly int cellsZ;

        // Current fertility of each cell, and the maximum it regenerates toward.
        private readonly float[] fertility;
        private readonly float[] capacity;

        // Plants in order of appearance, hence by increasing id: the iteration order is part of the
        // result. An array rather than a list, because Step walks all of it at every step and its
        // cost dominates that of the simulation.
        // A plant is found by binary search on its id: no index table to keep up to date when
        // plants die.
        private Plant[] plants = new Plant[256];
        private int plantCount;

        // For each block column, the id of the plant standing there, or 0.
        private readonly int[] plantAtColumn;
        private readonly List<int> dying = new List<int>();

        // Plants whose ground or base has changed since the last step: only those are rechecked.
        private readonly List<int> disturbed = new List<int>();
        private int nextPlantId = 1;

        public Ecosystem(World world, IReadOnlyList<Species> species, int seed)
            : this(world, species, Array.Empty<AnimalSpecies>(), seed)
        {
        }

        public Ecosystem(World world, IReadOnlyList<Species> species, IReadOnlyList<AnimalSpecies> animalSpecies, int seed)
        {
            this.world = world;
            this.species = new Species[species.Count];
            for (int i = 0; i < species.Count; i++)
            {
                this.species[i] = species[i];
            }

            random = new SimRandom(seed);

            cellsX = (world.SizeX + CellSize - 1) / CellSize;
            cellsZ = (world.SizeZ + CellSize - 1) / CellSize;
            fertility = new float[cellsX * cellsZ];
            capacity = new float[cellsX * cellsZ];
            plantAtColumn = new int[world.SizeX * world.SizeZ];

            InitializeAnimals(animalSpecies);
            FillCapacity();
            world.BlockChanged += OnBlockChanged;
        }

        public event Action<Plant> PlantAdded;

        // Raised when a plant moves to another growth stage, not at every step.
        public event Action<Plant> PlantChanged;
        public event Action<int> PlantRemoved;

        public long StepCount { get; private set; }

        // Snapshot: no longer valid after a Step, a Remove or a TryPlant.
        public IReadOnlyList<Plant> Plants => new ArraySegment<Plant>(plants, 0, plantCount);

        public IReadOnlyList<Species> AllSpecies => species;

        public float GetFertility(int x, int z) => fertility[CellIndex(x, z)];

        public bool TryGet(int plantId, out Plant plant)
        {
            int index = IndexOf(plantId);
            if (index >= 0)
            {
                plant = plants[index];
                return true;
            }

            plant = default;
            return false;
        }

        // Sows adult plants at random to start a new world. Returns the number of plants created.
        public int Populate(int attempts)
        {
            int created = 0;
            for (int i = 0; i < attempts; i++)
            {
                int speciesIndex = i % species.Length;
                int x = random.NextInt(world.SizeX);
                int z = random.NextInt(world.SizeZ);
                int age = random.NextInt(Math.Max(1, species[speciesIndex].LifespanSteps / 2));
                if (TryAdd(speciesIndex, x, z, 1f, age)) created++;
            }

            return created;
        }

        // Sprouts a seed in the given column, if the ground, the room and the fertility allow it.
        public bool TryPlant(int speciesIndex, int x, int z) => TryAdd(speciesIndex, x, z, 0f, 0);

        // Removes a plant, for instance one felled by a player. Returns false if it does not exist.
        public bool Remove(int plantId)
        {
            int index = IndexOf(plantId);
            if (index < 0) return false;

            Plant plant = plants[index];
            Array.Copy(plants, index + 1, plants, index, plantCount - index - 1);
            plantCount--;
            Forget(plant);
            return true;
        }

        // Advances the simulation by one step of simulated time.
        public void Step()
        {
            StepCount++;
            RemoveUprooted();

            for (int i = 0; i < fertility.Length; i++)
            {
                fertility[i] = Math.Min(capacity[i], fertility[i] + FertilityRegenPerStep);
            }

            // Plants born during this step only act from the next one.
            int count = plantCount;
            for (int i = 0; i < count; i++)
            {
                // Working copy: sowing can grow the array, so a reference into it would no longer be safe.
                Plant plant = plants[i];
                bool alive = Live(ref plant, out bool seeds);
                plants[i] = plant;

                if (!alive) dying.Add(i);
                else if (seeds) Seed(plant);
            }

            RemoveDying();
            StepAnimals();
        }

        // Runs one step of a plant's life. Returns false if it dies.
        private bool Live(ref Plant plant, out bool seeds)
        {
            seeds = false;
            Species kind = species[plant.SpeciesIndex];

            plant.Age++;
            if (plant.Age > kind.LifespanSteps) return false;

            int cell = CellIndex(plant.X, plant.Z);
            float soil = fertility[cell];
            if (soil < kind.MinFertility)
            {
                if (random.NextFloat() < StarvationChancePerStep) return false;
            }
            else if (plant.Growth < 1f)
            {
                int previousStage = plant.Stage;
                plant.Growth = Math.Min(1f, plant.Growth + soil / kind.MaturitySteps);
                if (plant.Stage != previousStage) PlantChanged?.Invoke(plant);
            }

            fertility[cell] = Math.Max(0f, soil - kind.FertilityUse * plant.Growth);
            seeds = plant.Growth >= 1f && random.NextFloat() < kind.SeedChance;
            return true;
        }

        private void Seed(Plant parent)
        {
            Species kind = species[parent.SpeciesIndex];
            int reach = 2 * kind.SeedRange + 1;
            int x = parent.X + random.NextInt(reach) - kind.SeedRange;
            int z = parent.Z + random.NextInt(reach) - kind.SeedRange;
            TryAdd(parent.SpeciesIndex, x, z, 0f, 0);
        }

        private bool TryAdd(int speciesIndex, int x, int z, float growth, int age)
        {
            if ((uint)x >= (uint)world.SizeX || (uint)z >= (uint)world.SizeZ) return false;

            Species kind = species[speciesIndex];
            int y = SurfaceAt(x, z);
            if (!HasGroundAndRoom(x, y, z)) return false;
            if (fertility[CellIndex(x, z)] < kind.MinFertility) return false;
            if (!IsSpaceFree(x, z, speciesIndex, kind.Spacing)) return false;

            var plant = new Plant
            {
                Id = nextPlantId++,
                SpeciesIndex = speciesIndex,
                X = x,
                Y = y,
                Z = z,
                Age = age,
                Growth = growth
            };

            Append(plant);
            PlantAdded?.Invoke(plant);
            return true;
        }

        private void Append(Plant plant)
        {
            if (plantCount == plants.Length) Array.Resize(ref plants, plants.Length * 2);

            plants[plantCount++] = plant;
            plantAtColumn[ColumnIndex(plant.X, plant.Z)] = plant.Id;
        }

        // The world only changes through World.SetBlock: rather than reading the ground of every
        // plant again at every step, we note those a block change may have disturbed.
        private void OnBlockChanged(BlockPos pos, ushort previous, ushort current)
        {
            if ((uint)pos.X >= (uint)world.SizeX || (uint)pos.Z >= (uint)world.SizeZ) return;

            int column = ColumnIndex(pos.X, pos.Z);
            surfaceCache[column] = 0;

            int plantId = plantAtColumn[column];
            if (plantId != 0) disturbed.Add(plantId);
        }

        // Removes, at the start of the step, plants that lost their ground or whose base is taken by a block.
        private void RemoveUprooted()
        {
            if (disturbed.Count == 0) return;

            foreach (int plantId in disturbed)
            {
                if (TryGet(plantId, out Plant plant) && !HasGroundAndRoom(plant.X, plant.Y, plant.Z)) Remove(plantId);
            }

            disturbed.Clear();
        }

        // Lays out the maximum fertility in wide smooth patches, rich or poor: values drawn at random
        // on a coarse grid, then interpolated between its points.
        private void FillCapacity()
        {
            int latticeX = cellsX / CapacityPatchCells + 2;
            int latticeZ = cellsZ / CapacityPatchCells + 2;
            var lattice = new float[latticeX * latticeZ];
            for (int i = 0; i < lattice.Length; i++)
            {
                lattice[i] = random.NextFloat();
            }

            for (int cz = 0; cz < cellsZ; cz++)
            {
                for (int cx = 0; cx < cellsX; cx++)
                {
                    int x0 = cx / CapacityPatchCells;
                    int z0 = cz / CapacityPatchCells;
                    float tx = (float)cx / CapacityPatchCells - x0;
                    float tz = (float)cz / CapacityPatchCells - z0;

                    float near = Lerp(lattice[x0 + latticeX * z0], lattice[x0 + 1 + latticeX * z0], tx);
                    float far = Lerp(lattice[x0 + latticeX * (z0 + 1)], lattice[x0 + 1 + latticeX * (z0 + 1)], tx);

                    int cell = cx + cellsX * cz;
                    capacity[cell] = MinCapacity + (1f - MinCapacity) * Lerp(near, far, tz);
                    fertility[cell] = capacity[cell];
                }
            }
        }

        private static float Lerp(float from, float to, float t) => from + (to - from) * t;

        // A plant needs dirt or grass under it, and air at its base.
        private bool HasGroundAndRoom(int x, int y, int z)
        {
            if (y <= 0 || y >= world.SizeY) return false;

            ushort ground = Blocks.TypeOf(world.GetBlock(x, y - 1, z));
            if (ground != Blocks.Grass && ground != Blocks.Dirt) return false;

            return !Blocks.IsSolid(world.GetBlock(x, y, z));
        }

        // A column holds a single plant. Spacing only applies between plants of the same species:
        // a tree can sprout among bushes, but not right next to another tree.
        private bool IsSpaceFree(int x, int z, int speciesIndex, int spacing)
        {
            if (plantAtColumn[ColumnIndex(x, z)] != 0) return false;

            int radius = Math.Max(0, spacing - 1);
            for (int dz = -radius; dz <= radius; dz++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    int nx = x + dx;
                    int nz = z + dz;
                    if ((uint)nx >= (uint)world.SizeX || (uint)nz >= (uint)world.SizeZ) continue;

                    int neighbourId = plantAtColumn[ColumnIndex(nx, nz)];
                    if (neighbourId != 0 && plants[IndexOf(neighbourId)].SpeciesIndex == speciesIndex) return false;
                }
            }

            return true;
        }

        // Removes the plants that died during the step, keeping the survivors in order.
        private void RemoveDying()
        {
            if (dying.Count == 0) return;

            int nextDying = 0;
            int kept = 0;
            for (int i = 0; i < plantCount; i++)
            {
                if (nextDying < dying.Count && dying[nextDying] == i)
                {
                    nextDying++;
                    Forget(plants[i]);
                    continue;
                }

                plants[kept++] = plants[i];
            }

            plantCount = kept;
            dying.Clear();
        }

        private void Forget(Plant plant)
        {
            plantAtColumn[ColumnIndex(plant.X, plant.Z)] = 0;
            PlantRemoved?.Invoke(plant.Id);
        }

        // Index of the plant in the array, sorted by id, or a negative value if it does not exist.
        private int IndexOf(int plantId)
        {
            int low = 0;
            int high = plantCount - 1;
            while (low <= high)
            {
                int middle = (low + high) / 2;
                int id = plants[middle].Id;
                if (id == plantId) return middle;

                if (id < plantId) low = middle + 1;
                else high = middle - 1;
            }

            return -1;
        }

        private int CellIndex(int x, int z) => x / CellSize + cellsX * (z / CellSize);

        private int ColumnIndex(int x, int z) => x + world.SizeX * z;
    }
}

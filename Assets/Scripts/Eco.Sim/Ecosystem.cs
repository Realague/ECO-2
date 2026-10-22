using System;
using System.Collections.Generic;
using Voxel.Core;

namespace Eco.Sim
{
    // Living state of the world: the soil (NPK, Ecosystem.Soil) and plants. It only changes through Step, called by
    // the server, and through player actions (Remove). No access to real time or to Unity:
    // two ecosystems started from the same seed and given the same calls stay identical.
    public sealed partial class Ecosystem
    {
        // The soil is stored on a coarse grid: one cell for CellSize × CellSize block columns.
        public const int CellSize = 4;

        private const float StarvationChancePerStep = 0.02f;

        // In the climate's richness (the nitrogen of the soil), the share of wetness (the rest is
        // mildness), and the warmth soil likes best.
        private const float WetnessShare = 0.7f;
        private const float IdealWarmth = 0.55f;

        // Populate: how many more times a species that needs water beside it tries to take.
        private const int WaterSideAttempts = 4;

        // Species.NeedsShade: a grown plant this high (in blocks) within this many columns.
        private const float ShadeHeight = 3f;
        private const int ShadeRange = 2;

        private readonly World world;
        private readonly Species[] species;
        private readonly SimRandom random;
        private readonly int cellsX;
        private readonly int cellsZ;

        // For each species, how well the climate of each cell suits it (Species.Suitability).
        private readonly float[][] suitability;

        // Plants in order of appearance, hence by increasing id: the iteration order is part of the
        // result. An array rather than a list, because Step walks all of it at every step and its
        // cost dominates that of the simulation.
        // A plant is found by binary search on its id: no index table to keep up to date when
        // plants die.
        private Plant[] plants = new Plant[256];
        private int plantCount;

        // A removed plant (Remove) stays in the array, marked with this age, until the next compaction
        // (RemoveDying in a step, CompactRemoved before the array is read from outside): every loop over
        // the array must skip it. IndexOf does not find it.
        private const int RemovedAge = int.MinValue;
        private int removedCount;

        // A felled tree's stump sprouts again after this long (CutToStump).
        public const int StumpRegrowSteps = 3 * SimClock.StepsPerDay;
        private bool stepping;

        // For each block column, the id of the plant standing there, or 0, and its species plus one
        // (0 for none): neighbourhood tests (spacing, shade, grazing) read the species without
        // looking the plant up. Both are written together, by Append and Forget only.
        private readonly int[] plantAtColumn;
        private readonly ushort[] plantSpeciesAtColumn;

        // Per species, whether it is tall enough to shade (Height >= ShadeHeight).
        private readonly bool[] shadingSpecies;
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
            SavedSpeciesCount = species.Count;

            cellsX = (world.SizeX + CellSize - 1) / CellSize;
            cellsZ = (world.SizeZ + CellSize - 1) / CellSize;
            soil = new float[cellsX * cellsZ * NutrientCount];
            soilCapacity = new float[cellsX * cellsZ * NutrientCount];
            nitrogenPace = new float[cellsX * cellsZ];
            speciesSoil = SpeciesSoil(this.species);
            suitability = new float[species.Count][];
            plantAtColumn = new int[world.SizeX * world.SizeZ];
            plantSpeciesAtColumn = new ushort[world.SizeX * world.SizeZ];
            shadingSpecies = new bool[species.Count];
            for (int i = 0; i < species.Count; i++)
            {
                shadingSpecies[i] = species[i].Height >= ShadeHeight;
            }

            InitializeAnimals(animalSpecies);
            InitializeCarrion();
            FillSoil();
            FillSuitability(world.Geography);
            world.BlockChanged += OnBlockChanged;
        }

        public event Action<Plant> PlantAdded;

        // Raised when a plant moves to another growth or ripeness stage, not at every step.
        public event Action<Plant> PlantChanged;
        public event Action<int> PlantRemoved;

        public long StepCount { get; private set; }

        // Snapshot: no longer valid after a Step, a Remove or a TryPlant.
        public IReadOnlyList<Plant> Plants
        {
            get
            {
                CompactRemoved();
                return new ArraySegment<Plant>(plants, 0, plantCount);
            }
        }

        public IReadOnlyList<Species> AllSpecies => species;

        // How many of the species the loaded save knew (the first ones): fewer than AllSpecies
        // when species were added since. All of them for an ecosystem not loaded.
        public int SavedSpeciesCount { get; private set; }

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

        // Sows adult plants at random to start a new world, the attempts shared among the species.
        // Species that grow by the water try WaterSideAttempts times as often, since their habitat
        // is a small part of the world (otherwise a world may start with a single willow); species
        // that need shade come last, once there are trees to give it. Returns the number of plants created.
        public int Populate(int attempts) => Populate(attempts, 0);

        // The same, for the species from firstSpecies on only: those added to the game since a
        // loaded world was saved (SavedSpeciesCount), which would otherwise never appear in it.
        public int Populate(int attempts, int firstSpecies)
        {
            int created = 0;
            for (int round = 0; round < 2; round++)
            {
                for (int i = 0; i < attempts; i++)
                {
                    int speciesIndex = i % species.Length;
                    Species kind = species[speciesIndex];
                    if (speciesIndex < firstSpecies || kind.NeedsShade != (round == 1)) continue;

                    for (int attempt = kind.NeedsWater ? WaterSideAttempts : 1; attempt > 0; attempt--)
                    {
                        int x = random.NextInt(world.SizeX);
                        int z = random.NextInt(world.SizeZ);
                        int age = random.NextInt(Math.Max(1, kind.LifespanSteps / 2));
                        if (TryAdd(speciesIndex, x, z, 1f, age)) created++;
                    }
                }
            }

            return created;
        }

        // Sprouts a seed in the given column, if the ground, the room and the soil allow it.
        public bool TryPlant(int speciesIndex, int x, int z) => TryAdd(speciesIndex, x, z, 0f, 0);

        // Places a grown plant in the given column, as a player's command asks: only its ground, its
        // room and, for reeds, water beside it count, not the climate, the soil or the spacing,
        // so it stands wherever it physically can. Outside its climate it then makes no seeds.
        public bool TryPlaceGrownPlant(int speciesIndex, int x, int z)
        {
            if ((uint)x >= (uint)world.SizeX || (uint)z >= (uint)world.SizeZ) return false;
            if (plantAtColumn[ColumnIndex(x, z)] != 0) return false;

            Species kind = species[speciesIndex];
            int y = PlantBaseAt(kind, x, z);
            if (!HasGroundAndRoom(kind, x, y, z)) return false;
            if (kind.NeedsWater && !IsBesideWater(x, y, z)) return false;

            var plant = new Plant
            {
                Id = nextPlantId++,
                SpeciesIndex = speciesIndex,
                X = x,
                Y = y,
                Z = z,
                Age = kind.MaturitySteps,
                Growth = 1f,
                Vigour = suitability[speciesIndex][CellIndex(x, z)]
            };

            Append(plant);
            PlantAdded?.Invoke(plant);
            return true;
        }

        // Harvests a crop, as a player picking it: false if the plant does not exist, bears no crop
        // or is not yet ripe (RipeStage 0). An annual is pulled up; a perennial keeps growing and
        // ripens again from nothing. ripeStage is the stage it had reached, which sets the harvest.
        public bool TryHarvest(int plantId, out int ripeStage)
        {
            ripeStage = 0;
            int index = IndexOf(plantId);
            if (index < 0) return false;

            Plant plant = plants[index];
            Species kind = species[plant.SpeciesIndex];
            if (!kind.BearsCrop || plant.IsStump || plant.RipeStage < 1) return false;

            ripeStage = plant.RipeStage;
            if (kind.Harvest == HarvestKind.Annual) return Remove(plantId);

            plant.Fruit = 0f;
            plants[index] = plant;
            PlantChanged?.Invoke(plant);
            return true;
        }

        // Removes a plant, for instance one felled by a player. Returns false if it does not exist.
        public bool Remove(int plantId)
        {
            int index = IndexOf(plantId);
            if (index < 0) return false;

            // Marked rather than taken out at once: shifting the array for every plant grazed to death
            // cost more than the rest of a step. The next compaction drops it (RemoveDying, CompactRemoved).
            Plant plant = plants[index];
            plants[index].Age = RemovedAge;
            removedCount++;
            Forget(plant);
            return true;
        }

        // Fells a tree to a stump, as a player with an axe: it keeps its id, column and size (so it is
        // drawn as the stump of that tree) and sprouts again after StumpRegrowSteps. Returns false if
        // the plant does not exist or is a stump already. Never called during a step.
        public bool CutToStump(int plantId)
        {
            int index = IndexOf(plantId);
            if (index < 0 || plants[index].IsStump) return false;

            plants[index].Age = -StumpRegrowSteps;
            plants[index].Fruit = 0f;
            PlantChanged?.Invoke(plants[index]);
            return true;
        }

        // Advances the simulation by one step of simulated time.
        public void Step()
        {
            stepping = true;
            try
            {
                StepCount++;
                RemoveUprooted();
                RegenerateSoil();

                // Plants born during this step only act from the next one.
                int count = plantCount;
                for (int i = 0; i < count; i++)
                {
                    // Working copy: sowing can grow the array, so a reference into it would no longer be safe.
                    Plant plant = plants[i];
                    if (plant.Age == RemovedAge) continue;

                    bool alive = Live(ref plant, out bool seeds);
                    plants[i] = plant;

                    if (!alive) dying.Add(i);
                    else if (seeds) Seed(plant);
                }

                RemoveDying();
                StepAnimals();
                StepCarrion();
            }
            finally
            {
                stepping = false;
            }
        }

        // Runs one step of a plant's life. Returns false if it dies.
        private bool Live(ref Plant plant, out bool seeds)
        {
            seeds = false;
            Species kind = species[plant.SpeciesIndex];

            plant.Age++;

            // A stump only waits, drawing nothing at random, then sprouts a sapling of its tree, which
            // lives a whole life from there.
            if (plant.Age <= 0)
            {
                if (plant.Age == 0)
                {
                    plant.Growth = 1f / Plant.StageCount;
                    plant.Fruit = 0f;
                    PlantChanged?.Invoke(plant);
                }

                return true;
            }

            if (plant.Age > kind.LifespanSteps) return false;

            int cell = CellIndex(plant.X, plant.Z);
            float available = Available(cell, plant.SpeciesIndex);
            if (available < kind.MinFertility)
            {
                if (random.NextFloat() < StarvationChancePerStep) return false;
            }
            else if (plant.Growth < 1f)
            {
                int previousStage = plant.Stage;
                float growth = plant.Growth + available * plant.Vigour / kind.MaturitySteps;
                plant.Growth = growth < 1f ? growth : 1f;
                if (plant.Stage != previousStage) PlantChanged?.Invoke(plant);
            }
            else if (plant.Fruit < 1f && kind.BearsCrop)
            {
                // An adult crop ripens as it grew: faster on rich soil and in a climate that suits it.
                int previousRipeStage = plant.RipeStage;
                float fruit = plant.Fruit + available * plant.Vigour / kind.FruitSteps;
                plant.Fruit = fruit < 1f ? fruit : 1f;
                if (plant.RipeStage != previousRipeStage) PlantChanged?.Invoke(plant);
            }

            Feed(cell, plant.SpeciesIndex, plant.Growth);
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
            int cell = CellIndex(x, z);
            float vigour = suitability[speciesIndex][cell];
            if (vigour <= 0f) return false;

            // The cheapest tests first (none of them draws at random, so their order changes nothing
            // but the cost): the column, the soil and the spacing before the blocks.
            if (Available(cell, speciesIndex) < kind.MinFertility) return false;
            if (!IsSpaceFree(x, z, speciesIndex, kind.Spacing)) return false;

            int y = PlantBaseAt(kind, x, z);
            if (!HasGroundAndRoom(kind, x, y, z)) return false;
            if (kind.NeedsWater && !IsBesideWater(x, y, z)) return false;
            if (kind.NeedsShade && !IsShaded(x, z)) return false;

            // Where the climate does not quite suit the species, fewer of its seeds take.
            if (vigour < 1f && random.NextFloat() >= vigour) return false;

            var plant = new Plant
            {
                Id = nextPlantId++,
                SpeciesIndex = speciesIndex,
                X = x,
                Y = y,
                Z = z,
                Age = age,
                Growth = growth,
                Vigour = vigour
            };

            Append(plant);
            PlantAdded?.Invoke(plant);
            return true;
        }

        private void Append(Plant plant)
        {
            if (plantCount == plants.Length) Array.Resize(ref plants, plants.Length * 2);

            plants[plantCount++] = plant;
            int column = ColumnIndex(plant.X, plant.Z);
            plantAtColumn[column] = plant.Id;
            plantSpeciesAtColumn[column] = (ushort)(plant.SpeciesIndex + 1);
        }

        // The world only changes through World.SetBlock: rather than reading the ground of every
        // plant again at every step, we note those a block change may have disturbed.
        private void OnBlockChanged(BlockPos pos, ushort previous, ushort current)
        {
            if ((uint)pos.X >= (uint)world.SizeX || (uint)pos.Z >= (uint)world.SizeZ) return;

            int column = ColumnIndex(pos.X, pos.Z);
            columnInfo[column] = 0;

            int plantId = plantAtColumn[column];
            if (plantId != 0) disturbed.Add(plantId);
        }

        // Removes, at the start of the step, plants that lost their ground or whose base is taken by a block.
        private void RemoveUprooted()
        {
            if (disturbed.Count == 0) return;

            foreach (int plantId in disturbed)
            {
                if (TryGet(plantId, out Plant plant) && !HasGroundAndRoom(species[plant.SpeciesIndex], plant.X, plant.Y, plant.Z)) Remove(plantId);
            }

            disturbed.Clear();
        }

        // How well each plant and animal species does in each cell's climate, worked out once: the
        // climate does not change.
        private void FillSuitability(Geography geography)
        {
            var warmth = new float[cellsX * cellsZ];
            var wetness = new float[cellsX * cellsZ];
            for (int cz = 0; cz < cellsZ; cz++)
            {
                for (int cx = 0; cx < cellsX; cx++)
                {
                    int x = Math.Min(world.SizeX - 1, cx * CellSize + CellSize / 2);
                    int z = Math.Min(world.SizeZ - 1, cz * CellSize + CellSize / 2);
                    warmth[cx + cellsX * cz] = (float)geography.TemperatureAt(x, z) / IntNoise.One;
                    wetness[cx + cellsX * cz] = (float)geography.HumidityAt(x, z) / IntNoise.One;
                }
            }

            for (int s = 0; s < species.Length; s++)
            {
                var fit = new float[warmth.Length];
                for (int cell = 0; cell < fit.Length; cell++)
                {
                    fit[cell] = species[s].Suitability(warmth[cell], wetness[cell]);
                }

                suitability[s] = fit;
            }

            FillAnimalSuitability(warmth, wetness);
        }

        // How well a species does in the climate of this column (Species.Suitability).
        public float VigourAt(int speciesIndex, int x, int z) => suitability[speciesIndex][CellIndex(x, z)];

        // How rich a climate makes the soil, from 0 to 1: wetness counts most, and soil is best where
        // it is mild to warm. Temperature and humidity are in fixed point (IntNoise.One is 1).
        public static float ClimateRichness(int temperature, int humidity)
        {
            float warmth = (float)temperature / IntNoise.One;
            float wetness = (float)humidity / IntNoise.One;
            float mildness = Math.Max(0f, 1f - Math.Abs(warmth - IdealWarmth) / IdealWarmth);
            return Math.Min(1f, WetnessShare * wetness + (1f - WetnessShare) * mildness);
        }

        // A plant needs a ground its species grows on (Species.Grounds) under it, and at its base air
        // (not under water) or, for water plants (Species.Rooting), water one block deep with air above.
        private bool HasGroundAndRoom(Species kind, int x, int y, int z)
        {
            if (y <= 0 || y >= world.SizeY) return false;
            if (!kind.GrowsOn(world.GetBlock(x, y - 1, z))) return false;

            ushort at = Blocks.TypeOf(world.GetBlock(x, y, z));
            if (at == Blocks.Air) return kind.Rooting != Rooting.Flooded;
            if (at == Blocks.Water) return kind.Rooting != Rooting.Dry && Blocks.TypeOf(world.GetBlock(x, y + 1, z)) == Blocks.Air;
            return false;
        }

        // Where a plant of this species would stand in the column: above dry ground, or for water
        // plants on the bed of water one block deep; -1 where neither.
        private int PlantBaseAt(Species kind, int x, int z)
        {
            if ((uint)x >= (uint)world.SizeX || (uint)z >= (uint)world.SizeZ) return -1;

            int info = ColumnInfo(ColumnIndex(x, z), x, z);
            int top = ColumnTopOf(info);
            if ((info & WetColumn) == 0) return kind.Rooting == Rooting.Flooded ? -1 : top;

            int bed = ColumnBedOf(info);
            return kind.Rooting != Rooting.Dry && top - bed == 1 ? bed : -1;
        }

        // Water in one of the eight columns around, beside the plant's ground or its base.
        private bool IsBesideWater(int x, int y, int z)
        {
            for (int dz = -1; dz <= 1; dz++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dz == 0) continue;
                    if (world.IsWater(x + dx, y - 1, z + dz) || world.IsWater(x + dx, y, z + dz)) return true;
                }
            }

            return false;
        }

        // In the shade of a grown plant at least ShadeHeight blocks high, within ShadeRange columns.
        private bool IsShaded(int x, int z)
        {
            int minX = Math.Max(0, x - ShadeRange);
            int maxX = Math.Min(world.SizeX - 1, x + ShadeRange);
            int minZ = Math.Max(0, z - ShadeRange);
            int maxZ = Math.Min(world.SizeZ - 1, z + ShadeRange);
            for (int nz = minZ; nz <= maxZ; nz++)
            {
                for (int nx = minX; nx <= maxX; nx++)
                {
                    int column = ColumnIndex(nx, nz);
                    int speciesPlusOne = plantSpeciesAtColumn[column];
                    if (speciesPlusOne == 0 || !shadingSpecies[speciesPlusOne - 1]) continue;

                    Plant shading = plants[IndexOf(plantAtColumn[column])];
                    if (shading.Growth >= 1f && !shading.IsStump) return true;
                }
            }

            return false;
        }

        // A column holds a single plant. Spacing only applies between plants of the same species:
        // a tree can sprout among bushes, but not right next to another tree.
        private bool IsSpaceFree(int x, int z, int speciesIndex, int spacing)
        {
            if (plantAtColumn[ColumnIndex(x, z)] != 0) return false;

            int radius = Math.Max(0, spacing - 1);
            int minX = Math.Max(0, x - radius);
            int maxX = Math.Min(world.SizeX - 1, x + radius);
            int minZ = Math.Max(0, z - radius);
            int maxZ = Math.Min(world.SizeZ - 1, z + radius);
            var same = (ushort)(speciesIndex + 1);
            for (int nz = minZ; nz <= maxZ; nz++)
            {
                int row = nz * world.SizeX;
                for (int nx = minX; nx <= maxX; nx++)
                {
                    if (plantSpeciesAtColumn[row + nx] == same) return false;
                }
            }

            return true;
        }

        // Removes the plants that died during the step, and those removed since the last compaction,
        // keeping the survivors in order.
        private void RemoveDying()
        {
            if (dying.Count == 0 && removedCount == 0) return;

            int nextDying = 0;
            int kept = 0;
            for (int i = 0; i < plantCount; i++)
            {
                if (plants[i].Age == RemovedAge) continue;

                if (nextDying < dying.Count && dying[nextDying] == i)
                {
                    nextDying++;
                    ReturnToSoil(plants[i]);
                    Forget(plants[i]);
                    continue;
                }

                plants[kept++] = plants[i];
            }

            plantCount = kept;
            removedCount = 0;
            dying.Clear();
        }

        // Drops the removed plants from the array, keeping the others in order. Never during a step:
        // the step holds indices into the array (an event handler reading Plants then sees them).
        private void CompactRemoved()
        {
            if (removedCount == 0 || stepping) return;

            int kept = 0;
            for (int i = 0; i < plantCount; i++)
            {
                if (plants[i].Age != RemovedAge) plants[kept++] = plants[i];
            }

            plantCount = kept;
            removedCount = 0;
        }

        private void Forget(Plant plant)
        {
            int column = ColumnIndex(plant.X, plant.Z);
            plantAtColumn[column] = 0;
            plantSpeciesAtColumn[column] = 0;
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
                if (id == plantId) return plants[middle].Age == RemovedAge ? -1 : middle;

                if (id < plantId) low = middle + 1;
                else high = middle - 1;
            }

            return -1;
        }

        private int CellIndex(int x, int z) => x / CellSize + cellsX * (z / CellSize);

        private int ColumnIndex(int x, int z) => x + world.SizeX * z;
    }
}

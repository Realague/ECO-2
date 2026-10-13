using System;
using System.Collections.Generic;
using Voxel.Core;

namespace Eco.Sim
{
    // Animals: at each step they move from one block column to another, look for food when hungry,
    // breed when well fed, and die of hunger or old age.
    // As with plants, everything goes through Step and the ecosystem's random generator.
    public sealed partial class Ecosystem
    {
        private const float SeeksFoodAbove = 0.35f;
        private const float BreedsBelow = 0.3f;
        private const float BirthCost = 0.2f;
        private const float NewbornHunger = 0.25f;

        // Grazing removes this share of growth from the plant, which must have at least that much.
        private const float GrazedGrowth = 0.5f;

        // Height difference an animal can cross in one move.
        private const int MaxClimb = 3;
        private const float ImmigrationChancePerStep = 0.05f;
        private const float ColumnsPerDensityUnit = 10000f;
        private const int InitialPopulationFactor = 3;
        private const int DeadAge = -1;

        private static readonly int[] NeighbourX = { 1, 1, 0, -1, -1, -1, 0, 1 };
        private static readonly int[] NeighbourZ = { 0, 1, 1, 1, 0, -1, -1, -1 };

        private AnimalSpecies[] animalSpecies;

        // Like plants: an array sorted by id, and the id of the animal standing in each column
        // (a column holds a single animal).
        private Animal[] animals = new Animal[64];
        private int animalCount;
        private int[] animalAtColumn;
        private int[] animalPopulation;
        private int[] animalFloor;
        private int nextAnimalId = 1;
        private bool animalsDied;

        // Ground height of each column, plus one; zero until it has been read. Animals look it up at
        // every move: reading it from the world again would cost a whole column each time.
        private int[] surfaceCache;

        // Snapshot: no longer valid after a Step or a RemoveAnimal.
        public IReadOnlyList<Animal> Animals => new ArraySegment<Animal>(animals, 0, animalCount);

        public IReadOnlyList<AnimalSpecies> AllAnimalSpecies => animalSpecies;

        public int CountAnimals(int speciesIndex) => animalPopulation[speciesIndex];

        public bool TryGetAnimal(int animalId, out Animal animal)
        {
            int index = AnimalIndexOf(animalId);
            if (index >= 0)
            {
                animal = animals[index];
                return true;
            }

            animal = default;
            return false;
        }

        // Removes an animal, for instance one killed by a player. Returns false if it does not exist.
        public bool RemoveAnimal(int animalId)
        {
            int index = AnimalIndexOf(animalId);
            if (index < 0) return false;

            Kill(index);
            CompactAnimals();
            return true;
        }

        // Places an adult in the given column, if it is walkable and free.
        public bool TryPlaceAnimal(int speciesIndex, int x, int z)
        {
            if (!CanStandAt(x, z, out int y)) return false;

            AddAnimal(nextAnimalId++, speciesIndex, x, y, z, animalSpecies[speciesIndex].MaturitySteps, NewbornHunger);
            return true;
        }

        // Populates a new world: a few times the minimum population of each species, at random. Returns
        // the number of animals created.
        public int PopulateAnimals()
        {
            int created = 0;
            for (int s = 0; s < animalSpecies.Length; s++)
            {
                for (int i = 0; i < animalFloor[s] * InitialPopulationFactor; i++)
                {
                    if (TryPlaceAnimal(s, random.NextInt(world.SizeX), random.NextInt(world.SizeZ))) created++;
                }
            }

            return created;
        }

        private void InitializeAnimals(IReadOnlyList<AnimalSpecies> kinds)
        {
            animalSpecies = new AnimalSpecies[kinds.Count];
            for (int i = 0; i < kinds.Count; i++)
            {
                animalSpecies[i] = kinds[i];
            }

            int columns = world.SizeX * world.SizeZ;
            animalAtColumn = new int[columns];
            surfaceCache = new int[columns];
            animalPopulation = new int[animalSpecies.Length];
            animalFloor = new int[animalSpecies.Length];
            for (int s = 0; s < animalSpecies.Length; s++)
            {
                // A zero density marks a species that can disappear for good.
                float density = animalSpecies[s].MinDensity;
                animalFloor[s] = density > 0f ? Math.Max(1, (int)Math.Round(density * columns / ColumnsPerDensityUnit)) : 0;
            }
        }

        private void StepAnimals()
        {
            // Animals born during this step only act from the next one.
            int count = animalCount;
            for (int i = 0; i < count; i++)
            {
                // Working copy: a birth can grow the array.
                Animal animal = animals[i];
                if (animal.Age == DeadAge) continue;

                bool alive = LiveAnimal(ref animal);
                animals[i] = animal;
                if (!alive) Kill(i);
            }

            CompactAnimals();
            Immigrate();
        }

        // Runs one step of an animal's life. Returns false if it dies.
        private bool LiveAnimal(ref Animal animal)
        {
            AnimalSpecies kind = animalSpecies[animal.SpeciesIndex];

            animal.Age++;
            if (animal.Age > kind.LifespanSteps) return false;

            animal.Hunger += kind.HungerPerStep;
            if (animal.Hunger >= 1f) return false;

            // The ground may have changed under it since the last step.
            animal.Y = SurfaceAt(animal.X, animal.Z);

            bool busy = animal.Hunger > SeeksFoodAbove && SeekFood(ref animal, kind);
            if (!busy)
            {
                int reach = 2 * kind.MoveRange + 1;
                int x = animal.X + random.NextInt(reach) - kind.MoveRange;
                int z = animal.Z + random.NextInt(reach) - kind.MoveRange;
                TryMoveTo(ref animal, x, z);
            }

            bool breeds = animal.Age >= kind.MaturitySteps && animal.Hunger < BreedsBelow && random.NextFloat() < kind.BirthChance;
            if (breeds && HasRoomToBreed(animal, kind)) GiveBirth(ref animal);

            return true;
        }

        private bool HasRoomToBreed(Animal animal, AnimalSpecies kind)
        {
            if (kind.TerritoryRange <= 0) return true;

            int neighbours = 0;
            for (int dz = -kind.TerritoryRange; dz <= kind.TerritoryRange; dz++)
            {
                for (int dx = -kind.TerritoryRange; dx <= kind.TerritoryRange; dx++)
                {
                    int x = animal.X + dx;
                    int z = animal.Z + dz;
                    if ((uint)x >= (uint)world.SizeX || (uint)z >= (uint)world.SizeZ) continue;

                    int otherId = animalAtColumn[ColumnIndex(x, z)];
                    if (otherId == 0 || otherId == animal.Id) continue;

                    if (animals[AnimalIndexOf(otherId)].SpeciesIndex == animal.SpeciesIndex && ++neighbours >= kind.MaxNeighbours) return false;
                }
            }

            return true;
        }

        // Moves toward the nearest food and eats it if within reach. Returns false if none is in
        // sight or if the way is blocked: the animal then wanders at random.
        private bool SeekFood(ref Animal animal, AnimalSpecies kind)
        {
            if (!FindFood(animal, kind, out int foodX, out int foodZ, out bool isPrey)) return false;

            int stepX = Math.Min(Math.Max(foodX - animal.X, -kind.MoveRange), kind.MoveRange);
            int stepZ = Math.Min(Math.Max(foodZ - animal.Z, -kind.MoveRange), kind.MoveRange);
            int x = animal.X + stepX;
            int z = animal.Z + stepZ;

            // A prey occupies its column: the predator stops just before it.
            if (isPrey && x == foodX && z == foodZ)
            {
                x -= Math.Sign(stepX);
                z -= Math.Sign(stepZ);
            }

            if (!TryMoveTo(ref animal, x, z)) return false;

            if (Math.Max(Math.Abs(foodX - animal.X), Math.Abs(foodZ - animal.Z)) > 1) return true;

            if (isPrey)
            {
                if (random.NextFloat() >= kind.CatchChance) return true;

                Kill(AnimalIndexOf(animalAtColumn[ColumnIndex(foodX, foodZ)]));
            }
            else
            {
                Graze(IndexOf(plantAtColumn[ColumnIndex(foodX, foodZ)]));
            }

            animal.Hunger = Math.Max(0f, animal.Hunger - kind.MealValue);
            return true;
        }

        // Searches outward around the animal for the first column that holds something to eat.
        private bool FindFood(Animal animal, AnimalSpecies kind, out int foodX, out int foodZ, out bool isPrey)
        {
            for (int radius = 0; radius <= kind.SenseRange; radius++)
            {
                for (int dz = -radius; dz <= radius; dz++)
                {
                    for (int dx = -radius; dx <= radius; dx++)
                    {
                        // Only the outline of the square: the inside was covered by the previous radii.
                        if (Math.Max(Math.Abs(dx), Math.Abs(dz)) != radius) continue;

                        int x = animal.X + dx;
                        int z = animal.Z + dz;
                        if ((uint)x >= (uint)world.SizeX || (uint)z >= (uint)world.SizeZ) continue;

                        int column = ColumnIndex(x, z);
                        if (IsEdiblePlant(plantAtColumn[column], kind))
                        {
                            foodX = x;
                            foodZ = z;
                            isPrey = false;
                            return true;
                        }

                        if (IsPrey(animalAtColumn[column], animal.Id, kind))
                        {
                            foodX = x;
                            foodZ = z;
                            isPrey = true;
                            return true;
                        }
                    }
                }
            }

            foodX = 0;
            foodZ = 0;
            isPrey = false;
            return false;
        }

        private bool IsEdiblePlant(int plantId, AnimalSpecies kind)
        {
            if (plantId == 0 || kind.EatsPlants.Length == 0) return false;

            Plant plant = plants[IndexOf(plantId)];
            return plant.Growth >= GrazedGrowth && Array.IndexOf(kind.EatsPlants, plant.SpeciesIndex) >= 0;
        }

        private bool IsPrey(int animalId, int hunterId, AnimalSpecies kind)
        {
            if (animalId == 0 || animalId == hunterId || kind.EatsAnimals.Length == 0) return false;

            Animal other = animals[AnimalIndexOf(animalId)];
            return other.Age != DeadAge && Array.IndexOf(kind.EatsAnimals, other.SpeciesIndex) >= 0;
        }

        // A grazed plant loses foliage but survives: it grows back if given the time.
        private void Graze(int plantIndex)
        {
            Plant plant = plants[plantIndex];
            int previousStage = plant.Stage;
            plant.Growth -= GrazedGrowth;
            plants[plantIndex] = plant;
            if (plant.Stage != previousStage) PlantChanged?.Invoke(plant);
        }

        private bool TryMoveTo(ref Animal animal, int x, int z)
        {
            if (x == animal.X && z == animal.Z) return true;
            if (!CanStandAt(x, z, out int y) || Math.Abs(y - animal.Y) > MaxClimb) return false;

            animalAtColumn[ColumnIndex(animal.X, animal.Z)] = 0;
            animalAtColumn[ColumnIndex(x, z)] = animal.Id;
            animal.X = x;
            animal.Y = y;
            animal.Z = z;
            return true;
        }

        private void GiveBirth(ref Animal parent)
        {
            int first = random.NextInt(NeighbourX.Length);
            for (int i = 0; i < NeighbourX.Length; i++)
            {
                int direction = (first + i) % NeighbourX.Length;
                int x = parent.X + NeighbourX[direction];
                int z = parent.Z + NeighbourZ[direction];
                if (!CanStandAt(x, z, out int y) || Math.Abs(y - parent.Y) > MaxClimb) continue;

                AddAnimal(nextAnimalId++, parent.SpeciesIndex, x, y, z, 0, NewbornHunger);
                parent.Hunger += BirthCost;
                return;
            }
        }

        // While a species is below its minimum population, individuals arrive from the map edges.
        private void Immigrate()
        {
            for (int s = 0; s < animalSpecies.Length; s++)
            {
                if (animalPopulation[s] >= animalFloor[s] || random.NextFloat() >= ImmigrationChancePerStep) continue;

                int side = random.NextInt(4);
                int along = random.NextInt(side < 2 ? world.SizeZ : world.SizeX);
                int x = side == 0 ? 0 : side == 1 ? world.SizeX - 1 : along;
                int z = side == 2 ? 0 : side == 3 ? world.SizeZ - 1 : along;
                TryPlaceAnimal(s, x, z);
            }
        }

        private bool CanStandAt(int x, int z, out int y)
        {
            y = 0;
            if ((uint)x >= (uint)world.SizeX || (uint)z >= (uint)world.SizeZ) return false;
            if (animalAtColumn[ColumnIndex(x, z)] != 0) return false;

            y = SurfaceAt(x, z);
            return y > 0 && y < world.SizeY;
        }

        private int SurfaceAt(int x, int z)
        {
            int column = ColumnIndex(x, z);
            if (surfaceCache[column] == 0) surfaceCache[column] = world.GetSurfaceHeight(x, z) + 1;
            return surfaceCache[column] - 1;
        }

        // Ids must be added in increasing order: this keeps the array sorted.
        private void AddAnimal(int id, int speciesIndex, int x, int y, int z, int age, float hunger)
        {
            if (animalCount == animals.Length) Array.Resize(ref animals, animals.Length * 2);

            var animal = new Animal { Id = id, SpeciesIndex = speciesIndex, X = x, Y = y, Z = z, Age = age, Hunger = hunger };
            animals[animalCount++] = animal;
            animalAtColumn[ColumnIndex(x, z)] = animal.Id;
            animalPopulation[speciesIndex]++;
        }

        // Marks the animal as dead and frees its column. CompactAnimals removes it from the array.
        private void Kill(int index)
        {
            Animal animal = animals[index];
            if (animal.Age == DeadAge) return;

            animalAtColumn[ColumnIndex(animal.X, animal.Z)] = 0;
            animalPopulation[animal.SpeciesIndex]--;
            animals[index].Age = DeadAge;
            animalsDied = true;
        }

        private void CompactAnimals()
        {
            if (!animalsDied) return;

            int kept = 0;
            for (int i = 0; i < animalCount; i++)
            {
                if (animals[i].Age != DeadAge) animals[kept++] = animals[i];
            }

            animalCount = kept;
            animalsDied = false;
        }

        private int AnimalIndexOf(int animalId)
        {
            int low = 0;
            int high = animalCount - 1;
            while (low <= high)
            {
                int middle = (low + high) / 2;
                int id = animals[middle].Id;
                if (id == animalId) return middle;

                if (id < animalId) low = middle + 1;
                else high = middle - 1;
            }

            return -1;
        }
    }
}

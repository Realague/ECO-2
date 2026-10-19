using System;
using System.Collections.Generic;
using Voxel.Core;

namespace Eco.Sim
{
    // Animals: at each step they walk a few block columns (one column at a time, following
    // AnimalMoves, so they never go through blocks), look for food when hungry, breed when well fed,
    // and die of hunger or old age.
    // As with plants, everything goes through Step and the ecosystem's random generator.
    public sealed partial class Ecosystem
    {
        private const float SeeksFoodAbove = 0.35f;
        private const float BreedsBelow = 0.3f;
        private const float BirthCost = 0.2f;
        private const float NewbornHunger = 0.25f;

        // Grazing removes this share of growth from the plant; a plant left with almost none dies (a browsed
        // sapling). A plant is only eaten once it has grown a little.
        private const float GrazedGrowth = 0.5f;
        private const float MinEdibleGrowth = 0.25f;
        private const float MinSurvivingGrowth = 0.1f;

        // Share of the catch chance left against a prey taller than the hunter.
        private const float BigPreyCatchShare = 0.5f;

        // A herd animal closer than this to one of its kind only ambles.
        private const int HerdSpacing = 2;

        // Wandering, an animal weighs this many places and goes to the one whose climate suits it best.
        private const int WanderChoices = 3;

        // Chance, each step, that a bird carrying a seed lets it fall.
        private const float SeedDropChance = 0.25f;

        // How much a shore animal or a wader likes a place away from water, against 1 beside it.
        private const float AwayFromWaterFit = 0.2f;

        // Newcomers and the first animals only appear where the climate suits them at least this well,
        // after at most this many tries.
        private const float SettlingSuitability = 0.5f;
        private const int SettlingTries = 8;

        private const float ImmigrationChancePerStep = 0.05f;
        private const float ColumnsPerDensityUnit = 10000f;
        private const int InitialPopulationFactor = 3;
        private const int DeadAge = -1;

        private AnimalSpecies[] animalSpecies;

        // Like plants: an array sorted by id, and the id of the animal standing in each column
        // (a column holds a single animal).
        private Animal[] animals = new Animal[64];
        private int animalCount;
        private int[] animalAtColumn;
        private int[] animalPopulation;
        private int[] animalFloor;

        // For each animal species, how well the climate of each fertility cell suits it.
        private float[][] animalSuitability;
        private int nextAnimalId = 1;
        private bool animalsDied;

        // Per column (ReadColumn): its top plus one (zero until read), its bed, and whether water lies on it.
        private int[] columnTop;
        private int[] columnBed;
        private bool[] columnWet;

        // Snapshot: no longer valid after a Step or a RemoveAnimal.
        public IReadOnlyList<Animal> Animals => new ArraySegment<Animal>(animals, 0, animalCount);

        public IReadOnlyList<AnimalSpecies> AllAnimalSpecies => animalSpecies;

        public int CountAnimals(int speciesIndex) => animalPopulation[speciesIndex];

        // Whether an animal's body takes up this block: the server refuses to place a block there.
        public bool IsAnimalInside(int x, int y, int z)
        {
            if ((uint)x >= (uint)world.SizeX || (uint)z >= (uint)world.SizeZ) return false;

            int animalId = animalAtColumn[ColumnIndex(x, z)];
            if (animalId == 0) return false;

            Animal animal = animals[AnimalIndexOf(animalId)];
            int height = Math.Max(1, (int)Math.Ceiling(animalSpecies[animal.SpeciesIndex].Height));
            return y >= animal.Y && y < animal.Y + height;
        }

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

        // Kills an animal, for instance one a player struck: it leaves a carcass. Returns false if it does not exist.
        public bool RemoveAnimal(int animalId)
        {
            int index = AnimalIndexOf(animalId);
            if (index < 0) return false;

            Kill(index, 1f);
            CompactAnimals();
            return true;
        }

        // Places an adult in the given column, if it is walkable and free.
        public bool TryPlaceAnimal(int speciesIndex, int x, int z)
        {
            if (!CanStandAt(x, z, animalSpecies[speciesIndex].Habitat, out int y)) return false;

            AddAnimal(nextAnimalId++, speciesIndex, x, y, z, animalSpecies[speciesIndex].MaturitySteps, NewbornHunger);
            return true;
        }

        // Populates a new world: a few times the minimum population of each species, at random where
        // the climate suits it. Returns the number of animals created.
        public int PopulateAnimals()
        {
            int created = 0;
            for (int s = 0; s < animalSpecies.Length; s++)
            {
                for (int i = 0; i < animalFloor[s] * InitialPopulationFactor; i++)
                {
                    if (TrySettle(s)) created++;
                }
            }

            return created;
        }

        // How well the climate of this column suits an animal species.
        public float AnimalVigourAt(int speciesIndex, int x, int z) => animalSuitability[speciesIndex][CellIndex(x, z)];

        private void InitializeAnimals(IReadOnlyList<AnimalSpecies> kinds)
        {
            animalSpecies = new AnimalSpecies[kinds.Count];
            for (int i = 0; i < kinds.Count; i++)
            {
                animalSpecies[i] = kinds[i];
            }

            int columns = world.SizeX * world.SizeZ;
            animalAtColumn = new int[columns];
            columnTop = new int[columns];
            columnBed = new int[columns];
            columnWet = new bool[columns];
            animalPopulation = new int[animalSpecies.Length];
            animalFloor = new int[animalSpecies.Length];
            animalSuitability = new float[animalSpecies.Length][];
        }

        // Once the climate is known (FillSuitability): how well it suits each species everywhere, and
        // each species' minimum population, counted over the land whose climate suits it.
        private void FillAnimalSuitability(float[] warmth, float[] wetness)
        {
            for (int s = 0; s < animalSpecies.Length; s++)
            {
                var fit = new float[warmth.Length];
                int suitedCells = 0;
                for (int cell = 0; cell < fit.Length; cell++)
                {
                    fit[cell] = animalSpecies[s].Suitability(warmth[cell], wetness[cell]);
                    if (fit[cell] >= SettlingSuitability) suitedCells++;
                }

                animalSuitability[s] = fit;

                // A zero density marks a species that can disappear for good.
                float density = animalSpecies[s].MinDensity;
                float columns = (float)suitedCells * CellSize * CellSize;
                animalFloor[s] = density > 0f && suitedCells > 0 ? Math.Max(1, (int)Math.Round(density * columns / ColumnsPerDensityUnit)) : 0;
            }
        }

        // Places an adult at a random column whose climate suits its species, if one is found in a few tries.
        private bool TrySettle(int speciesIndex)
        {
            for (int i = 0; i < SettlingTries; i++)
            {
                int x = random.NextInt(world.SizeX);
                int z = random.NextInt(world.SizeZ);
                bool suits = AnimalVigourAt(speciesIndex, x, z) >= SettlingSuitability && HabitatFit(animalSpecies[speciesIndex].Habitat, x, z) >= 1f;
                if (suits) return TryPlaceAnimal(speciesIndex, x, z);
            }

            return false;
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
                if (!alive) Kill(i, 1f);
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

            // A species that eats nothing the simulation knows (fish feed on what it does not model) never gets hungry.
            if (kind.EatsSomething) animal.Hunger += kind.HungerPerStep;
            if (animal.Hunger >= 1f) return false;

            // The ground may have changed under it since the last step; an animal left where it cannot
            // live (a fish on dry land, a deer under water) dies.
            if (!TryGetAnimalGround(animal.X, animal.Z, kind.Habitat, out animal.Y)) return false;

            bool busy = animal.Hunger > SeeksFoodAbove && SeekFood(ref animal, kind);
            if (!busy && kind.Herd) busy = JoinHerd(ref animal, kind);
            if (!busy) Wander(ref animal, kind, kind.MoveRange);
            DropSeed(ref animal);

            // It breeds less where the climate suits it less.
            float birthChance = kind.BirthChance * AnimalVigourAt(animal.SpeciesIndex, animal.X, animal.Z);
            bool breeds = animal.Age >= kind.MaturitySteps && animal.Hunger < BreedsBelow && random.NextFloat() < birthChance;
            if (breeds && HasRoomToBreed(animal, kind)) GiveBirth(ref animal);

            return true;
        }

        // Walks toward one of a few places drawn at random within range: the one whose climate suits
        // it best, so that animals keep to their climate.
        private void Wander(ref Animal animal, AnimalSpecies kind, int range)
        {
            int reach = 2 * range + 1;
            int bestX = animal.X;
            int bestZ = animal.Z;
            float best = -1f;
            for (int i = 0; i < WanderChoices; i++)
            {
                int x = animal.X + random.NextInt(reach) - range;
                int z = animal.Z + random.NextInt(reach) - range;
                float fit = (uint)x < (uint)world.SizeX && (uint)z < (uint)world.SizeZ
                    ? AnimalVigourAt(animal.SpeciesIndex, x, z) * HabitatFit(kind.Habitat, x, z)
                    : 0f;
                if (fit <= best) continue;

                best = fit;
                bestX = x;
                bestZ = z;
            }

            WalkToward(ref animal, bestX, bestZ, range, false);
        }

        // A bird carrying a seed from a plant it ate lets it fall, sooner or later, where it now
        // stands: it sprouts if the plant could grow there (TryPlant), otherwise it is lost.
        private void DropSeed(ref Animal animal)
        {
            if (animal.CarriedSeed == 0 || random.NextFloat() >= SeedDropChance) return;

            TryPlant(animal.CarriedSeed - 1, animal.X, animal.Z);
            animal.CarriedSeed = 0;
        }

        private bool Flies(Animal animal) => animalSpecies[animal.SpeciesIndex].Habitat == AnimalHabitat.Air;

        // How much an animal of this habitat likes a column, besides its climate: animals of the shore
        // and waders, which live off the water, keep near it (on it, or beside it); others like
        // anywhere they can go.
        private float HabitatFit(AnimalHabitat habitat, int x, int z)
        {
            if (habitat != AnimalHabitat.Shore && habitat != AnimalHabitat.Wader) return 1f;

            return IsWetColumn(x, z) || IsWetColumn(x + 1, z) || IsWetColumn(x - 1, z) || IsWetColumn(x, z + 1) || IsWetColumn(x, z - 1)
                ? 1f
                : AwayFromWaterFit;
        }

        private bool IsWetColumn(int x, int z)
        {
            if ((uint)x >= (uint)world.SizeX || (uint)z >= (uint)world.SizeZ) return false;

            int column = ColumnIndex(x, z);
            ReadColumn(column, x, z);
            return columnWet[column];
        }

        // A herd animal walks to the nearest of its kind in sight, or only ambles if one is close by.
        // Returns false if none is in sight: it then wanders like any other.
        private bool JoinHerd(ref Animal animal, AnimalSpecies kind)
        {
            if (!FindKin(animal, kind, out int kinX, out int kinZ)) return false;

            if (AnimalMoves.Steps(animal.X, animal.Z, kinX, kinZ) <= HerdSpacing) Wander(ref animal, kind, 1);
            else WalkToward(ref animal, kinX, kinZ, kind.MoveRange, true);
            return true;
        }

        // Searches outward around the animal for the nearest other animal of its species.
        private bool FindKin(Animal animal, AnimalSpecies kind, out int kinX, out int kinZ)
        {
            for (int radius = 1; radius <= kind.SenseRange; radius++)
            {
                for (int dz = -radius; dz <= radius; dz++)
                {
                    for (int dx = -radius; dx <= radius; dx++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dz)) != radius) continue;

                        int x = animal.X + dx;
                        int z = animal.Z + dz;
                        if ((uint)x >= (uint)world.SizeX || (uint)z >= (uint)world.SizeZ) continue;

                        int otherId = animalAtColumn[ColumnIndex(x, z)];
                        if (otherId == 0) continue;

                        Animal other = animals[AnimalIndexOf(otherId)];
                        if (other.SpeciesIndex != animal.SpeciesIndex || other.Age == DeadAge) continue;

                        kinX = x;
                        kinZ = z;
                        return true;
                    }
                }
            }

            kinX = 0;
            kinZ = 0;
            return false;
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

        // Walks toward the nearest food and eats it once next to it. Returns false if none is in
        // sight or if the way is blocked: the animal then wanders at random.
        private bool SeekFood(ref Animal animal, AnimalSpecies kind)
        {
            if (!FindFood(animal, kind, out int foodX, out int foodZ, out Food food)) return false;

            // It stops next to the food: a prey occupies its column, and a plant or a carcass is eaten from beside it.
            bool walked = WalkToward(ref animal, foodX, foodZ, kind.MoveRange, true);
            if (AnimalMoves.Steps(animal.X, animal.Z, foodX, foodZ) > 1) return walked;

            if (food == Food.Prey)
            {
                // A prey taller than its hunter is twice as hard to bring down: wolves that live on
                // hares do not wipe out the deer. What the hunter does not eat is left as a carcass.
                int preyIndex = AnimalIndexOf(animalAtColumn[ColumnIndex(foodX, foodZ)]);
                float catchChance = kind.CatchChance;
                if (animalSpecies[animals[preyIndex].SpeciesIndex].Height > kind.Height) catchChance *= BigPreyCatchShare;
                if (random.NextFloat() >= catchChance) return true;

                Kill(preyIndex, RemainsMeat);
            }
            else if (food == Food.Carrion)
            {
                EatCarrion(foodX, foodZ);
            }
            else
            {
                int plantIndex = IndexOf(plantAtColumn[ColumnIndex(foodX, foodZ)]);
                if (kind.SpreadsSeeds) animal.CarriedSeed = plants[plantIndex].SpeciesIndex + 1;
                Eat(plantIndex);
            }

            animal.Hunger = Math.Max(0f, animal.Hunger - kind.MealValue);
            return true;
        }

        // Searches outward around the animal for the first column that holds something to eat.
        private enum Food
        {
            Plant,
            Prey,
            Carrion
        }

        private bool FindFood(Animal animal, AnimalSpecies kind, out int foodX, out int foodZ, out Food food)
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
                        Food? found = IsEdiblePlant(plantAtColumn[column], kind) ? Food.Plant
                            : IsPrey(animalAtColumn[column], animal.Id, kind) ? Food.Prey
                            : IsCarrion(x, z, kind) ? Food.Carrion
                            : (Food?)null;
                        if (found == null) continue;

                        foodX = x;
                        foodZ = z;
                        food = found.Value;
                        return true;
                    }
                }
            }

            foodX = 0;
            foodZ = 0;
            food = Food.Plant;
            return false;
        }

        private bool IsEdiblePlant(int plantId, AnimalSpecies kind)
        {
            if (plantId == 0 || kind.EatsPlants.Length == 0) return false;

            Plant plant = plants[IndexOf(plantId)];
            if (plant.Growth < MinEdibleGrowth || Array.IndexOf(kind.EatsPlants, plant.SpeciesIndex) < 0) return false;

            // Ripe fruit is within reach of whoever eats it, however tall the tree: birds, deer under apple trees.
            if (plant.RipeStage >= 1 && species[plant.SpeciesIndex].Harvest == HarvestKind.Perennial) return true;

            return species[plant.SpeciesIndex].Height * PlantShape.Scale(plant.Stage) <= kind.ReachHeight;
        }

        // A plant is eaten: a perennial's ripe fruit are taken and the plant is spared; otherwise it is
        // grazed, and an annual's crop is lost with its foliage.
        private void Eat(int plantIndex)
        {
            Plant plant = plants[plantIndex];
            Species kind = species[plant.SpeciesIndex];
            if (plant.RipeStage >= 1 && kind.Harvest == HarvestKind.Perennial)
            {
                plant.Fruit = 0f;
                plants[plantIndex] = plant;
                PlantChanged?.Invoke(plant);
                return;
            }

            if (kind.BearsCrop && plant.Fruit > 0f)
            {
                int previousRipeStage = plant.RipeStage;
                plant.Fruit = 0f;
                plants[plantIndex] = plant;
                if (previousRipeStage != 0) PlantChanged?.Invoke(plant);
            }

            Graze(plantIndex);
        }

        private bool IsPrey(int animalId, int hunterId, AnimalSpecies kind)
        {
            if (animalId == 0 || animalId == hunterId || kind.EatsAnimals.Length == 0) return false;

            Animal other = animals[AnimalIndexOf(animalId)];
            return other.Age != DeadAge && Array.IndexOf(kind.EatsAnimals, other.SpeciesIndex) >= 0;
        }

        // A grazed plant loses foliage: it grows back if given the time, unless it was too young to
        // spare it (a browsed sapling dies).
        private void Graze(int plantIndex)
        {
            Plant plant = plants[plantIndex];
            if (plant.Growth - GrazedGrowth < MinSurvivingGrowth)
            {
                Remove(plant.Id);
                return;
            }

            int previousStage = plant.Stage;
            plant.Growth -= GrazedGrowth;
            plants[plantIndex] = plant;
            if (plant.Stage != previousStage) PlantChanged?.Invoke(plant);
        }

        // Walks at most maxSteps columns toward the target, one column at a time, stopping on it or,
        // if asked, next to it. Returns false if the animal could not move at all.
        private bool WalkToward(ref Animal animal, int targetX, int targetZ, int maxSteps, bool stopNextToIt)
        {
            var ground = new SimGround(this, animalSpecies[animal.SpeciesIndex].Habitat);
            bool moved = false;
            for (int step = 0; step < maxSteps; step++)
            {
                int distance = AnimalMoves.Steps(animal.X, animal.Z, targetX, targetZ);
                if (distance == 0 || (stopNextToIt && distance <= 1)) break;
                if (!AnimalMoves.TryNextStep(ground, animal.X, animal.Y, animal.Z, targetX, targetZ, out int x, out int y, out int z, Flies(animal))) break;

                animalAtColumn[ColumnIndex(animal.X, animal.Z)] = 0;
                animalAtColumn[ColumnIndex(x, z)] = animal.Id;
                animal.X = x;
                animal.Y = y;
                animal.Z = z;
                moved = true;
            }

            return moved;
        }

        // The young appears in a neighbouring column the parent could step onto.
        private void GiveBirth(ref Animal parent)
        {
            var ground = new SimGround(this, animalSpecies[parent.SpeciesIndex].Habitat);
            int first = random.NextInt(AnimalMoves.DirectionCount);
            for (int i = 0; i < AnimalMoves.DirectionCount; i++)
            {
                int direction = (first + i) % AnimalMoves.DirectionCount;
                int dx = AnimalMoves.OffsetX(direction);
                int dz = AnimalMoves.OffsetZ(direction);
                if (!AnimalMoves.CanEnter(ground, parent.X, parent.Y, parent.Z, dx, dz, out int y, Flies(parent))) continue;

                AddAnimal(nextAnimalId++, parent.SpeciesIndex, parent.X + dx, y, parent.Z + dz, 0, NewbornHunger);
                if (animalSpecies[parent.SpeciesIndex].EatsSomething) parent.Hunger += BirthCost;
                return;
            }
        }

        // While a species is below its minimum population, newcomers arrive where the climate suits it.
        private void Immigrate()
        {
            for (int s = 0; s < animalSpecies.Length; s++)
            {
                if (animalPopulation[s] >= animalFloor[s] || random.NextFloat() >= ImmigrationChancePerStep) continue;

                TrySettle(s);
            }
        }

        private bool CanStandAt(int x, int z, AnimalHabitat habitat, out int y)
        {
            return TryGetAnimalGround(x, z, habitat, out y) && animalAtColumn[ColumnIndex(x, z)] == 0;
        }

        // Where an animal of this habitat stands in the column (AnimalMoves.GroundFor), if the column
        // is inside the world and not built up to the top.
        private bool TryGetAnimalGround(int x, int z, AnimalHabitat habitat, out int y)
        {
            y = 0;
            if ((uint)x >= (uint)world.SizeX || (uint)z >= (uint)world.SizeZ) return false;

            int column = ColumnIndex(x, z);
            ReadColumn(column, x, z);
            return AnimalMoves.GroundFor(habitat, columnTop[column] - 1, columnWet[column], columnBed[column], out y) && y < world.SizeY;
        }

        // The ground as AnimalMoves sees it during the simulation, for one habitat: a column with an
        // animal in it is taken.
        private readonly struct SimGround : IAnimalGround
        {
            private readonly Ecosystem ecosystem;
            private readonly AnimalHabitat habitat;

            public SimGround(Ecosystem ecosystem, AnimalHabitat habitat)
            {
                this.ecosystem = ecosystem;
                this.habitat = habitat;
            }

            public bool TryGetGround(int x, int z, out int y) => ecosystem.TryGetAnimalGround(x, z, habitat, out y);

            public bool IsFree(int x, int z) => ecosystem.animalAtColumn[ecosystem.ColumnIndex(x, z)] == 0;
        }

        // The air block above the column's ground, or -1 if the column is under water: where land
        // animals, plants and carcasses stand.
        private int SurfaceAt(int x, int z)
        {
            return TryGetAnimalGround(x, z, AnimalHabitat.Land, out int y) ? y : -1;
        }

        // Animals look a column up at every move: reading it from the world again would cost a whole
        // column each time, so its top, bed and whether water lies on it are kept until a block changes.
        private void ReadColumn(int column, int x, int z)
        {
            if (columnTop[column] != 0) return;

            int top = world.GetTopHeight(x, z);
            bool wet = top > 0 && world.IsWater(x, top - 1, z);
            columnTop[column] = top + 1;
            columnWet[column] = wet;
            columnBed[column] = wet ? world.GetSurfaceHeight(x, z) : top;
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

        // Marks the animal as dead, frees its column and leaves a carcass with this much meat (0: none).
        // CompactAnimals removes it from the array.
        private void Kill(int index, float carcassMeat)
        {
            Animal animal = animals[index];
            if (animal.Age == DeadAge) return;

            LeaveCarcass(animal, carcassMeat);
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

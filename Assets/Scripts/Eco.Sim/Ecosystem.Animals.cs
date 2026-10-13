using System;
using System.Collections.Generic;
using Voxel.Core;

namespace Eco.Sim
{
    // Les animaux : ils se déplacent d'une colonne de blocs à une autre à chaque pas, cherchent à manger
    // quand ils ont faim, se reproduisent quand ils sont bien nourris, et meurent de faim ou de vieillesse.
    // Comme pour les plantes, tout passe par Step et par le générateur aléatoire de l'écosystème.
    public sealed partial class Ecosystem
    {
        private const float SeeksFoodAbove = 0.35f;
        private const float BreedsBelow = 0.3f;
        private const float BirthCost = 0.2f;
        private const float NewbornHunger = 0.25f;

        // Brouter retire cette part de croissance à la plante, qui doit en avoir au moins autant.
        private const float GrazedGrowth = 0.5f;

        // Dénivelé qu'un animal franchit en un déplacement.
        private const int MaxClimb = 3;
        private const float ImmigrationChancePerStep = 0.05f;
        private const float ColumnsPerDensityUnit = 10000f;
        private const int InitialPopulationFactor = 3;
        private const int DeadAge = -1;

        private static readonly int[] NeighbourX = { 1, 1, 0, -1, -1, -1, 0, 1 };
        private static readonly int[] NeighbourZ = { 0, 1, 1, 1, 0, -1, -1, -1 };

        private AnimalSpecies[] animalSpecies;

        // Comme les plantes : un tableau trié par identifiant, et l'identifiant de l'animal présent dans
        // chaque colonne (une colonne ne porte qu'un animal).
        private Animal[] animals = new Animal[64];
        private int animalCount;
        private int[] animalAtColumn;
        private int[] animalPopulation;
        private int[] animalFloor;
        private int nextAnimalId = 1;
        private bool animalsDied;

        // Hauteur du sol de chaque colonne, plus un ; zéro tant qu'elle n'a pas été lue. Les animaux la
        // consultent à chaque déplacement : la relire dans le monde coûterait une colonne entière à chaque fois.
        private int[] surfaceCache;

        // Vue de l'instant : elle n'est plus valable après un Step ou un RemoveAnimal.
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

        // Retire un animal, par exemple tué par un joueur. Renvoie faux s'il n'existe pas.
        public bool RemoveAnimal(int animalId)
        {
            int index = AnimalIndexOf(animalId);
            if (index < 0) return false;

            Kill(index);
            CompactAnimals();
            return true;
        }

        // Place un adulte dans la colonne donnée, si elle est praticable et libre.
        public bool TryPlaceAnimal(int speciesIndex, int x, int z)
        {
            if (!CanStandAt(x, z, out int y)) return false;

            AddAnimal(nextAnimalId++, speciesIndex, x, y, z, animalSpecies[speciesIndex].MaturitySteps, NewbornHunger);
            return true;
        }

        // Peuple un monde neuf : quelques fois l'effectif minimal de chaque espèce, au hasard. Renvoie
        // le nombre d'animaux créés.
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
                // Une densité nulle désigne une espèce qui peut disparaître pour de bon.
                float density = animalSpecies[s].MinDensity;
                animalFloor[s] = density > 0f ? Math.Max(1, (int)Math.Round(density * columns / ColumnsPerDensityUnit)) : 0;
            }
        }

        private void StepAnimals()
        {
            // Les animaux nés pendant ce pas n'agissent qu'au suivant.
            int count = animalCount;
            for (int i = 0; i < count; i++)
            {
                // Copie de travail : une naissance peut agrandir le tableau.
                Animal animal = animals[i];
                if (animal.Age == DeadAge) continue;

                bool alive = LiveAnimal(ref animal);
                animals[i] = animal;
                if (!alive) Kill(i);
            }

            CompactAnimals();
            Immigrate();
        }

        // Fait vivre un animal pendant un pas. Renvoie faux s'il meurt.
        private bool LiveAnimal(ref Animal animal)
        {
            AnimalSpecies kind = animalSpecies[animal.SpeciesIndex];

            animal.Age++;
            if (animal.Age > kind.LifespanSteps) return false;

            animal.Hunger += kind.HungerPerStep;
            if (animal.Hunger >= 1f) return false;

            // Le sol a pu changer sous lui depuis le dernier pas.
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

        // Va vers la nourriture la plus proche et la mange si elle est à portée. Renvoie faux s'il n'y en
        // a pas en vue ou si le chemin est barré : l'animal erre alors au hasard.
        private bool SeekFood(ref Animal animal, AnimalSpecies kind)
        {
            if (!FindFood(animal, kind, out int foodX, out int foodZ, out bool isPrey)) return false;

            int stepX = Math.Min(Math.Max(foodX - animal.X, -kind.MoveRange), kind.MoveRange);
            int stepZ = Math.Min(Math.Max(foodZ - animal.Z, -kind.MoveRange), kind.MoveRange);
            int x = animal.X + stepX;
            int z = animal.Z + stepZ;

            // Une proie occupe sa colonne : le prédateur s'arrête juste avant.
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

        // Cherche, de proche en proche autour de l'animal, la première colonne qui porte de quoi manger.
        private bool FindFood(Animal animal, AnimalSpecies kind, out int foodX, out int foodZ, out bool isPrey)
        {
            for (int radius = 0; radius <= kind.SenseRange; radius++)
            {
                for (int dz = -radius; dz <= radius; dz++)
                {
                    for (int dx = -radius; dx <= radius; dx++)
                    {
                        // Seulement le pourtour du carré : l'intérieur a été vu aux rayons précédents.
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

        // Une plante broutée perd du feuillage mais survit : elle repousse si on lui en laisse le temps.
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

        // Tant qu'une espèce est sous son effectif minimal, des individus arrivent par les bords de la carte.
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

        // Les identifiants doivent être ajoutés par ordre croissant : le tableau reste ainsi trié.
        private void AddAnimal(int id, int speciesIndex, int x, int y, int z, int age, float hunger)
        {
            if (animalCount == animals.Length) Array.Resize(ref animals, animals.Length * 2);

            var animal = new Animal { Id = id, SpeciesIndex = speciesIndex, X = x, Y = y, Z = z, Age = age, Hunger = hunger };
            animals[animalCount++] = animal;
            animalAtColumn[ColumnIndex(x, z)] = animal.Id;
            animalPopulation[speciesIndex]++;
        }

        // Marque l'animal comme mort et libère sa colonne. Il est retiré du tableau par CompactAnimals.
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

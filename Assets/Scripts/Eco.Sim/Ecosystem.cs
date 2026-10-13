using System;
using System.Collections.Generic;
using Voxel.Core;

namespace Eco.Sim
{
    // État vivant du monde : la fertilité du sol et les plantes. Il ne change que par Step, appelé par
    // le serveur, et par les actions des joueurs (Remove). Aucun accès à l'heure réelle ni à Unity :
    // deux écosystèmes partis de la même graine et soumis aux mêmes appels restent identiques.
    public sealed partial class Ecosystem
    {
        // La fertilité est stockée sur une grille grossière : une case pour CellSize × CellSize colonnes de blocs.
        public const int CellSize = 4;

        private const float FertilityRegenPerStep = 0.004f;
        private const float StarvationChancePerStep = 0.02f;
        private const float MinCapacity = 0.4f;

        // Largeur, en cases de fertilité, des taches de sol riche ou pauvre.
        private const int CapacityPatchCells = 4;

        private readonly World world;
        private readonly IReadOnlyList<Species> species;
        private readonly SimRandom random;
        private readonly int cellsX;
        private readonly int cellsZ;

        // Fertilité actuelle de chaque case, et le maximum vers lequel elle se régénère.
        private readonly float[] fertility;
        private readonly float[] capacity;

        // Plantes dans l'ordre de leur apparition : l'ordre de parcours fait partie du résultat.
        private readonly List<Plant> plants = new List<Plant>();
        private readonly Dictionary<int, int> indexById = new Dictionary<int, int>();

        // Pour chaque colonne de blocs, l'identifiant de la plante qui s'y trouve, ou 0.
        private readonly int[] plantAtColumn;
        private readonly List<int> dying = new List<int>();
        private int nextPlantId = 1;

        public Ecosystem(World world, IReadOnlyList<Species> species, int seed)
        {
            this.world = world;
            this.species = species;
            random = new SimRandom(seed);

            cellsX = (world.SizeX + CellSize - 1) / CellSize;
            cellsZ = (world.SizeZ + CellSize - 1) / CellSize;
            fertility = new float[cellsX * cellsZ];
            capacity = new float[cellsX * cellsZ];
            plantAtColumn = new int[world.SizeX * world.SizeZ];

            FillCapacity();
        }

        public event Action<Plant> PlantAdded;

        // Levé quand une plante change de palier de croissance, pas à chaque pas.
        public event Action<Plant> PlantChanged;
        public event Action<int> PlantRemoved;

        public long StepCount { get; private set; }

        public IReadOnlyList<Plant> Plants => plants;

        public IReadOnlyList<Species> AllSpecies => species;

        public float GetFertility(int x, int z) => fertility[CellIndex(x, z)];

        public bool TryGet(int plantId, out Plant plant)
        {
            if (indexById.TryGetValue(plantId, out int index))
            {
                plant = plants[index];
                return true;
            }

            plant = default;
            return false;
        }

        // Sème des plantes adultes au hasard pour démarrer un monde neuf. Renvoie le nombre de plantes créées.
        public int Populate(int attempts)
        {
            int created = 0;
            for (int i = 0; i < attempts; i++)
            {
                int speciesIndex = i % species.Count;
                int x = random.NextInt(world.SizeX);
                int z = random.NextInt(world.SizeZ);
                int age = random.NextInt(Math.Max(1, species[speciesIndex].LifespanSteps / 2));
                if (TryAdd(speciesIndex, x, z, 1f, age)) created++;
            }

            return created;
        }

        // Fait germer une graine dans la colonne donnée, si le sol, la place et la fertilité le permettent.
        public bool TryPlant(int speciesIndex, int x, int z) => TryAdd(speciesIndex, x, z, 0f, 0);

        // Retire une plante, par exemple abattue par un joueur. Renvoie faux si elle n'existe pas.
        public bool Remove(int plantId)
        {
            if (!indexById.TryGetValue(plantId, out int index)) return false;

            Plant plant = plants[index];
            plants.RemoveAt(index);
            Forget(plant);
            for (int i = index; i < plants.Count; i++)
            {
                indexById[plants[i].Id] = i;
            }

            return true;
        }

        // Avance la simulation d'un pas de temps simulé.
        public void Step()
        {
            StepCount++;

            for (int i = 0; i < fertility.Length; i++)
            {
                fertility[i] = Math.Min(capacity[i], fertility[i] + FertilityRegenPerStep);
            }

            // Les plantes nées pendant ce pas n'agissent qu'au suivant.
            int count = plants.Count;
            for (int i = 0; i < count; i++)
            {
                Plant plant = plants[i];
                if (!Live(ref plant))
                {
                    dying.Add(i);
                    continue;
                }

                plants[i] = plant;
            }

            RemoveDying();
        }

        // Fait vivre une plante pendant un pas. Renvoie faux si elle meurt.
        private bool Live(ref Plant plant)
        {
            Species kind = species[plant.SpeciesIndex];
            if (!HasGroundAndRoom(plant.X, plant.Y, plant.Z)) return false;

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

            if (plant.Growth >= 1f && random.NextFloat() < kind.SeedChance)
            {
                int reach = 2 * kind.SeedRange + 1;
                int x = plant.X + random.NextInt(reach) - kind.SeedRange;
                int z = plant.Z + random.NextInt(reach) - kind.SeedRange;
                TryAdd(plant.SpeciesIndex, x, z, 0f, 0);
            }

            return true;
        }

        private bool TryAdd(int speciesIndex, int x, int z, float growth, int age)
        {
            if ((uint)x >= (uint)world.SizeX || (uint)z >= (uint)world.SizeZ) return false;

            Species kind = species[speciesIndex];
            int y = world.GetSurfaceHeight(x, z);
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

            indexById[plant.Id] = plants.Count;
            plants.Add(plant);
            plantAtColumn[ColumnIndex(x, z)] = plant.Id;
            PlantAdded?.Invoke(plant);
            return true;
        }

        // Répartit la fertilité maximale en larges taches douces, riches ou pauvres : des valeurs tirées
        // au hasard sur une grille grossière, puis interpolées entre ses points.
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

        // Une plante a besoin de terre ou d'herbe sous elle, et d'air à son pied.
        private bool HasGroundAndRoom(int x, int y, int z)
        {
            if (y <= 0 || y >= world.SizeY) return false;

            ushort ground = Blocks.TypeOf(world.GetBlock(x, y - 1, z));
            if (ground != Blocks.Grass && ground != Blocks.Dirt) return false;

            return !Blocks.IsSolid(world.GetBlock(x, y, z));
        }

        // Une colonne ne porte qu'une plante. L'espacement, lui, ne joue qu'entre plantes de la même
        // espèce : un arbre peut germer au milieu des buissons, pas collé à un autre arbre.
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
                    if (neighbourId != 0 && plants[indexById[neighbourId]].SpeciesIndex == speciesIndex) return false;
                }
            }

            return true;
        }

        // Retire les plantes mortes pendant le pas, en conservant l'ordre des survivantes.
        private void RemoveDying()
        {
            if (dying.Count == 0) return;

            int nextDying = 0;
            int kept = 0;
            for (int i = 0; i < plants.Count; i++)
            {
                if (nextDying < dying.Count && dying[nextDying] == i)
                {
                    nextDying++;
                    Forget(plants[i]);
                    continue;
                }

                Plant survivor = plants[i];
                plants[kept] = survivor;
                indexById[survivor.Id] = kept;
                kept++;
            }

            plants.RemoveRange(kept, plants.Count - kept);
            dying.Clear();
        }

        private void Forget(Plant plant)
        {
            indexById.Remove(plant.Id);
            plantAtColumn[ColumnIndex(plant.X, plant.Z)] = 0;
            PlantRemoved?.Invoke(plant.Id);
        }

        private int CellIndex(int x, int z) => x / CellSize + cellsX * (z / CellSize);

        private int ColumnIndex(int x, int z) => x + world.SizeX * z;
    }
}

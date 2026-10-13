using System.Collections.Generic;
using System.IO;
using System.Text;
using Voxel.Core;

namespace Eco.Sim
{
    // Sauvegarde de l'écosystème : tout ce qu'il faut pour que la simulation reprenne exactement où
    // elle s'était arrêtée, y compris l'état du générateur aléatoire.
    public sealed partial class Ecosystem
    {
        private const uint Magic = 0x454F4345; // « ECOE »
        private const int FormatVersion = 2;

        public void Write(Stream stream)
        {
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                writer.Write(Magic);
                writer.Write(FormatVersion);

                // Les plantes désignent leur espèce par son rang : la liste doit être la même au chargement.
                writer.Write(species.Length);
                foreach (Species kind in species)
                {
                    writer.Write(kind.Name);
                }

                // Les plantes sont posées sur un terrain précis : celui de ce monde, de cette graine et de
                // cette version du générateur.
                writer.Write(world.SizeX);
                writer.Write(world.SizeZ);
                writer.Write(world.Seed);
                writer.Write(world.GeneratorVersion);
                writer.Write(StepCount);
                writer.Write(random.State);
                writer.Write(nextPlantId);

                for (int i = 0; i < fertility.Length; i++)
                {
                    writer.Write(fertility[i]);
                    writer.Write(capacity[i]);
                }

                writer.Write(plantCount);
                for (int i = 0; i < plantCount; i++)
                {
                    Plant plant = plants[i];
                    writer.Write(plant.Id);
                    writer.Write(plant.SpeciesIndex);
                    writer.Write(plant.X);
                    writer.Write(plant.Y);
                    writer.Write(plant.Z);
                    writer.Write(plant.Age);
                    writer.Write(plant.Growth);
                }
            }
        }

        // Lève InvalidDataException si le fichier ne correspond pas à ce monde ou à ces espèces, et
        // EndOfStreamException s'il est tronqué.
        public static Ecosystem Read(Stream stream, World world, IReadOnlyList<Species> species)
        {
            using (var reader = new BinaryReader(stream, Encoding.UTF8, true))
            {
                if (reader.ReadUInt32() != Magic)
                {
                    throw new InvalidDataException("Ce fichier n'est pas une sauvegarde d'écosystème.");
                }

                int formatVersion = reader.ReadInt32();
                if (formatVersion != FormatVersion)
                {
                    throw new InvalidDataException(
                        $"Format de sauvegarde d'écosystème {formatVersion} non pris en charge (attendu : {FormatVersion}).");
                }

                int speciesCount = reader.ReadInt32();
                if (speciesCount != species.Count)
                {
                    throw new InvalidDataException("La liste des espèces a changé depuis cette sauvegarde.");
                }

                for (int i = 0; i < speciesCount; i++)
                {
                    string name = reader.ReadString();
                    if (name != species[i].Name)
                    {
                        throw new InvalidDataException(
                            $"La liste des espèces a changé depuis cette sauvegarde : « {name} » au lieu de « {species[i].Name} ».");
                    }
                }

                if (reader.ReadInt32() != world.SizeX || reader.ReadInt32() != world.SizeZ)
                {
                    throw new InvalidDataException("La sauvegarde d'écosystème ne correspond pas aux dimensions du monde.");
                }

                if (reader.ReadInt32() != world.Seed || reader.ReadInt32() != world.GeneratorVersion)
                {
                    throw new InvalidDataException("La sauvegarde d'écosystème a été faite sur un autre terrain que celui de ce monde.");
                }

                var ecosystem = new Ecosystem(world, species, 0);
                ecosystem.StepCount = reader.ReadInt64();
                ecosystem.random.State = reader.ReadUInt64();
                ecosystem.nextPlantId = reader.ReadInt32();

                for (int i = 0; i < ecosystem.fertility.Length; i++)
                {
                    ecosystem.fertility[i] = reader.ReadSingle();
                    ecosystem.capacity[i] = reader.ReadSingle();
                }

                int savedCount = reader.ReadInt32();
                int previousId = 0;
                for (int i = 0; i < savedCount; i++)
                {
                    var plant = new Plant
                    {
                        Id = reader.ReadInt32(),
                        SpeciesIndex = reader.ReadInt32(),
                        X = reader.ReadInt32(),
                        Y = reader.ReadInt32(),
                        Z = reader.ReadInt32(),
                        Age = reader.ReadInt32(),
                        Growth = reader.ReadSingle()
                    };

                    // Les plantes sont enregistrées par identifiant croissant, ordre que le reste du code suppose.
                    bool valid = (uint)plant.SpeciesIndex < (uint)species.Count
                        && (uint)plant.X < (uint)world.SizeX
                        && (uint)plant.Z < (uint)world.SizeZ
                        && plant.Id > previousId;
                    if (!valid)
                    {
                        throw new InvalidDataException($"Plante invalide dans la sauvegarde d'écosystème (numéro {plant.Id}).");
                    }

                    previousId = plant.Id;
                    ecosystem.Append(plant);
                }

                return ecosystem;
            }
        }

        public void SaveToFile(string path)
        {
            AtomicFile.Write(path, Write);
        }

        public static Ecosystem LoadFromFile(string path, World world, IReadOnlyList<Species> species)
        {
            using (FileStream stream = File.OpenRead(path))
            {
                return Read(stream, world, species);
            }
        }
    }
}

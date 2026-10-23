using System;
using System.Collections.Generic;

namespace Eco.Sim
{
    // Carcasses: an animal that dies of hunger or old age, or that a player kills, stays where it
    // fell as a carcass; a prey caught by a predator leaves what the predator did not eat. A carcass
    // rots in a few days, giving the soil under it what it loses (a patch of soil richer than the
    // land around, which slowly comes back to normal), unless scavengers (AnimalSpecies.EatsCarrion)
    // eat it first. A column holds at most one carcass; carcasses do not stand in anyone's way.
    // As with everything else, it only changes in Step, in list order.
    public sealed partial class Ecosystem
    {
        // Meat left by a predator's catch; an animal dead of other causes leaves a whole carcass (1).
        private const float RemainsMeat = 0.5f;

        // A whole carcass rots away in this many steps (three days).
        private const float RotSteps = 3 * SimClock.StepsPerDay;

        // Nutrients the soil gains per unit of meat that rots into it, mostly nitrogen, the rest
        // phosphorus (up to Ecosystem.Soil's MaxEnrichment above its capacity).
        private const float NutrientsPerMeat = 1f;
        private const float MeatNitrogenShare = 0.6f;

        // Meat one meal takes from a carcass.
        private const float CarrionBite = 0.25f;

        private readonly List<Carcass> carcasses = new List<Carcass>();
        private bool[] carcassAtColumn;

        // Snapshot: no longer valid after a Step.
        public IReadOnlyList<Carcass> Carcasses => carcasses;

        private void InitializeCarrion()
        {
            carcassAtColumn = new bool[world.SizeX * world.SizeZ];
        }

        // Leaves a carcass where the animal fell. On a column that already has one, the meat adds up.
        private void LeaveCarcass(Animal animal, float meat)
        {
            // An animal that dies in water (a fish, a duck afloat) leaves nothing: the water takes it.
            int ground = SurfaceAt(animal.X, animal.Z);
            if (meat <= 0f || ground < 0) return;

            int column = ColumnIndex(animal.X, animal.Z);
            if (carcassAtColumn[column])
            {
                int index = CarcassIndexAt(animal.X, animal.Z);
                Carcass existing = carcasses[index];
                existing.Meat = Math.Min(1f, existing.Meat + meat);
                carcasses[index] = existing;
                return;
            }

            carcassAtColumn[column] = true;
            carcasses.Add(new Carcass { SpeciesIndex = animal.SpeciesIndex, X = animal.X, Y = ground, Z = animal.Z, Meat = Math.Min(1f, meat) });
        }

        // Each step every carcass rots a little into the soil under it; a carcass whose ground went
        // under water, or that has rotted away, is gone.
        private void StepCarrion()
        {
            if (carcasses.Count == 0) return;

            for (int i = 0; i < carcasses.Count; i++)
            {
                Carcass carcass = carcasses[i];
                carcass.Age++;
                carcass.Y = SurfaceAt(carcass.X, carcass.Z);
                if (carcass.Y < 0)
                {
                    carcass.Meat = 0f;
                    carcasses[i] = carcass;
                    continue;
                }

                float rotted = Math.Min(carcass.Meat, 1f / RotSteps);
                carcass.Meat -= rotted;
                carcasses[i] = carcass;

                int at = CellIndex(carcass.X, carcass.Z) * NutrientCount;
                Enrich(at, rotted * NutrientsPerMeat * MeatNitrogenShare);
                Enrich(at + 1, rotted * NutrientsPerMeat * (1f - MeatNitrogenShare));
            }

            RemoveEmptyCarcasses();
        }

        private bool IsCarrion(int x, int z, AnimalSpecies kind) => kind.EatsCarrion && carcassAtColumn[ColumnIndex(x, z)];

        // A scavenger's meal: a bite out of the carcass, which is gone once nothing is left.
        private void EatCarrion(int x, int z)
        {
            int index = CarcassIndexAt(x, z);
            if (index < 0) return;

            Carcass carcass = carcasses[index];
            carcass.Meat -= CarrionBite;
            carcasses[index] = carcass;
            if (carcass.Meat <= 0f) RemoveEmptyCarcasses();
        }

        // A player takes the carcass lying in a column, if at least minMeat of it is left: it is gone
        // from the world. A player action, between steps; draws nothing at random.
        public bool TryTakeCarcass(int x, int z, float minMeat, out Carcass taken)
        {
            taken = default;
            if ((uint)x >= (uint)world.SizeX || (uint)z >= (uint)world.SizeZ || !carcassAtColumn[ColumnIndex(x, z)]) return false;

            int index = CarcassIndexAt(x, z);
            if (index < 0 || carcasses[index].Meat < minMeat) return false;

            taken = carcasses[index];
            carcasses.RemoveAt(index);
            carcassAtColumn[ColumnIndex(x, z)] = false;
            return true;
        }

        private int CarcassIndexAt(int x, int z)
        {
            for (int i = 0; i < carcasses.Count; i++)
            {
                if (carcasses[i].X == x && carcasses[i].Z == z) return i;
            }

            return -1;
        }

        private void RemoveEmptyCarcasses()
        {
            int kept = 0;
            for (int i = 0; i < carcasses.Count; i++)
            {
                Carcass carcass = carcasses[i];
                if (carcass.Meat <= 0f)
                {
                    carcassAtColumn[ColumnIndex(carcass.X, carcass.Z)] = false;
                    continue;
                }

                carcasses[kept++] = carcass;
            }

            carcasses.RemoveRange(kept, carcasses.Count - kept);
        }

        private void WriteCarrion(System.IO.BinaryWriter writer)
        {
            writer.Write(carcasses.Count);
            foreach (Carcass carcass in carcasses)
            {
                writer.Write(carcass.SpeciesIndex);
                writer.Write(carcass.X);
                writer.Write(carcass.Y);
                writer.Write(carcass.Z);
                writer.Write(carcass.Meat);
                writer.Write(carcass.Age);
            }
        }

        private void ReadCarrion(System.IO.BinaryReader reader)
        {
            int count = reader.ReadInt32();
            if (count < 0 || count > world.SizeX * world.SizeZ)
            {
                throw new System.IO.InvalidDataException($"Invalid carcass count in the ecosystem save ({count}).");
            }

            for (int i = 0; i < count; i++)
            {
                var carcass = new Carcass
                {
                    SpeciesIndex = reader.ReadInt32(),
                    X = reader.ReadInt32(),
                    Y = reader.ReadInt32(),
                    Z = reader.ReadInt32(),
                    Meat = reader.ReadSingle(),
                    Age = reader.ReadInt32()
                };

                bool valid = (uint)carcass.SpeciesIndex < (uint)animalSpecies.Length
                    && (uint)carcass.X < (uint)world.SizeX
                    && (uint)carcass.Z < (uint)world.SizeZ
                    && carcass.Meat > 0f && carcass.Meat <= 1f
                    && !carcassAtColumn[ColumnIndex(carcass.X, carcass.Z)];
                if (!valid) throw new System.IO.InvalidDataException("Invalid carcass in the ecosystem save.");

                carcassAtColumn[ColumnIndex(carcass.X, carcass.Z)] = true;
                carcasses.Add(carcass);
            }
        }
    }

    // What is left of a dead animal: its species, where it lies, and the meat left on it (1 for a
    // whole carcass, 0 when it is gone).
    public struct Carcass
    {
        public int SpeciesIndex;
        public int X;
        public int Y;
        public int Z;
        public float Meat;
        public int Age;
    }
}

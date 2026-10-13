using System;

namespace Eco.Sim
{
    // Règles d'une espèce animale. Les durées sont en pas de simulation, les distances en blocs.
    // La faim va de 0 (rassasié) à 1 (mort de faim).
    public sealed class AnimalSpecies
    {
        public string Name { get; set; }

        // Ce que l'espèce mange : des rangs dans la liste des espèces de plantes et dans celle des animaux.
        public int[] EatsPlants { get; set; } = Array.Empty<int>();
        public int[] EatsAnimals { get; set; } = Array.Empty<int>();

        // Distance parcourue au plus en un pas, et distance à laquelle la nourriture est repérée.
        public int MoveRange { get; set; }
        public int SenseRange { get; set; }

        public int LifespanSteps { get; set; }
        public int MaturitySteps { get; set; }

        public float HungerPerStep { get; set; }

        // Faim retirée par un repas.
        public float MealValue { get; set; }

        // Probabilité, à chaque pas, qu'un adulte bien nourri ait un petit.
        public float BirthChance { get; set; }

        // Territoire : l'animal ne se reproduit pas s'il compte déjà MaxNeighbours congénères à moins de
        // TerritoryRange blocs. C'est ce qui plafonne une population quand la nourriture abonde ; sans
        // cela elle croît jusqu'à tout manger, puis s'effondre. Un rayon nul lève la limite.
        public int TerritoryRange { get; set; }
        public int MaxNeighbours { get; set; }

        // Probabilité d'attraper une proie une fois à son contact.
        public float CatchChance { get; set; }

        // Effectif minimal pour 10 000 colonnes de blocs (un carré de 100 sur 100). En dessous, des
        // individus arrivent par les bords de la carte : l'espèce ne disparaît donc jamais pour de bon.
        // À zéro, rien n'arrive de l'extérieur.
        public float MinDensity { get; set; }

        // Dimensions en blocs : elles servent à viser l'animal.
        public float Height { get; set; }
        public float Width { get; set; }
    }

    public struct Animal
    {
        public int Id;
        public int SpeciesIndex;

        // Case d'air où l'animal se tient, juste au-dessus du sol.
        public int X;
        public int Y;
        public int Z;

        public int Age;
        public float Hunger;
    }
}

namespace Eco.Sim
{
    // Règles d'une espèce de plante. Les durées sont en pas de simulation.
    public sealed class Species
    {
        public string Name { get; set; }

        // Pas nécessaires pour atteindre la taille adulte sur un sol de fertilité 1.
        public int MaturitySteps { get; set; }

        public int LifespanSteps { get; set; }

        // Probabilité, à chaque pas, qu'une plante adulte tente de semer.
        public float SeedChance { get; set; }

        // Distance maximale, en blocs, à laquelle une graine tombe.
        public int SeedRange { get; set; }

        // Une graine ne germe pas à moins de cette distance d'une plante de la même espèce (1 = aucune contrainte).
        // Dans tous les cas, une colonne de blocs ne porte qu'une seule plante.
        public int Spacing { get; set; }

        // En dessous de cette fertilité, la graine ne germe pas et la plante dépérit.
        public float MinFertility { get; set; }

        // Fertilité prélevée à chaque pas par une plante adulte.
        public float FertilityUse { get; set; }

        // Dimensions adultes, en blocs : elles servent à viser la plante.
        public float Height { get; set; }
        public float Width { get; set; }
    }
}

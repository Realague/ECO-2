namespace Voxel.Core
{
    // Un objet d'inventaire est désigné par un entier. Les blocs gardent leur numéro de type ; les
    // autres objets occupent des plages réservées au-dessus.
    public static class Items
    {
        public const int None = 0;
        public const int Wood = 100;

        // Une graine par espèce de plante : FirstSeed + rang de l'espèce.
        public const int MaxSeedSpecies = 100;
        private const int FirstSeed = 200;

        public static int OfBlock(ushort block) => Blocks.TypeOf(block);

        public static bool IsBlock(int item) => item > Blocks.Air && item <= Blocks.LastType;

        public static ushort BlockOf(int item) => (ushort)item;

        public static int Seed(int plantSpecies) => FirstSeed + plantSpecies;

        public static bool IsSeed(int item) => item >= FirstSeed && item < FirstSeed + MaxSeedSpecies;

        public static int SeedSpecies(int item) => item - FirstSeed;

        public static bool IsValid(int item) => IsBlock(item) || item == Wood || IsSeed(item);

        // Ce que rapporte un bloc cassé. L'herbe ne se ramasse pas : on récupère de la terre.
        public static int DropOf(ushort block)
        {
            ushort type = Blocks.TypeOf(block);
            return type == Blocks.Grass ? Blocks.Dirt : type;
        }

        // Nom affiché. Une graine porte en plus le nom de son espèce, que seul l'écosystème connaît.
        public static string NameOf(int item)
        {
            switch (item)
            {
                case Blocks.Stone: return "Pierre";
                case Blocks.Dirt: return "Terre";
                case Blocks.Grass: return "Herbe";
                case Blocks.Planks: return "Planches";
                case Blocks.Bricks: return "Briques";
                case Blocks.Timbered: return "Colombages";
                case Wood: return "Bois";
                default: return IsSeed(item) ? "Graine" : "Objet inconnu";
            }
        }
    }
}

namespace Eco.Sim
{
    // Générateur aléatoire propre à la simulation (xorshift64*). Son état se sauvegarde, et il donne la
    // même suite sur toutes les machines, ce que System.Random ne garantit pas.
    public sealed class SimRandom
    {
        private const ulong DefaultState = 0x9E3779B97F4A7C15UL;

        private ulong state;

        public SimRandom(int seed)
        {
            State = (ulong)(uint)seed * DefaultState + 1UL;
        }

        // L'état ne doit jamais valoir zéro : la suite resterait à zéro.
        public ulong State
        {
            get => state;
            set => state = value == 0UL ? DefaultState : value;
        }

        public ulong NextULong()
        {
            ulong x = state;
            x ^= x >> 12;
            x ^= x << 25;
            x ^= x >> 27;
            state = x;
            return x * 0x2545F4914F6CDD1DUL;
        }

        // Dans [0, 1[.
        public float NextFloat() => (NextULong() >> 40) * (1f / 16777216f);

        // Dans [0, exclusiveMax[.
        public int NextInt(int exclusiveMax) => (int)((NextULong() >> 33) % (ulong)exclusiveMax);
    }
}

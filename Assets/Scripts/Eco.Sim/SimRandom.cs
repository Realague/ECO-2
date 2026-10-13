namespace Eco.Sim
{
    // Random generator dedicated to the simulation (xorshift64*). Its state can be saved, and it gives
    // the same sequence on every machine, which System.Random does not guarantee.
    public sealed class SimRandom
    {
        private const ulong DefaultState = 0x9E3779B97F4A7C15UL;

        private ulong state;

        public SimRandom(int seed)
        {
            State = (ulong)(uint)seed * DefaultState + 1UL;
        }

        // The state must never be zero: the sequence would stay at zero.
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

        // In [0, 1).
        public float NextFloat() => (NextULong() >> 40) * (1f / 16777216f);

        // In [0, exclusiveMax).
        public int NextInt(int exclusiveMax) => (int)((NextULong() >> 33) % (ulong)exclusiveMax);
    }
}

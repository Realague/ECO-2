using System.Collections.Generic;

namespace Voxel.Core
{
    // The hits taken by what is being mined or felled (a block by its position, a plant by its id),
    // and when the last one landed, so that damage wears off when left alone (MiningRules.HitsAfterIdle).
    // Not saved: a server that restarts forgets half-broken blocks.
    public sealed class MiningDamage<TKey>
    {
        private struct Entry
        {
            public int Hits;
            public double LastHit;
        }

        private readonly Dictionary<TKey, Entry> entries = new Dictionary<TKey, Entry>();
        private readonly List<TKey> spent = new List<TKey>();

        public int Count => entries.Count;

        public IEnumerable<TKey> Keys => entries.Keys;

        // The hits still counted at now.
        public int Hits(TKey key, double now)
        {
            return entries.TryGetValue(key, out Entry entry) ? MiningRules.HitsAfterIdle(entry.Hits, now - entry.LastHit) : 0;
        }

        // One more hit at now; returns the hits counted.
        public int AddHit(TKey key, double now)
        {
            int hits = Hits(key, now) + 1;
            entries[key] = new Entry { Hits = hits, LastHit = now };
            return hits;
        }

        // Sets the hits as another machine counted them (a client told by the server).
        public void Set(TKey key, int hits, double now)
        {
            if (hits <= 0) entries.Remove(key);
            else entries[key] = new Entry { Hits = hits, LastHit = now };
        }

        public bool Remove(TKey key) => entries.Remove(key);

        public void Clear() => entries.Clear();

        // Forgets what has worn off completely.
        public void Sweep(double now)
        {
            spent.Clear();
            foreach (KeyValuePair<TKey, Entry> pair in entries)
            {
                if (MiningRules.HitsAfterIdle(pair.Value.Hits, now - pair.Value.LastHit) <= 0) spent.Add(pair.Key);
            }

            foreach (TKey key in spent) entries.Remove(key);
        }
    }
}

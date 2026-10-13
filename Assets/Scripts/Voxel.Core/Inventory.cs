using System.Collections.Generic;
using System.IO;

namespace Voxel.Core
{
    // Les objets que possède un joueur : une quantité par type d'objet, sans limite de place.
    public sealed class Inventory
    {
        // Trié par numéro d'objet : l'ordre d'affichage ne change pas quand les quantités changent.
        private readonly SortedDictionary<int, int> counts = new SortedDictionary<int, int>();

        public int DistinctCount => counts.Count;

        public IEnumerable<KeyValuePair<int, int>> Entries => counts;

        public int Count(int item) => counts.TryGetValue(item, out int count) ? count : 0;

        public bool Has(int item, int count = 1) => Count(item) >= count;

        public void Add(int item, int count)
        {
            if (item == Items.None || count <= 0) return;

            counts[item] = Count(item) + count;
        }

        // Retire la quantité demandée, ou rien du tout si le joueur n'en a pas assez.
        public bool TryRemove(int item, int count = 1)
        {
            if (count <= 0 || !Has(item, count)) return false;

            int left = Count(item) - count;
            if (left > 0) counts[item] = left;
            else counts.Remove(item);
            return true;
        }

        public void Clear() => counts.Clear();

        public void CopyFrom(Inventory other)
        {
            counts.Clear();
            foreach (KeyValuePair<int, int> entry in other.counts)
            {
                counts.Add(entry.Key, entry.Value);
            }
        }

        // Fait passer des objets d'un inventaire à un autre. Renvoie faux, sans rien changer, si le
        // premier n'en a pas assez.
        public static bool Transfer(Inventory from, Inventory to, int item, int count)
        {
            if (!from.TryRemove(item, count)) return false;

            to.Add(item, count);
            return true;
        }

        public void Write(BinaryWriter writer)
        {
            writer.Write(counts.Count);
            foreach (KeyValuePair<int, int> entry in counts)
            {
                writer.Write(entry.Key);
                writer.Write(entry.Value);
            }
        }

        // Les données peuvent venir d'un fichier abîmé : tout objet inconnu ou quantité invalide est refusé.
        public void Read(BinaryReader reader)
        {
            counts.Clear();
            int entries = reader.ReadInt32();
            for (int i = 0; i < entries; i++)
            {
                int item = reader.ReadInt32();
                int count = reader.ReadInt32();
                if (!Items.IsValid(item) || count <= 0 || counts.ContainsKey(item))
                {
                    throw new InvalidDataException($"Inventaire invalide : objet {item}, quantité {count}.");
                }

                counts.Add(item, count);
            }
        }
    }
}

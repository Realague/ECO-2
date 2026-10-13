using System;
using System.Collections.Generic;
using Eco.Sim;
using UnityEngine;

namespace Voxel.Client
{
    // Une espèce de plante décrite par des données : ses règles de simulation et son apparence.
    // Ajouter une espèce revient à créer un de ces fichiers et à l'ajouter à la liste d'EcosystemSync.
    [CreateAssetMenu(menuName = "Eco/Espèce de plante", fileName = "Species")]
    public class SpeciesAsset : ScriptableObject
    {
        // Forme affichée à la taille adulte ; elle est réduite selon le palier de croissance.
        [Serializable]
        public struct Part
        {
            public Mesh mesh;
            public Material material;
            public Vector3 offset;
            public Vector3 scale;
        }

        [Header("Simulation (durées en jours de jeu)")]
        [SerializeField] private float maturityDays = 3f;
        [SerializeField] private float lifespanDays = 30f;
        [SerializeField] private float seedAttemptsPerDay = 1.5f;
        [SerializeField] private int seedRange = 6;
        [SerializeField] private int spacing = 2;
        [SerializeField, Range(0f, 1f)] private float minFertility = 0.2f;
        [SerializeField] private float fertilityUsePerDay = 0.2f;

        [Header("Apparence et visée")]
        [SerializeField] private float height = 5.5f;
        [SerializeField] private float width = 1f;
        [SerializeField] private Part[] parts;

        public IReadOnlyList<Part> Parts => parts;

        public Species ToSpecies()
        {
            return new Species
            {
                Name = name,
                MaturitySteps = Mathf.Max(1, Mathf.RoundToInt(maturityDays * SimClock.StepsPerDay)),
                LifespanSteps = Mathf.Max(1, Mathf.RoundToInt(lifespanDays * SimClock.StepsPerDay)),
                SeedChance = seedAttemptsPerDay / SimClock.StepsPerDay,
                SeedRange = Mathf.Max(1, seedRange),
                Spacing = Mathf.Max(1, spacing),
                MinFertility = minFertility,
                FertilityUse = fertilityUsePerDay / SimClock.StepsPerDay,
                Height = height,
                Width = width
            };
        }
    }
}

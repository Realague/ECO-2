using System;
using System.Collections.Generic;
using Eco.Sim;
using UnityEngine;

namespace Voxel.Client
{
    // A plant species described by data: its simulation rules and its appearance.
    // Adding a species means creating one of these files and adding it to EcosystemSync's list.
    [CreateAssetMenu(menuName = "Eco/Plant species", fileName = "Species")]
    public class SpeciesAsset : ScriptableObject
    {
        // Shape displayed at adult size; it is scaled down according to the growth stage.
        [Serializable]
        public struct Part
        {
            public Mesh mesh;
            public Material material;
            public Vector3 offset;
            public Vector3 scale;
        }

        [Header("Simulation (durations in game days)")]
        [SerializeField] private float maturityDays = 3f;
        [SerializeField] private float lifespanDays = 30f;
        [SerializeField] private float seedAttemptsPerDay = 1.5f;
        [SerializeField] private int seedRange = 6;
        [SerializeField] private int spacing = 2;
        [SerializeField, Range(0f, 1f)] private float minFertility = 0.2f;
        [SerializeField] private float fertilityUsePerDay = 0.2f;

        [Header("Harvest")]
        [Tooltip("Wood obtained by felling an adult plant; a younger plant gives less.")]
        [SerializeField] private int woodYield;

        [Header("Appearance and aiming")]
        [SerializeField] private float height = 5.5f;
        [SerializeField] private float width = 1f;
        [SerializeField] private Part[] parts;

        public IReadOnlyList<Part> Parts => parts;

        public int WoodYield => woodYield;

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

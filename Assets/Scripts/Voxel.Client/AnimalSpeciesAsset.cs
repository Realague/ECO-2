using System;
using System.Collections.Generic;
using Eco.Sim;
using UnityEngine;

namespace Voxel.Client
{
    // An animal species described by data: its simulation rules, what it eats and its appearance.
    // Adding a species means creating one of these files and adding it to EcosystemSync's list.
    [CreateAssetMenu(menuName = "Eco/Animal species", fileName = "Animal")]
    public class AnimalSpeciesAsset : ScriptableObject
    {
        [Header("Diet")]
        [SerializeField] private SpeciesAsset[] eatsPlants = Array.Empty<SpeciesAsset>();
        [SerializeField] private AnimalSpeciesAsset[] eatsAnimals = Array.Empty<AnimalSpeciesAsset>();

        [Header("Simulation (durations in game days, distances in blocks)")]
        [SerializeField] private int moveRange = 3;
        [SerializeField] private int senseRange = 5;
        [SerializeField] private float lifespanDays = 15f;
        [SerializeField] private float maturityDays = 1f;
        [SerializeField] private float daysWithoutFood = 2f;
        [SerializeField, Range(0f, 1f)] private float mealValue = 0.5f;
        [SerializeField] private float birthsPerDay = 1f;

        [Tooltip("Does not breed if it already counts this many of its kind within this range. Range 0: no limit.")]
        [SerializeField] private int territoryRange = 8;
        [SerializeField] private int maxNeighbours = 2;
        [SerializeField, Range(0f, 1f)] private float catchChance = 0.5f;
        [SerializeField] private float minPerTenThousandColumns = 10f;

        [Header("Appearance and aiming")]
        [SerializeField] private float height = 0.6f;
        [SerializeField] private float width = 0.6f;
        [SerializeField] private SpeciesAsset.Part[] parts;

        public IReadOnlyList<SpeciesAsset.Part> Parts => parts;

        // Diets refer to species by their index in the given lists, those of EcosystemSync.
        public AnimalSpecies ToSpecies(SpeciesAsset[] allPlants, AnimalSpeciesAsset[] allAnimals)
        {
            return new AnimalSpecies
            {
                Name = name,
                EatsPlants = IndicesOf(eatsPlants, allPlants),
                EatsAnimals = IndicesOf(eatsAnimals, allAnimals),
                MoveRange = Mathf.Max(1, moveRange),
                SenseRange = Mathf.Max(1, senseRange),
                LifespanSteps = Mathf.Max(1, Mathf.RoundToInt(lifespanDays * SimClock.StepsPerDay)),
                MaturitySteps = Mathf.Max(0, Mathf.RoundToInt(maturityDays * SimClock.StepsPerDay)),
                HungerPerStep = 1f / Mathf.Max(1f, daysWithoutFood * SimClock.StepsPerDay),
                MealValue = mealValue,
                BirthChance = birthsPerDay / SimClock.StepsPerDay,
                TerritoryRange = Mathf.Max(0, territoryRange),
                MaxNeighbours = Mathf.Max(1, maxNeighbours),
                CatchChance = catchChance,
                MinDensity = minPerTenThousandColumns,
                Height = height,
                Width = width
            };
        }

        private static int[] IndicesOf<T>(T[] chosen, T[] all) where T : UnityEngine.Object
        {
            var indices = new List<int>();
            foreach (T item in chosen)
            {
                int index = Array.IndexOf(all, item);
                if (index >= 0) indices.Add(index);
            }

            return indices.ToArray();
        }
    }
}

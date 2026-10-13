using System;
using System.Collections.Generic;
using Eco.Sim;
using UnityEngine;

namespace Voxel.Client
{
    // Une espèce animale décrite par des données : ses règles de simulation, ce qu'elle mange et son
    // apparence. Ajouter une espèce revient à créer un de ces fichiers et à l'ajouter à la liste d'EcosystemSync.
    [CreateAssetMenu(menuName = "Eco/Espèce animale", fileName = "Animal")]
    public class AnimalSpeciesAsset : ScriptableObject
    {
        [Header("Régime alimentaire")]
        [SerializeField] private SpeciesAsset[] eatsPlants = Array.Empty<SpeciesAsset>();
        [SerializeField] private AnimalSpeciesAsset[] eatsAnimals = Array.Empty<AnimalSpeciesAsset>();

        [Header("Simulation (durées en jours de jeu, distances en blocs)")]
        [SerializeField] private int moveRange = 3;
        [SerializeField] private int senseRange = 5;
        [SerializeField] private float lifespanDays = 15f;
        [SerializeField] private float maturityDays = 1f;
        [SerializeField] private float daysWithoutFood = 2f;
        [SerializeField, Range(0f, 1f)] private float mealValue = 0.5f;
        [SerializeField] private float birthsPerDay = 1f;

        [Tooltip("Ne se reproduit pas s'il compte déjà ce nombre de congénères dans ce rayon. Rayon 0 : aucune limite.")]
        [SerializeField] private int territoryRange = 8;
        [SerializeField] private int maxNeighbours = 2;
        [SerializeField, Range(0f, 1f)] private float catchChance = 0.5f;
        [SerializeField] private float minPerTenThousandColumns = 10f;

        [Header("Apparence et visée")]
        [SerializeField] private float height = 0.6f;
        [SerializeField] private float width = 0.6f;
        [SerializeField] private SpeciesAsset.Part[] parts;

        public IReadOnlyList<SpeciesAsset.Part> Parts => parts;

        // Les régimes désignent les espèces par leur rang dans les listes données, celles d'EcosystemSync.
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

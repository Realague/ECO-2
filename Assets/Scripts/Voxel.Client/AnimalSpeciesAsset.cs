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
        [Tooltip("A scavenger: also eats carcasses.")]
        [SerializeField] private bool eatsCarrion;
        [Tooltip("Keeps a seed of each plant it eats and lets it fall later, wherever it has gone.")]
        [SerializeField] private bool spreadsSeeds;

        [Tooltip("Land; Water (fish); Shore: land and floating on water (ducks, otters); Wader: land and shallow water (herons).")]
        [SerializeField] private AnimalHabitat habitat = AnimalHabitat.Land;

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

        [Tooltip("Tallest plant it reaches, in blocks: taller plants (grown trees) are out of its reach.")]
        [SerializeField] private float reachHeight = 1.5f;
        [Tooltip("Keeps near others of its kind instead of wandering off.")]
        [SerializeField] private bool herd;

        [Header("Climate")]
        [Tooltip("Warmth it thrives in, from 0 (the coldest) to 1 (the hottest): the climate's temperature.")]
        [SerializeField] private Vector2 idealWarmth = new Vector2(0f, 1f);
        [Tooltip("Wetness it thrives in, from 0 (the driest) to 1 (the wettest): the climate's humidity.")]
        [SerializeField] private Vector2 idealWetness = new Vector2(0f, 1f);
        [Tooltip("How far past those ranges it still lives, less and less well.")]
        [SerializeField] private float climateTolerance = 0.1f;

        [Header("Appearance and aiming")]
        [SerializeField] private float height = 0.6f;
        [SerializeField] private float width = 0.6f;
        [SerializeField] private SpeciesAsset.Part[] parts;

        public IReadOnlyList<SpeciesAsset.Part> Parts => parts;

        public enum Gait { Glide, Hop, Trot, Fly }

        public enum PartMotion { Body, Ear, FrontLeg, HindLeg, Tail, Wing }

        [Serializable]
        public struct PartRig
        {
            public PartMotion motion;
            [Tooltip("Point the part turns around, in the same space as the part's offset.")]
            public Vector3 pivot;
        }

        [Header("Movement animation")]
        [Tooltip("Glide: slides without moving its parts. Hop: hops, legs, ears and tail follow. " +
                 "Trot: walks at walkSpeed, legs swinging in diagonal pairs, the tail swaying. " +
                 "Fly: flies at walkSpeed in an arc up to flightHeight, beating its wings, and perches at rest.")]
        [SerializeField] private Gait gait = Gait.Glide;
        [Tooltip("Distance covered by one hop, in blocks.")]
        [SerializeField] private float hopLength = 0.6f;
        [Tooltip("Seconds one hop lasts.")]
        [SerializeField] private float hopDuration = 0.3f;
        [SerializeField] private float hopHeight = 0.2f;
        [Tooltip("Speed of a gliding or trotting move, in blocks per second.")]
        [SerializeField] private float walkSpeed = 2.5f;
        [Tooltip("Distance covered by one full trot cycle (each leg forward once), in blocks.")]
        [SerializeField] private float strideLength = 0.7f;
        [Tooltip("Fly: how high above the ground the middle of a flight goes, in blocks, and how many wingbeats a second.")]
        [SerializeField] private float flightHeight = 4f;
        [SerializeField] private float wingbeatsPerSecond = 4f;
        [Tooltip("Fly: the share of their length the wings keep when folded at rest.")]
        [SerializeField, Range(0.1f, 1f)] private float wingFold = 0.45f;
        [Tooltip("Point the body tilts around during a hop.")]
        [SerializeField] private Vector3 bodyPivot = new Vector3(0f, 0.2f, 0f);
        [Tooltip("One entry per part, in the same order as Parts. Parts without an entry move with the body.")]
        [SerializeField] private PartRig[] rig = Array.Empty<PartRig>();

        public Gait MoveGait => gait;
        public AnimalHabitat Habitat => habitat;
        public float HopLength => Mathf.Max(0.05f, hopLength);
        public float HopDuration => Mathf.Max(0.05f, hopDuration);
        public float HopHeight => hopHeight;
        public float WalkSpeed => Mathf.Max(0.1f, walkSpeed);
        public float StrideLength => Mathf.Max(0.05f, strideLength);
        public float FlightHeight => flightHeight;
        public float WingbeatsPerSecond => wingbeatsPerSecond;
        public float WingFold => wingFold;

        // How long a move over this horizontal distance takes at the species' own pace: whole hops
        // for a hopping animal, a steady walk otherwise.
        public float TravelSeconds(float distance)
        {
            return gait == Gait.Hop ? HopCount(distance, HopLength) * HopDuration : distance / WalkSpeed;
        }

        // Hops needed to cover a distance, at least one: they are stretched or shortened to land exactly.
        public static int HopCount(float distance, float hopLength) => Mathf.Max(1, Mathf.RoundToInt(distance / hopLength));
        public Vector3 BodyPivot => bodyPivot;
        public PartRig RigOf(int part) => rig != null && part < rig.Length ? rig[part] : default;

        // Diets refer to species by their index in the given lists, those of EcosystemSync.
        public AnimalSpecies ToSpecies(SpeciesAsset[] allPlants, AnimalSpeciesAsset[] allAnimals)
        {
            return new AnimalSpecies
            {
                Name = name,
                EatsPlants = IndicesOf(eatsPlants, allPlants),
                EatsAnimals = IndicesOf(eatsAnimals, allAnimals),
                EatsCarrion = eatsCarrion,
                SpreadsSeeds = spreadsSeeds,
                Habitat = habitat,
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
                Width = width,
                ReachHeight = reachHeight,
                Herd = herd,
                WarmthMin = idealWarmth.x,
                WarmthMax = idealWarmth.y,
                WetnessMin = idealWetness.x,
                WetnessMax = idealWetness.y,
                ClimateTolerance = climateTolerance
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

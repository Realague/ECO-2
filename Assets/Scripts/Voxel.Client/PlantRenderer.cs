using System.Collections.Generic;
using Eco.Sim;
using UnityEngine;
using UnityEngine.Rendering;

namespace Voxel.Client
{
    // Ce qu'une machine sait d'une plante pour l'afficher et la viser.
    internal struct PlantView
    {
        public int SpeciesIndex;
        public int X;
        public int Y;
        public int Z;
        public int Stage;
    }

    // Dessine toutes les plantes par lots (instanciation GPU), sans créer un objet par plante.
    internal sealed class PlantRenderer
    {
        private const int MaxInstancesPerCall = 1023;

        private readonly SpeciesAsset[] species;

        // Par espèce puis par partie : les matrices des plantes de cette espèce.
        private readonly Matrix4x4[][][] matrices;
        private readonly int[] counts;
        private readonly RenderParams[][] renderParams;

        public PlantRenderer(SpeciesAsset[] species)
        {
            this.species = species;
            matrices = new Matrix4x4[species.Length][][];
            counts = new int[species.Length];
            renderParams = new RenderParams[species.Length][];

            // Les plantes sont réparties dans tout le monde : pas d'élimination par boîte englobante.
            var everywhere = new Bounds(Vector3.zero, Vector3.one * 100000f);
            for (int s = 0; s < species.Length; s++)
            {
                IReadOnlyList<SpeciesAsset.Part> parts = species[s].Parts;
                matrices[s] = new Matrix4x4[parts.Count][];
                renderParams[s] = new RenderParams[parts.Count];
                for (int p = 0; p < parts.Count; p++)
                {
                    matrices[s][p] = new Matrix4x4[0];
                    renderParams[s][p] = new RenderParams(parts[p].material)
                    {
                        shadowCastingMode = ShadowCastingMode.On,
                        receiveShadows = true,
                        worldBounds = everywhere
                    };
                }
            }
        }

        // À appeler quand la liste des plantes a changé.
        public void Rebuild(Dictionary<int, PlantView> plants)
        {
            for (int s = 0; s < species.Length; s++)
            {
                counts[s] = 0;
            }

            foreach (PlantView plant in plants.Values)
            {
                counts[plant.SpeciesIndex]++;
            }

            for (int s = 0; s < species.Length; s++)
            {
                for (int p = 0; p < matrices[s].Length; p++)
                {
                    if (matrices[s][p].Length < counts[s]) matrices[s][p] = new Matrix4x4[Mathf.NextPowerOfTwo(counts[s])];
                }

                counts[s] = 0;
            }

            foreach (PlantView plant in plants.Values)
            {
                int s = plant.SpeciesIndex;
                float size = PlantShape.Scale(plant.Stage);
                var foot = new Vector3(plant.X + 0.5f, plant.Y, plant.Z + 0.5f);
                IReadOnlyList<SpeciesAsset.Part> parts = species[s].Parts;
                for (int p = 0; p < parts.Count; p++)
                {
                    matrices[s][p][counts[s]] = Matrix4x4.TRS(foot + parts[p].offset * size, Quaternion.identity, parts[p].scale * size);
                }

                counts[s]++;
            }
        }

        // À appeler à chaque image.
        public void Render()
        {
            for (int s = 0; s < species.Length; s++)
            {
                IReadOnlyList<SpeciesAsset.Part> parts = species[s].Parts;
                for (int p = 0; p < parts.Count; p++)
                {
                    for (int start = 0; start < counts[s]; start += MaxInstancesPerCall)
                    {
                        int count = Mathf.Min(MaxInstancesPerCall, counts[s] - start);
                        Graphics.RenderMeshInstanced(renderParams[s][p], parts[p].mesh, 0, matrices[s][p], count, start);
                    }
                }
            }
        }
    }
}

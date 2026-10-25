using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using Voxel.Core;

namespace Voxel.Client
{
    // The claimed plots drawn over the maps: a texture of one pixel per block column, like WorldMap's,
    // each claimed plot tinted by what the local player is to its owner (theirs pale gold, allowed
    // green, anyone else a colour of their own from their name) and outlined where it meets land
    // that is not its owner's. Redrawn only where a plot changed, uploaded at most ten times a second.
    public sealed class ClaimLayer : IDisposable
    {
        private const float UploadInterval = 0.1f;
        private const byte FillAlpha = 64;
        private const byte EdgeAlpha = 230;

        private static readonly Color32 YoursColour = new Color32(232, 200, 120, 255);
        private static readonly Color32 AllowedColour = new Color32(120, 200, 130, 255);

        private readonly ClaimSync claims;
        private readonly HashSet<int> dirty = new HashSet<int>();
        private Texture2D texture;
        private bool redrawAll;
        private bool changed;
        private float nextUpload;

        public ClaimLayer(ClaimSync claims)
        {
            this.claims = claims;
            claims.PlotChanged += OnPlotChanged;
            redrawAll = true;
        }

        // Null while there is no world.
        public Texture2D Texture => texture;

        // The colour a player's land is drawn in, by what the local player is to them.
        public static Color32 ColourOf(string ownerName, ClaimRelation relation)
        {
            if (relation == ClaimRelation.Yours) return YoursColour;
            if (relation == ClaimRelation.Allowed) return AllowedColour;

            // FNV-1a, the same colour for a name on every machine and every session.
            uint hash = 2166136261;
            foreach (char c in (ownerName ?? "").ToLowerInvariant())
            {
                hash = (hash ^ c) * 16777619;
            }

            // Hues away from the gold and the green of one's own and allowed land.
            float hue = (0.5f + (hash % 1000) / 1000f * 0.62f) % 1f;
            Color colour = Color.HSVToRGB(hue, 0.6f, 0.95f);
            return colour;
        }

        public void Update()
        {
            int width = claims.PlotsX * ClaimRules.PlotSize;
            int height = claims.PlotsZ * ClaimRules.PlotSize;
            if (width == 0 || height == 0)
            {
                if (texture != null) UnityEngine.Object.Destroy(texture);
                texture = null;
                return;
            }

            if (texture == null || texture.width != width || texture.height != height)
            {
                if (texture != null) UnityEngine.Object.Destroy(texture);
                texture = new Texture2D(width, height, TextureFormat.RGBA32, false)
                {
                    name = "ClaimLayer",
                    filterMode = FilterMode.Point,
                    wrapMode = TextureWrapMode.Clamp
                };
                redrawAll = true;
            }

            if (redrawAll)
            {
                redrawAll = false;
                dirty.Clear();
                NativeArray<Color32> pixels = texture.GetPixelData<Color32>(0);
                for (int plotZ = 0; plotZ < claims.PlotsZ; plotZ++)
                {
                    for (int plotX = 0; plotX < claims.PlotsX; plotX++)
                    {
                        DrawPlot(pixels, width, plotX, plotZ);
                    }
                }

                changed = true;
            }
            else if (dirty.Count > 0)
            {
                NativeArray<Color32> pixels = texture.GetPixelData<Color32>(0);
                foreach (int plot in dirty)
                {
                    DrawPlot(pixels, width, plot % claims.PlotsX, plot / claims.PlotsX);
                }

                dirty.Clear();
                changed = true;
            }

            if (changed && Time.unscaledTime >= nextUpload)
            {
                texture.Apply(false);
                changed = false;
                nextUpload = Time.unscaledTime + UploadInterval;
            }
        }

        public void Dispose()
        {
            claims.PlotChanged -= OnPlotChanged;
            if (texture != null) UnityEngine.Object.Destroy(texture);
            texture = null;
        }

        private void OnPlotChanged(int plotX, int plotZ)
        {
            if (plotX < 0)
            {
                redrawAll = true;
                return;
            }

            // Its neighbours' outlines along it change too.
            for (int dz = -1; dz <= 1; dz++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    int x = plotX + dx;
                    int z = plotZ + dz;
                    if (x >= 0 && z >= 0 && x < claims.PlotsX && z < claims.PlotsZ) dirty.Add(z * claims.PlotsX + x);
                }
            }
        }

        private void DrawPlot(NativeArray<Color32> pixels, int width, int plotX, int plotZ)
        {
            int owner = claims.ShownOwnerIndex(plotX, plotZ);
            Color32 fill = default;
            Color32 edge = default;
            if (owner >= 0 && claims.TryGetShownOwner(owner, out string name, out ClaimRelation relation))
            {
                Color32 colour = ColourOf(name, relation);
                fill = new Color32(colour.r, colour.g, colour.b, FillAlpha);
                edge = new Color32(colour.r, colour.g, colour.b, EdgeAlpha);
            }
            else
            {
                owner = -1;
            }

            bool west = owner >= 0 && claims.ShownOwnerIndex(plotX - 1, plotZ) != owner;
            bool east = owner >= 0 && claims.ShownOwnerIndex(plotX + 1, plotZ) != owner;
            bool south = owner >= 0 && claims.ShownOwnerIndex(plotX, plotZ - 1) != owner;
            bool north = owner >= 0 && claims.ShownOwnerIndex(plotX, plotZ + 1) != owner;
            int last = ClaimRules.PlotSize - 1;
            for (int z = 0; z < ClaimRules.PlotSize; z++)
            {
                int row = (plotZ * ClaimRules.PlotSize + z) * width + plotX * ClaimRules.PlotSize;
                for (int x = 0; x < ClaimRules.PlotSize; x++)
                {
                    bool onEdge = (west && x == 0) || (east && x == last) || (south && z == 0) || (north && z == last);
                    pixels[row + x] = onEdge ? edge : fill;
                }
            }
        }
    }
}

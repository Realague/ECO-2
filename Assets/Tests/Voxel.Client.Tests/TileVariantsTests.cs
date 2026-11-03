using System.Collections.Generic;
using NUnit.Framework;

namespace Voxel.Client.Tests
{
    public class TileVariantsTests
    {
        private static readonly string[] Tiles = { "Granite", "Dirt", "GrassTop" };

        private static TileVariants Make(string weights, params string[] files)
        {
            var present = new HashSet<string>(files);
            var problems = new List<string>();
            TileVariants variants = TileVariants.Parse(Tiles, present.Contains, weights, problems);
            Assert.IsEmpty(problems, string.Join("; ", problems));
            return variants;
        }

        // Variants are the files numbered from 2 while they follow each other; they take slots after the
        // base tiles, in tile order.
        [Test]
        public void Variants_AreTheNumberedFiles_AfterTheBaseTiles()
        {
            TileVariants variants = Make("", "Granite_2", "Granite_3", "Granite_5", "GrassTop_2");

            Assert.AreEqual(3 + 2 + 1, variants.SlotCount, "Granite_5 does not follow Granite_3");
            Assert.AreEqual(3, variants.VariantCount(0));
            Assert.AreEqual(1, variants.VariantCount(1));
            Assert.AreEqual("Granite_2", variants.SlotName(3));
            Assert.AreEqual("GrassTop_2", variants.SlotName(5));
            Assert.AreEqual(0, variants.BaseOf(4));
            Assert.AreEqual(2, variants.BaseOf(5));
        }

        // Over many blocks each file shows about as often as its weight says; a weight of 0 never shows.
        [Test]
        public void Picks_FollowTheWeights()
        {
            TileVariants variants = Make("Granite: 6 3 0 1", "Granite_2", "Granite_3", "Granite_4");
            var counts = new Dictionary<int, int>();
            for (int x = 0; x < 100; x++)
            {
                for (int z = 0; z < 100; z++)
                {
                    int slot = variants.Pick(0, x, 20, z, 2, out _, out _);
                    counts[slot] = counts.TryGetValue(slot, out int count) ? count + 1 : 1;
                }
            }

            Assert.AreEqual(0.6, counts[0] / 10000.0, 0.03);
            Assert.AreEqual(0.3, counts[3] / 10000.0, 0.03);
            Assert.IsFalse(counts.ContainsKey(4), "weight 0");
            Assert.AreEqual(0.1, counts[5] / 10000.0, 0.02);
        }

        // The same face always picks the same; a tile without variants is always itself.
        [Test]
        public void Picks_AreTheSameEveryTime()
        {
            TileVariants variants = Make("", "Granite_2", "Granite_3");
            for (int i = 0; i < 50; i++)
            {
                Assert.AreEqual(variants.Pick(0, i, -i, 3 * i, i % 6, out _, out _), variants.Pick(0, i, -i, 3 * i, i % 6, out _, out _));
                Assert.AreEqual(1, variants.Pick(1, i, -i, 3 * i, i % 6, out _, out _));
            }
        }

        // Only the tiles listed to rotate turn, and only on a block's top and bottom.
        [Test]
        public void Rotation_OnlyOnTheTopAndBottomOfListedTiles()
        {
            TileVariants variants = Make("rotate: Dirt");
            bool turned = false;
            for (int x = 0; x < 64; x++)
            {
                variants.Pick(1, x, 5, 7, ChunkMesher.TopFace, out int turn, out bool mirror);
                turned |= turn != 0 || mirror;

                variants.Pick(1, x, 5, 7, 0, out int sideTurn, out bool sideMirror);
                Assert.IsTrue(sideTurn == 0 && !sideMirror, "a side never turns");
                variants.Pick(0, x, 5, 7, ChunkMesher.TopFace, out int graniteTurn, out _);
                Assert.AreEqual(0, graniteTurn, "granite is not listed");
            }

            Assert.IsTrue(turned);
        }

        [Test]
        public void ABadLine_IsReported()
        {
            var problems = new List<string>();
            TileVariants.Parse(Tiles, _ => false, "Basalt: 1 2\nGranite: x\nnonsense\n# fine\nrotate: Nothing", problems);
            Assert.AreEqual(4, problems.Count, string.Join("; ", problems));
        }
    }
}

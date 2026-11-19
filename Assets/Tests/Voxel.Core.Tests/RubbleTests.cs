using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using NUnit.Framework;

namespace Voxel.Core.Tests
{
    public class RubbleTests
    {
        [Test]
        public void WhatAPickaxeBreaks_LeavesRubble()
        {
            Assert.IsTrue(MiningRules.LeavesRubble(Blocks.Stone));
            Assert.IsTrue(MiningRules.LeavesRubble(Ores.Make(Blocks.Stone, OreKind.Iron)));
            Assert.IsTrue(MiningRules.LeavesRubble(Blocks.Ice));
            Assert.IsFalse(MiningRules.LeavesRubble(Blocks.Dirt));
            Assert.IsFalse(MiningRules.LeavesRubble(Blocks.Grass));
            Assert.IsFalse(MiningRules.LeavesRubble(Blocks.Torch));
            Assert.IsTrue(WoodBlocks.TryPlanksOf(0, out ushort planks));
            Assert.IsFalse(MiningRules.LeavesRubble(planks));
        }

        [Test]
        public void Piles_AreAddedMovedAndRemoved_ByColumn()
        {
            var piles = new RubblePiles();
            Rubble a = piles.Add(new Vector3(3.4f, 8f, 5.6f), 0.5f, Blocks.Stone);
            Rubble b = piles.Add(new Vector3(3.7f, 9f, 5.2f), 1f, Blocks.Ice);
            var ids = new List<int>();

            piles.InColumn(3, 5, ids);
            CollectionAssert.AreEquivalent(new[] { a.Id, b.Id }, ids);

            Assert.IsTrue(piles.Move(a.Id, new Vector3(4.5f, 8f, 5.5f)));
            piles.InColumn(3, 5, ids);
            CollectionAssert.AreEqual(new[] { b.Id }, ids);
            piles.InColumn(4, 5, ids);
            CollectionAssert.AreEqual(new[] { a.Id }, ids);

            Assert.IsTrue(piles.Remove(b.Id));
            Assert.IsFalse(piles.TryGet(b.Id, out _));
            Assert.AreEqual(1, piles.Count);
        }

        [Test]
        public void Piles_KeepTheirRockAndOre_AcrossASave()
        {
            var piles = new RubblePiles();
            ushort iron = Ores.Make(Blocks.Stone, OreKind.Iron);
            Rubble rubble = piles.Add(new Vector3(1.5f, 7f, 2.5f), 2f, iron);
            var stream = new MemoryStream();
            piles.Write(stream);
            stream.Position = 0;

            RubblePiles read = RubblePiles.Read(stream);

            Assert.IsTrue(read.TryGet(rubble.Id, out Rubble back));
            Assert.AreEqual(iron, back.Block);
            Assert.AreEqual(OreKind.Iron, Ores.OreOf(back.Block));
            Assert.AreEqual(rubble.Position, back.Position);
            Assert.AreEqual(rubble.Id + 1, read.Add(Vector3.Zero, 0f, Blocks.Stone).Id, "ids go on after the saved ones");
        }

        // Where a player placed a block is kept across a save, and forgotten once asked.
        [Test]
        public void PlacedCells_AreKeptAcrossASave_AndTakenOnce()
        {
            var piles = new RubblePiles();
            piles.MarkPlaced(new BlockPos(3, 9, 4));
            var stream = new MemoryStream();
            piles.Write(stream);
            stream.Position = 0;

            RubblePiles read = RubblePiles.Read(stream);

            Assert.IsTrue(read.TakePlaced(new BlockPos(3, 9, 4)));
            Assert.IsFalse(read.TakePlaced(new BlockPos(3, 9, 4)), "taken once");
            Assert.IsFalse(read.TakePlaced(new BlockPos(3, 8, 4)));
        }

        // The pieces spread about the broken block's cell, onto the ground beside it, but never into a
        // wall: a piece whose way out is blocked stays in the block's own cell.
        [Test]
        public void Pieces_SpreadAboutTheCell_ButNotIntoAWall()
        {
            World world = FlatGenerator.CreateWorld();
            int ground = FlatGenerator.GroundHeight;
            var pos = new BlockPos(8, ground, 8);
            var spots = new List<Vector3>();

            RubbleScatter.Spots(world, pos, 3, 0f, spots);
            Assert.AreEqual(3, spots.Count);
            foreach (Vector3 spot in spots)
            {
                Assert.AreEqual(ground, spot.Y, 0.001f, "on the ground");
                Assert.AreEqual(RubbleScatter.Reach, Vector2.Distance(new Vector2(spot.X, spot.Z), new Vector2(8.5f, 8.5f)), 0.001f);
            }

            Assert.AreEqual(9, (int)MathF.Floor(spots[0].X), "the first goes out toward +x, into the next column");

            world.SetBlock(9, ground, 8, Blocks.Stone);
            RubbleScatter.Spots(world, pos, 3, 0f, spots);
            Assert.AreEqual(9, (int)MathF.Floor(spots[0].X), "out of the hole");
            Assert.AreEqual(ground + 1, spots[0].Y, 0.001f, "onto its rim");

            world.SetBlock(9, ground + 1, 8, Blocks.Stone);
            RubbleScatter.Spots(world, pos, 3, 0f, spots);
            Assert.AreEqual(8, (int)MathF.Floor(spots[0].X), "a wall keeps it in the cell");
            Assert.AreEqual(ground, spots[0].Y, 0.001f);

            RubbleScatter.Spots(world, pos, 1, 0f, spots);
            Assert.AreEqual(new Vector3(8.5f, ground, 8.5f), spots[0], "one piece lies in the middle");
        }

        [Test]
        public void AnotherFile_IsRefused()
        {
            var stream = new MemoryStream();
            new LyingLogs().Write(stream);
            stream.Position = 0;

            Assert.Throws<InvalidDataException>(() => RubblePiles.Read(stream));
        }

        // Rubble sinks to a pond's bed; a log floats on it.
        [Test]
        public void Rubble_SinksThroughWater_WhereALogFloats()
        {
            World world = FlatGenerator.CreateWorld();
            int ground = FlatGenerator.GroundHeight;
            world.SetBlock(4, ground - 1, 4, Blocks.Water);
            world.SetBlock(4, ground - 2, 4, Blocks.Water);
            var above = new Vector3(4.5f, ground + 1, 4.5f);

            Assert.IsTrue(LogPlacement.Settle(world, above, out Vector3 sunk, false));
            Assert.AreEqual(ground - 2, sunk.Y, 0.001f);
            Assert.IsTrue(LogPlacement.Settle(world, above, out Vector3 floating));
            Assert.AreEqual(ground - LogPlacement.FloatDraft, floating.Y, 0.001f);
        }
    }
}

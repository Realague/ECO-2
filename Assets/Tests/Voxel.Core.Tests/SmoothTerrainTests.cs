using System.Numerics;
using NUnit.Framework;

namespace Voxel.Core.Tests
{
    public class SmoothTerrainTests
    {
        private const int Ground = FlatGenerator.GroundHeight;

        // Flat ground keeps its exact height, its normals point straight up, and the vertices sit on
        // the block grid's corners, up to the world's sides (beyond which the edge column is repeated).
        [Test]
        public void FlatGround_KeepsItsHeight()
        {
            World world = FlatGenerator.CreateWorld();
            for (int z = 0; z <= world.SizeZ; z++)
            {
                for (int x = 0; x <= world.SizeX; x++)
                {
                    SmoothTerrain.CornerVertex(world, x, Ground, z, out Vector3 position, out Vector3 normal);
                    Assert.AreEqual(new Vector3(x, Ground, z), position, $"corner ({x}, {z})");
                    Assert.AreEqual(Vector3.UnitY, normal, $"corner ({x}, {z})");
                }
            }

            Assert.AreEqual(Ground, SmoothTerrain.HeightAt(world, 5, 5, Ground, 0.5f, 0.5f));
        }

        // Every vertex lies within half a block of its corner, on generated terrain.
        [Test]
        public void EveryVertex_StaysWithinItsCell()
        {
            var world = new World(4, 4, 4);
            world.Generate(new TerrainGenerator(), 1);
            int crossing = 0;
            for (int y = 1; y < world.SizeY; y += 3)
            {
                for (int z = 0; z <= world.SizeZ; z += 2)
                {
                    for (int x = 0; x <= world.SizeX; x += 2)
                    {
                        int mask = SmoothTerrain.CornerMask(world, x, y, z);
                        if (mask == 0 || mask == 0xFF) continue;

                        crossing++;
                        SmoothTerrain.CornerVertex(mask, x, y, z, out Vector3 position, out _);
                        Assert.That(position.X, Is.InRange(x - 0.5f, x + 0.5f), $"({x}, {y}, {z})");
                        Assert.That(position.Y, Is.InRange(y - 0.5f, y + 0.5f), $"({x}, {y}, {z})");
                        Assert.That(position.Z, Is.InRange(z - 0.5f, z + 0.5f), $"({x}, {y}, {z})");
                    }
                }
            }

            Assert.Greater(crossing, 100, "the ground crosses many cells");
        }

        // A step of one block becomes a ramp: the lower column rises toward it, the upper one falls
        // toward the lower, and both stay between their two heights.
        [Test]
        public void AStep_BecomesARamp()
        {
            World world = FlatGenerator.CreateWorld();
            for (int z = 0; z < world.SizeZ; z++)
            {
                for (int x = 8; x < world.SizeX; x++)
                {
                    world.SetBlock(x, Ground, z, Blocks.Stone);
                }
            }

            float lowFar = SmoothTerrain.HeightAt(world, 4, 5, Ground, 0.5f, 0.5f);
            float lowNear = SmoothTerrain.HeightAt(world, 7, 5, Ground, 0.5f, 0.5f);
            float highNear = SmoothTerrain.HeightAt(world, 8, 5, Ground + 1, 0.5f, 0.5f);
            float highFar = SmoothTerrain.HeightAt(world, 12, 5, Ground + 1, 0.5f, 0.5f);

            Assert.AreEqual(Ground, lowFar);
            Assert.AreEqual(Ground + 1, highFar);
            Assert.That(lowNear, Is.GreaterThan(Ground).And.LessThan(highNear));
            Assert.That(highNear, Is.LessThan(Ground + 1));

            // The step's edge is rounded: its upper corner is pulled down and out, its lower one up and in.
            SmoothTerrain.CornerVertex(world, 8, Ground + 1, 5, out Vector3 upper, out Vector3 upperNormal);
            SmoothTerrain.CornerVertex(world, 8, Ground, 5, out Vector3 lower, out _);
            Assert.AreEqual(new Vector3(8.25f, Ground + 0.75f, 5f), upper);
            Assert.AreEqual(new Vector3(7.75f, Ground + 0.25f, 5f), lower);
            Assert.Less(upperNormal.X, 0f, "the edge faces toward the lower side");
            Assert.Greater(upperNormal.Y, 0f, "and up");
        }

        // Along a pool's edge, at the water's surface, the ground keeps its block corners, so that the
        // water (drawn in blocks) never bares its sides above a bank, nor hides under one.
        [Test]
        public void AtTheWatersSurface_TheBankKeepsItsCorners()
        {
            World world = FlatGenerator.CreateWorld();
            world.SetBlock(5, Ground - 1, 5, Blocks.Water);

            SmoothTerrain.CornerVertex(world, 5, Ground, 5, out Vector3 surface, out _);
            Assert.AreEqual(new Vector3(5f, Ground, 5f), surface, "the pool's rim");

            // Without the water, the hole's rim is rounded.
            world.SetBlock(5, Ground - 1, 5, Blocks.Air);
            SmoothTerrain.CornerVertex(world, 5, Ground, 5, out Vector3 hole, out _);
            Assert.AreNotEqual(new Vector3(5f, Ground, 5f), hole);
        }

        // Ground under a player's block (a construction block or a torch) stays a cube with square
        // corners, and the smoothed ground around meets it there; the ground beside it is smoothed.
        [Test]
        public void GroundUnderAPlayersBlock_KeepsItsCorners()
        {
            World world = FlatGenerator.CreateWorld();
            for (int z = 0; z < world.SizeZ; z++)
            {
                for (int x = 8; x < world.SizeX; x++)
                {
                    world.SetBlock(x, Ground, z, Blocks.Stone);
                }
            }

            SmoothTerrain.CornerVertex(world, 8, Ground + 1, 5, out Vector3 free, out _);
            Assert.AreNotEqual(new Vector3(8f, Ground + 1, 5f), free, "the step's edge is rounded");

            world.SetBlock(8, Ground + 1, 5, Blocks.FirstPlanks);
            Assert.IsTrue(SmoothTerrain.IsPinned(world, 8, Ground, 5));
            Assert.IsFalse(SmoothTerrain.IsPinned(world, 8, Ground, 7), "beside it");
            Assert.IsFalse(SmoothTerrain.IsPinned(world, 8, Ground + 1, 5), "the planks themselves are no ground");
            SmoothTerrain.CornerVertex(world, 8, Ground + 1, 5, out Vector3 pinned, out _);
            SmoothTerrain.CornerVertex(world, 8, Ground, 6, out Vector3 below, out _);
            Assert.AreEqual(new Vector3(8f, Ground + 1, 5f), pinned, "the block's top corner");
            Assert.AreEqual(new Vector3(8f, Ground, 6f), below, "its bottom corner");

            // A torch pins the ground it stands on too; taking it away frees it.
            world.SetBlock(12, Ground + 1, 5, Blocks.Torch);
            Assert.IsTrue(SmoothTerrain.IsPinned(world, 12, Ground, 5));
            world.SetBlock(12, Ground + 1, 5, Blocks.Air);
            Assert.IsFalse(SmoothTerrain.IsPinned(world, 12, Ground, 5));
        }

        // Ground pinned for storage standing on it (World.Pins: a chest, a stockpile's cell, a station)
        // keeps its corners too, until nothing stands there any more; each change is told once.
        [Test]
        public void GroundUnderStorage_KeepsItsCorners()
        {
            World world = FlatGenerator.CreateWorld();
            for (int z = 0; z < world.SizeZ; z++)
            {
                for (int x = 8; x < world.SizeX; x++)
                {
                    world.SetBlock(x, Ground, z, Blocks.Stone);
                }
            }

            int changes = 0;
            world.Pins.Changed += cell => changes++;
            var ground = new BlockPos(8, Ground, 5);
            world.Pins.Pin(ground);
            world.Pins.Pin(ground);
            Assert.IsTrue(SmoothTerrain.IsPinned(world, 8, Ground, 5));
            SmoothTerrain.CornerVertex(world, 8, Ground + 1, 5, out Vector3 pinned, out _);
            Assert.AreEqual(new Vector3(8f, Ground + 1, 5f), pinned);

            world.Pins.Unpin(ground);
            Assert.IsTrue(SmoothTerrain.IsPinned(world, 8, Ground, 5), "pinned twice, freed once");
            world.Pins.Unpin(ground);
            Assert.IsFalse(SmoothTerrain.IsPinned(world, 8, Ground, 5));
            Assert.AreEqual(2, changes, "pinned, then free");

            // A pin on air pins nothing.
            world.Pins.Pin(new BlockPos(4, Ground + 3, 4));
            Assert.IsFalse(SmoothTerrain.IsPinned(world, 4, Ground + 3, 4));
        }

        // Construction blocks, water, torches and doors stay out of the smoothing: over a plank floor
        // things stand at the block's top.
        [Test]
        public void ConstructionAndWater_AreNotSmoothed()
        {
            Assert.IsTrue(SmoothTerrain.IsSmooth(Blocks.Grass));
            Assert.IsTrue(SmoothTerrain.IsSmooth(Ores.Make(Blocks.Granite, OreKind.Iron)));
            Assert.IsFalse(SmoothTerrain.IsSmooth(Blocks.FirstPlanks));
            Assert.IsFalse(SmoothTerrain.IsSmooth(Blocks.FirstLog));
            Assert.IsFalse(SmoothTerrain.IsSmooth(Blocks.Water));
            Assert.IsFalse(SmoothTerrain.IsSmooth(Blocks.Torch));
            Assert.IsFalse(SmoothTerrain.IsSmooth(Blocks.Air));

            World world = FlatGenerator.CreateWorld();
            world.SetBlock(5, Ground, 5, Blocks.FirstPlanks);
            Assert.AreEqual(Ground + 1, SmoothTerrain.HeightAt(world, 5, 5, Ground + 1, 0.5f, 0.5f));

            // The plank block counts as nothing for the ground around it: its corners stay flat.
            SmoothTerrain.CornerVertex(world, 5, Ground, 5, out Vector3 position, out _);
            Assert.AreEqual(new Vector3(5f, Ground, 5f), position);
        }

        // A corner shows the ground's top: grass at a cliff's upper edge, the rock under it lower down;
        // an ore is never a corner's material (its own block shows it).
        [Test]
        public void ACorner_ShowsTheTopOfTheGround()
        {
            World world = FlatGenerator.CreateWorld();
            for (int z = 0; z < world.SizeZ; z++)
            {
                for (int x = 0; x < 8; x++)
                {
                    world.SetBlock(x, Ground - 1, z, Blocks.Grass);
                    world.SetBlock(x, Ground - 2, z, Blocks.Dirt);
                }

                for (int x = 8; x < world.SizeX; x++)
                {
                    for (int y = Ground - 4; y < Ground; y++) world.SetBlock(x, y, z, Blocks.Air);
                }
            }

            world.SetBlock(7, Ground - 3, 5, Ores.Make(Blocks.Granite, OreKind.Gold));

            Assert.AreEqual(SmoothTerrain.MaterialOf(Blocks.Grass), SmoothTerrain.MaterialAt(world, 8, Ground, 5), "the cliff's top edge");
            Assert.AreEqual(SmoothTerrain.MaterialOf(Blocks.Grass), SmoothTerrain.MaterialAt(world, 8, Ground - 1, 5), "the grass block's lower edge");
            Assert.AreEqual(SmoothTerrain.MaterialOf(Blocks.Dirt), SmoothTerrain.MaterialAt(world, 8, Ground - 2, 5), "under the grass");
            Assert.AreEqual(SmoothTerrain.MaterialOf(Blocks.Granite), SmoothTerrain.MaterialAt(world, 8, Ground - 3, 5), "the rock, not its ore");
            Assert.AreEqual(Blocks.Granite, SmoothTerrain.MaterialOf(Ores.Make(Blocks.Granite, OreKind.Gold)));
        }
    }
}

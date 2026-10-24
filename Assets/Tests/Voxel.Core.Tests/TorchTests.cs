using System.Collections.Generic;
using System.Numerics;
using NUnit.Framework;

namespace Voxel.Core.Tests
{
    public class TorchTests
    {
        private const int Ground = FlatGenerator.GroundHeight;

        private static readonly Vector3 Eyes = new Vector3(8.5f, Ground + PlayerRules.EyeHeight, 8.5f);

        // 32 × 32 × 32 blocks of flat ground, solid below Ground.
        private static World CreateWorld()
        {
            var world = new World(2, 2, 2);
            world.Generate(new FlatGenerator(), 0);
            return world;
        }

        private static void Set(World world, BlockLight light, int x, int y, int z, ushort block)
        {
            world.SetBlock(x, y, z, block);
            light.Update(new BlockPos(x, y, z), new HashSet<Chunk>());
        }

        private static ushort Standing => Blocks.WithOrientation(Blocks.Torch, Torches.Standing);

        [Test]
        public void ATorch_IsNeitherSolidNorOpaque_AndIsTakenDownByHand()
        {
            Assert.IsFalse(Blocks.IsSolid(Standing));
            Assert.IsFalse(Blocks.IsOpaque(Standing));
            Assert.IsTrue(Blocks.IsBreakable(Standing));
            Assert.IsFalse(Blocks.IsReplaceable(Standing), "a block is not placed over it");
            Assert.AreEqual(Items.None, ToolRules.ToBreak(Standing));
            Assert.AreEqual(1, MiningRules.HitsToBreak(Standing));
            Assert.AreEqual((int)Blocks.Torch, Items.DropOf(Blocks.WithOrientation(Blocks.Torch, Torches.OnNorth)), "it gives a torch, whichever way it held");
            Assert.IsTrue(Blocks.IsValid(Blocks.WithOrientation(Blocks.Torch, Torches.LastOrientation)));
            Assert.IsFalse(Blocks.IsValid(Blocks.WithOrientation(Blocks.Torch, Torches.LastOrientation + 1)));
            Assert.IsFalse(Blocks.IsValid(Blocks.WithOrientation(Blocks.Stone, 1)), "only torches are oriented");
        }

        [Test]
        public void TheFaceAimedAt_SaysHowATorchHolds()
        {
            Assert.IsTrue(Torches.TryOrient(new BlockPos(0, 1, 0), out ushort onTop));
            Assert.AreEqual(Torches.Standing, Blocks.OrientationOf(onTop));
            Assert.IsTrue(Torches.TryOrient(new BlockPos(-1, 0, 0), out ushort onSide));
            Assert.AreEqual(new BlockPos(5, 5, 5), Torches.SupportOf(new BlockPos(4, 5, 5), onSide), "it hangs on the block whose west face was aimed at");
            Assert.IsFalse(Torches.TryOrient(new BlockPos(0, -1, 0), out _), "not under a block");
        }

        [Test]
        public void ATorch_IsPlacedInTheAir_OnlyWhereSomethingHoldsIt()
        {
            World world = FlatGenerator.CreateWorld();
            var onGround = new BlockPos(8, Ground, 10);
            var inTheAir = new BlockPos(8, Ground + 1, 10);

            Assert.IsTrue(BlockEditRules.IsAllowed(world, Eyes, onGround, Standing));
            Assert.IsFalse(BlockEditRules.IsAllowed(world, Eyes, inTheAir, Standing), "nothing under it");

            world.SetBlock(9, Ground + 1, 10, Blocks.Stone);
            Torches.TryOrient(new BlockPos(-1, 0, 0), out ushort hanging);
            Assert.IsTrue(BlockEditRules.IsAllowed(world, Eyes, inTheAir, hanging), "hanging on the stone beside it");

            world.SetBlock(onGround.X, onGround.Y, onGround.Z, Standing);
            Assert.IsFalse(BlockEditRules.IsAllowed(world, Eyes, onGround, Blocks.Stone), "not over a torch");
            Assert.IsTrue(BlockEditRules.IsAllowed(world, Eyes, onGround, Blocks.Air), "taken down");
        }

        [Test]
        public void TheTorchesABlockHolds_AreFound()
        {
            World world = FlatGenerator.CreateWorld();
            var support = new BlockPos(8, Ground - 1, 10);
            world.SetBlock(8, Ground, 10, Standing);
            Torches.TryOrient(new BlockPos(0, 0, 1), out ushort onSouthFace);
            world.SetBlock(8, Ground - 1, 11, Blocks.Air);
            world.SetBlock(8, Ground - 1, 11, onSouthFace);
            world.SetBlock(9, Ground, 10, Standing);

            var held = new List<BlockPos>();
            Torches.FindHeldBy(world, support, held);

            CollectionAssert.AreEquivalent(new[] { new BlockPos(8, Ground, 10), new BlockPos(8, Ground - 1, 11) }, held,
                "the one standing on it and the one hanging on its side, not the one on its neighbour");
        }

        [Test]
        public void TorchLight_FallsByOneLevelPerBlock_AndStopsAtWalls()
        {
            World world = CreateWorld();
            var light = new BlockLight(world);
            Set(world, light, 10, Ground, 10, Standing);

            Assert.AreEqual(Blocks.TorchLight, light.Get(10, Ground, 10));
            Assert.AreEqual(Blocks.TorchLight - 1, light.Get(11, Ground, 10));
            Assert.AreEqual(Blocks.TorchLight - 5, light.Get(13, Ground + 2, 10));
            Assert.AreEqual(0, light.Get(10, Ground - 1, 10), "the ground holds none");
            Assert.AreEqual(0, light.Get(10 + Blocks.TorchLight, Ground, 10), "out of its reach");
            Assert.AreEqual(1, light.Count);

            // A wall across the world beside it, from the ground to the top.
            for (int y = Ground; y < 32; y++)
            {
                for (int z = 0; z < 32; z++)
                {
                    Set(world, light, 12, y, z, Blocks.Stone);
                }
            }

            Assert.AreEqual(0, light.Get(13, Ground, 10), "nothing goes through a wall that closes the world");
            Assert.AreEqual(Blocks.TorchLight - 1, light.Get(11, Ground, 10));
        }

        [Test]
        public void TakingATorchDown_LeavesItDark()
        {
            World world = CreateWorld();
            var light = new BlockLight(world);
            Set(world, light, 10, Ground, 10, Standing);
            Set(world, light, 10, Ground, 10, Blocks.Air);

            Assert.AreEqual(0, light.Count);
            for (int x = 0; x < 32; x++)
            {
                Assert.AreEqual(0, light.Get(x, Ground, 10));
            }
        }

        [Test]
        public void Updates_GiveTheSameLevelsAsComputingAgain()
        {
            World world = CreateWorld();
            var light = new BlockLight(world);
            Set(world, light, 10, Ground, 10, Standing);
            Set(world, light, 14, Ground, 12, Standing);
            Set(world, light, 12, Ground, 11, Blocks.Stone);
            Set(world, light, 12, Ground + 1, 11, Blocks.Stone);
            Set(world, light, 10, Ground, 10, Blocks.Air);
            Set(world, light, 12, Ground, 11, Blocks.Air);
            Set(world, light, 11, Ground - 1, 12, Blocks.Air);

            var fresh = new BlockLight(world);
            for (int y = 0; y < 32; y++)
            {
                for (int z = 0; z < 32; z++)
                {
                    for (int x = 0; x < 32; x++)
                    {
                        Assert.AreEqual(fresh.Get(x, y, z), light.Get(x, y, z), $"at {x}, {y}, {z}");
                    }
                }
            }
        }

        [Test]
        public void TheTopOfAColumn_LeavesTorchesOut()
        {
            World world = FlatGenerator.CreateWorld();
            world.SetBlock(8, Ground, 8, Standing);

            Assert.AreEqual(Ground, world.GetTopHeight(8, 8));
        }

        [Test]
        public void Torches_WeighInsteadOfCountingAsBlocks()
        {
            Assert.IsFalse(CarryLimits.CountsAsBlock(Blocks.Torch));
            Assert.AreEqual(ItemWeights.Torch, new ItemWeights(new float[0]).Of(Blocks.Torch));
            Assert.IsTrue(StorageRules.AcceptsInChest(Blocks.Torch));
        }
    }
}

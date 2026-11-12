using System.IO;
using System.Numerics;
using NUnit.Framework;

namespace Voxel.Core.Tests
{
    public class FallenTreesTests
    {
        private static FallenSegment[] Logs(int count)
        {
            var logs = new FallenSegment[count];
            for (int i = 0; i < count; i++) logs[i] = new FallenSegment(new Vector3(10.5f + i, 20f, 5.5f), 1.5f);
            return logs;
        }

        [Test]
        public void Branches_GoByTheWood()
        {
            Assert.AreEqual(0, FallenTreeRules.BranchesOf(0));
            Assert.AreEqual(1, FallenTreeRules.BranchesOf(1));
            Assert.AreEqual(1, FallenTreeRules.BranchesOf(2));
            Assert.AreEqual(2, FallenTreeRules.BranchesOf(4), "an adult oak");
            Assert.AreEqual(4, FallenTreeRules.BranchesOf(8));
            Assert.AreEqual(8, MiningRules.HitsToFell(4) , "two hits a wood");
        }

        [Test]
        public void ATree_IsPrunedFirst_ThenBuckedFromItsTop()
        {
            var trees = new FallenTrees();
            FallenTree tree = trees.Add(0, 5, 10, 20, 5, 1.5f, 2, Logs(3));
            Assert.AreEqual(FallenTreeRules.HitsPerBranch, FallenTreeRules.HitsFor(tree));

            Assert.IsFalse(trees.Buck(tree.Id, out _), "its branches come off first");
            Assert.IsTrue(trees.Prune(tree.Id));
            Assert.IsTrue(trees.Prune(tree.Id));
            Assert.IsFalse(trees.Prune(tree.Id), "no branch left");
            Assert.IsTrue(tree.IsPruned);
            Assert.AreEqual(FallenTreeRules.HitsPerLog, FallenTreeRules.HitsFor(tree));

            Assert.IsTrue(trees.Buck(tree.Id, out FallenSegment top));
            Assert.AreEqual(12.5f, top.Position.X, "the top log first");
            Assert.IsTrue(trees.Buck(tree.Id, out _));
            Assert.IsTrue(trees.Buck(tree.Id, out FallenSegment foot));
            Assert.AreEqual(10.5f, foot.Position.X);
            Assert.IsFalse(trees.TryGet(tree.Id, out _), "gone with its last log");
        }

        [Test]
        public void Trees_AreFoundByColumn()
        {
            var trees = new FallenTrees();
            FallenTree tree = trees.Add(0, 5, 9, 20, 5, 1.5f, 1, Logs(3));
            var found = new System.Collections.Generic.List<int>();

            trees.InColumn(11, 5, found);
            CollectionAssert.AreEqual(new[] { tree.Id }, found, "a log lies there");
            trees.InColumn(9, 5, found);
            CollectionAssert.AreEqual(new[] { tree.Id }, found, "its foot");
            trees.InColumn(11, 6, found);
            Assert.IsEmpty(found);
        }

        [Test]
        public void Trees_SurviveASaveAndReload()
        {
            var trees = new FallenTrees();
            trees.Add(3, 4, 1, 2, 3, 0.5f, 2, Logs(4));
            FallenTree second = trees.Add(7, 5, 4, 5, 6, -1f, 0, Logs(1));
            trees.Remove(1);

            var stream = new MemoryStream();
            trees.Write(stream);
            stream.Position = 0;
            FallenTrees loaded = FallenTrees.Read(stream);

            Assert.AreEqual(1, loaded.Count);
            Assert.IsTrue(loaded.TryGet(second.Id, out FallenTree read));
            Assert.AreEqual(7, read.Species);
            Assert.AreEqual(5, read.Stage);
            Assert.AreEqual(-1f, read.Yaw);
            Assert.AreEqual(1, read.Segments.Count);
            Assert.AreEqual(second.Segments[0].Position, read.Segments[0].Position);
            Assert.Greater(loaded.Add(0, 1, 0, 0, 0, 0f, 1, Logs(1)).Id, second.Id, "ids are never given twice");
        }

        [Test]
        public void ADamagedSave_IsRefused()
        {
            var stream = new MemoryStream(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
            Assert.Throws<InvalidDataException>(() => FallenTrees.Read(stream));
        }
    }
}

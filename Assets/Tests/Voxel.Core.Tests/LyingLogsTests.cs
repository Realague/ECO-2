using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using NUnit.Framework;

namespace Voxel.Core.Tests
{
    public class LyingLogsTests
    {
        [Test]
        public void Logs_AreAdded_Moved_AndPickedUp_ByIdentifier()
        {
            var logs = new LyingLogs();
            LyingLog first = logs.Add(new Vector3(1.5f, 8f, 1.5f), 0.3f, 2);
            LyingLog second = logs.Add(new Vector3(4.5f, 8f, 1.5f), 0f, 2);
            var inColumn = new List<int>();

            logs.InColumn(1, 1, inColumn);
            CollectionAssert.AreEqual(new[] { first.Id }, inColumn);

            Assert.IsTrue(logs.Move(first.Id, new Vector3(2.5f, 9f, 1.5f)));
            logs.InColumn(1, 1, inColumn);
            Assert.IsEmpty(inColumn, "moved out of its column");
            logs.InColumn(2, 1, inColumn);
            CollectionAssert.AreEqual(new[] { first.Id }, inColumn);

            Assert.IsTrue(logs.Remove(second.Id));
            Assert.IsFalse(logs.Remove(second.Id), "picked up once");
            Assert.AreEqual(1, logs.Count);
        }

        [Test]
        public void WriteThenRead_KeepsTheLogs_AndNeverReusesAnIdentifier()
        {
            var logs = new LyingLogs();
            logs.Add(new Vector3(1.5f, 8f, 1.5f), 0.3f, 2);
            LyingLog last = logs.Add(new Vector3(2.5f, 8f, 1.5f), 1f, 5);
            logs.Remove(last.Id);
            var stream = new MemoryStream();
            logs.Write(stream);
            stream.Position = 0;

            LyingLogs loaded = LyingLogs.Read(stream);

            LyingLog kept = loaded.All.Single();
            Assert.AreEqual(new Vector3(1.5f, 8f, 1.5f), kept.Position);
            Assert.AreEqual(0.3f, kept.Yaw);
            Assert.AreEqual(2, kept.Species);
            Assert.Greater(loaded.Add(Vector3.Zero, 0f, 0).Id, last.Id);
        }

        [Test]
        public void Read_RefusesWhatIsNotALogSave()
        {
            Assert.Throws<InvalidDataException>(() => LyingLogs.Read(new MemoryStream(new byte[16])));
        }

        [Test]
        public void AlongFall_LaysTheLogs_OnTheGround_InTheWayTheTreeFell()
        {
            World world = FlatGenerator.CreateWorld();
            var placed = new List<(Vector3 position, float yaw)>();
            var foot = new Vector3(3.5f, FlatGenerator.GroundHeight, 3.5f);

            LogPlacement.AlongFall(world, foot, Vector3.UnitX, 1f, 5f, 4, placed);

            Assert.AreEqual(4, placed.Count);
            float last = 0f;
            foreach ((Vector3 position, float yaw) in placed)
            {
                Assert.AreEqual(FlatGenerator.GroundHeight, position.Y, 1e-4f, "on the ground");
                Assert.AreEqual(3.5f, position.Z, 1e-4f, "along the fall");
                Assert.Greater(position.X, last);
                last = position.X;
            }
        }

        [Test]
        public void AlongFall_PilesTheLogs_AgainstAWall()
        {
            World world = FlatGenerator.CreateWorld();
            for (int y = FlatGenerator.GroundHeight; y < FlatGenerator.GroundHeight + 3; y++)
            {
                world.SetBlock(new BlockPos(6, y, 3), Blocks.Stone);
            }

            var placed = new List<(Vector3 position, float yaw)>();
            LogPlacement.AlongFall(world, new Vector3(3.5f, FlatGenerator.GroundHeight, 3.5f), Vector3.UnitX, 1f, 6f, 5, placed);

            Assert.AreEqual(5, placed.Count);
            foreach ((Vector3 position, _) in placed)
            {
                Assert.Less(position.X, 6f, "never inside the wall");
            }

            Assert.Greater(placed[4].position.Y, placed[0].position.Y, "piled up");
        }

        [Test]
        public void Settle_DropsALog_WhoseGroundIsDug_AndLiftsOneABlockIsPlacedInto()
        {
            World world = FlatGenerator.CreateWorld();
            var resting = new Vector3(3.5f, FlatGenerator.GroundHeight, 3.5f);
            Assert.IsFalse(LogPlacement.Settle(world, resting, out _), "on the ground, it stays");

            world.SetBlock(new BlockPos(3, FlatGenerator.GroundHeight - 1, 3), Blocks.Air);
            Assert.IsTrue(LogPlacement.Settle(world, resting, out Vector3 dropped));
            Assert.AreEqual(FlatGenerator.GroundHeight - 1, dropped.Y, 1e-4f);

            world.SetBlock(new BlockPos(3, FlatGenerator.GroundHeight - 1, 3), Blocks.Stone);
            world.SetBlock(new BlockPos(3, FlatGenerator.GroundHeight, 3), Blocks.Stone);
            Assert.IsTrue(LogPlacement.Settle(world, resting, out Vector3 lifted));
            Assert.AreEqual(FlatGenerator.GroundHeight + 1, lifted.Y, 1e-4f);
        }
    }
}

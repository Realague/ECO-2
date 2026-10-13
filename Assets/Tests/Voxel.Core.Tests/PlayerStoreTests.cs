using System;
using System.IO;
using System.Numerics;
using NUnit.Framework;

namespace Voxel.Core.Tests
{
    public class PlayerStoreTests
    {
        private string temporaryDirectory;

        [SetUp]
        public void CreateTemporaryDirectory()
        {
            temporaryDirectory = Path.Combine(Path.GetTempPath(), "VoxelCoreTests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporaryDirectory);
        }

        [TearDown]
        public void DeleteTemporaryDirectory()
        {
            Directory.Delete(temporaryDirectory, true);
        }

        [Test]
        public void TryGet_UnknownPlayer_ReturnsFalse()
        {
            var store = new PlayerStore();

            Assert.IsFalse(store.TryGet("nobody", out _));
        }

        [Test]
        public void Set_SamePlayerTwice_KeepsTheLatestState()
        {
            var store = new PlayerStore();
            store.Set("alice", new PlayerState { Position = new Vector3(1f, 2f, 3f), Yaw = 10f });

            store.Set("alice", new PlayerState { Position = new Vector3(4f, 5f, 6f), Yaw = 20f });

            Assert.AreEqual(1, store.Count);
            Assert.IsTrue(store.TryGet("alice", out PlayerState state));
            Assert.AreEqual(new Vector3(4f, 5f, 6f), state.Position);
            Assert.AreEqual(20f, state.Yaw);
        }

        [Test]
        public void WriteThenRead_RestoresEveryPlayer()
        {
            var original = new PlayerStore();
            original.Set("alice", new PlayerState { Position = new Vector3(64.5f, 25.001f, 60.25f), Yaw = 270.5f });
            original.Set("bob", new PlayerState { Position = new Vector3(1f, 2f, 3f), Yaw = 0f });
            var stream = new MemoryStream();
            original.Write(stream);
            stream.Position = 0;

            PlayerStore loaded = PlayerStore.Read(stream);

            Assert.AreEqual(2, loaded.Count);
            Assert.IsTrue(loaded.TryGet("alice", out PlayerState alice));
            Assert.AreEqual(new Vector3(64.5f, 25.001f, 60.25f), alice.Position);
            Assert.AreEqual(270.5f, alice.Yaw);
            Assert.IsTrue(loaded.TryGet("bob", out PlayerState bob));
            Assert.AreEqual(new Vector3(1f, 2f, 3f), bob.Position);
        }

        [Test]
        public void WriteThenRead_RestoresSpecialityAndInventory()
        {
            var original = new PlayerStore();
            PlayerState alice = original.GetOrAdd("alice");
            alice.Speciality = Speciality.Mason;
            alice.Inventory.Add(Blocks.Stone, 12);
            alice.Inventory.Add(Items.Seed(1), 2);
            original.GetOrAdd("bob");
            var stream = new MemoryStream();
            original.Write(stream);
            stream.Position = 0;

            PlayerStore loaded = PlayerStore.Read(stream);

            Assert.IsTrue(loaded.TryGet("alice", out PlayerState loadedAlice));
            Assert.AreEqual(Speciality.Mason, loadedAlice.Speciality);
            Assert.AreEqual(2, loadedAlice.Inventory.DistinctCount);
            Assert.AreEqual(12, loadedAlice.Inventory.Count(Blocks.Stone));
            Assert.AreEqual(2, loadedAlice.Inventory.Count(Items.Seed(1)));
            Assert.IsTrue(loaded.TryGet("bob", out PlayerState loadedBob));
            Assert.AreEqual(Speciality.None, loadedBob.Speciality);
            Assert.AreEqual(0, loadedBob.Inventory.DistinctCount);
        }

        [Test]
        public void GetOrAdd_ReturnsTheSameStateEveryTime()
        {
            var store = new PlayerStore();

            PlayerState first = store.GetOrAdd("alice");
            first.Inventory.Add(Items.Wood, 1);

            Assert.AreSame(first, store.GetOrAdd("alice"));
            Assert.AreEqual(1, store.Count);
        }

        // Une sauvegarde d'avant les inventaires : le joueur garde sa position et repart les mains vides.
        [Test]
        public void Read_SaveWithoutInventory_KeepsThePosition()
        {
            var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
            {
                writer.Write(0x504F4345u);
                writer.Write(1);
                writer.Write(1);
                writer.Write("alice");
                writer.Write(1f);
                writer.Write(2f);
                writer.Write(3f);
                writer.Write(90f);
            }

            stream.Position = 0;
            PlayerStore loaded = PlayerStore.Read(stream);

            Assert.IsTrue(loaded.TryGet("alice", out PlayerState state));
            Assert.AreEqual(new Vector3(1f, 2f, 3f), state.Position);
            Assert.AreEqual(90f, state.Yaw);
            Assert.AreEqual(Speciality.None, state.Speciality);
            Assert.AreEqual(0, state.Inventory.DistinctCount);
        }

        [Test]
        public void Read_UnknownSpeciality_Throws()
        {
            var store = new PlayerStore();
            store.GetOrAdd("alice").Speciality = (Speciality)200;
            var stream = new MemoryStream();
            store.Write(stream);
            stream.Position = 0;

            Assert.Throws<InvalidDataException>(() => PlayerStore.Read(stream));
        }

        [Test]
        public void Read_FileThatIsNotAPlayerSave_Throws()
        {
            Assert.Throws<InvalidDataException>(() => PlayerStore.Read(new MemoryStream(new byte[32])));
        }

        [Test]
        public void Read_TruncatedSave_Throws()
        {
            var store = new PlayerStore();
            store.Set("alice", new PlayerState { Position = new Vector3(1f, 2f, 3f) });
            var stream = new MemoryStream();
            store.Write(stream);
            byte[] data = stream.ToArray();

            Assert.Throws<EndOfStreamException>(() => PlayerStore.Read(new MemoryStream(data, 0, data.Length - 2)));
        }

        [Test]
        public void SaveToFile_OverExistingFile_ThenLoadFromFile_RestoresLatestState()
        {
            string path = Path.Combine(temporaryDirectory, "players.dat");
            var store = new PlayerStore();
            store.Set("alice", new PlayerState { Position = new Vector3(1f, 2f, 3f) });
            store.SaveToFile(path);
            store.Set("alice", new PlayerState { Position = new Vector3(7f, 8f, 9f), Yaw = 45f });

            store.SaveToFile(path);
            PlayerStore loaded = PlayerStore.LoadFromFile(path);

            Assert.IsTrue(loaded.TryGet("alice", out PlayerState state));
            Assert.AreEqual(new Vector3(7f, 8f, 9f), state.Position);
            Assert.AreEqual(45f, state.Yaw);
            Assert.AreEqual(new[] { path }, Directory.GetFiles(temporaryDirectory));
        }
    }
}

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
            alice.Speciality = Speciality.Gardener;
            alice.Inventory.Add(Blocks.Stone, 12);
            alice.Inventory.Add(Items.Seed(1), 2);
            original.GetOrAdd("bob");
            var stream = new MemoryStream();
            original.Write(stream);
            stream.Position = 0;

            PlayerStore loaded = PlayerStore.Read(stream);

            Assert.IsTrue(loaded.TryGet("alice", out PlayerState loadedAlice));
            Assert.AreEqual(Speciality.Gardener, loadedAlice.Speciality);
            Assert.AreEqual(2, loadedAlice.Inventory.DistinctCount);
            Assert.AreEqual(12, loadedAlice.Inventory.Count(Blocks.Stone));
            Assert.AreEqual(2, loadedAlice.Inventory.Count(Items.Seed(1)));
            Assert.IsTrue(loaded.TryGet("bob", out PlayerState loadedBob));
            Assert.AreEqual(Speciality.None, loadedBob.Speciality);
            Assert.AreEqual(0, loadedBob.Inventory.DistinctCount);
        }

        [Test]
        public void WriteThenRead_RestoresTheAppearance_AndKeepsAnUnchosenOneUnchosen()
        {
            var original = new PlayerStore();
            original.GetOrAdd("alice").Appearance = new Appearance(4, Sex.Female, HairStyle.Ponytail);
            original.GetOrAdd("bob");
            var stream = new MemoryStream();
            original.Write(stream);
            stream.Position = 0;

            PlayerStore loaded = PlayerStore.Read(stream);

            Assert.IsTrue(loaded.TryGet("alice", out PlayerState alice));
            Assert.AreEqual(new Appearance(4, Sex.Female, HairStyle.Ponytail), alice.Appearance);
            Assert.IsTrue(loaded.TryGet("bob", out PlayerState bob));
            Assert.IsNull(bob.Appearance);
        }

        [Test]
        public void WriteThenRead_RestoresTheHotbar()
        {
            var original = new PlayerStore();
            PlayerState alice = original.GetOrAdd("alice");
            alice.Hotbar.Set(0, Items.WoodOf(Items.OakSpecies));
            alice.Hotbar.Set(9, Blocks.Stone);
            var stream = new MemoryStream();
            original.Write(stream);
            stream.Position = 0;

            PlayerStore loaded = PlayerStore.Read(stream);

            Assert.IsTrue(loaded.TryGet("alice", out PlayerState state));
            Assert.AreEqual(Items.WoodOf(Items.OakSpecies), state.Hotbar[0]);
            Assert.AreEqual(Blocks.Stone, state.Hotbar[9]);
            Assert.AreEqual(Items.None, state.Hotbar[5]);
        }

        [Test]
        public void WriteThenRead_RestoresTheStomach()
        {
            var original = new PlayerStore();
            PlayerState alice = original.GetOrAdd("alice");
            alice.Stomach.Burn(1000f);
            alice.Stomach.Eat(new Nutrition(200f, 2f, 10f, 1f, 2f));
            alice.ReceivedTools = true;
            original.GetOrAdd("bob");
            var stream = new MemoryStream();
            original.Write(stream);
            stream.Position = 0;

            PlayerStore loaded = PlayerStore.Read(stream);

            Assert.IsTrue(loaded.TryGet("alice", out PlayerState state));
            Assert.AreEqual(alice.Stomach.Calories, state.Stomach.Calories);
            Assert.IsTrue(state.ReceivedTools);
            Assert.IsTrue(loaded.TryGet("bob", out PlayerState bob));
            Assert.IsFalse(bob.ReceivedTools);
            for (int i = 0; i < Nutrition.NutrientCount; i++)
            {
                Assert.AreEqual(alice.Stomach.Nutrient(i), state.Stomach.Nutrient(i));
            }
        }

        [Test]
        public void WriteThenRead_RestoresTheNameAndTheStorageGift()
        {
            var original = new PlayerStore();
            PlayerState alice = original.GetOrAdd("alice");
            alice.Name = "Alice-Ä 2";
            alice.ReceivedStorage = true;
            original.GetOrAdd("bob");
            var stream = new MemoryStream();
            original.Write(stream);
            stream.Position = 0;

            PlayerStore loaded = PlayerStore.Read(stream);

            Assert.IsTrue(loaded.TryGet("alice", out PlayerState state));
            Assert.AreEqual("Alice-Ä 2", state.Name);
            Assert.IsTrue(state.ReceivedStorage);
            Assert.IsTrue(loaded.TryGet("bob", out PlayerState bob));
            Assert.AreEqual("", bob.Name);
            Assert.IsFalse(bob.ReceivedStorage);
            Assert.IsTrue(loaded.IsNameTaken("ALICE-ä 2", "bob"), "names are unique whatever the case");
            Assert.IsFalse(loaded.IsNameTaken("Alice-Ä 2", "alice"), "one's own name is not taken");
        }

        // A save from before names and storage (format 6): the tools given are kept, the name is to be
        // chosen and the chest and stockpile to be given.
        [Test]
        public void Read_SaveWithoutName_KeepsTheToolsAndLeavesTheNameToChoose()
        {
            var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
            {
                writer.Write(0x504F4345u);
                writer.Write(6);
                writer.Write(1);
                writer.Write("alice");
                writer.Write(1f);
                writer.Write(2f);
                writer.Write(3f);
                writer.Write(90f);
                writer.Write((byte)Speciality.None);
                writer.Write(0);
                writer.Write(Appearance.NotChosen);
                for (int slot = 0; slot < Hotbar.SlotCount; slot++) writer.Write(Items.None);
                new Stomach().Write(writer);
                writer.Write(true);
            }

            stream.Position = 0;
            PlayerStore loaded = PlayerStore.Read(stream);

            Assert.IsTrue(loaded.TryGet("alice", out PlayerState state));
            Assert.IsTrue(state.ReceivedTools);
            Assert.AreEqual("", state.Name);
            Assert.IsFalse(state.ReceivedStorage);
        }

        // A save from before knives (format 8): the torches given are kept, the knife is to be given.
        [Test]
        public void Read_SaveWithoutKnife_KeepsTheTorchesAndLeavesTheKnifeToGive()
        {
            var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
            {
                writer.Write(0x504F4345u);
                writer.Write(8);
                writer.Write(1);
                writer.Write("alice");
                writer.Write(1f);
                writer.Write(2f);
                writer.Write(3f);
                writer.Write(90f);
                writer.Write((byte)Speciality.None);
                writer.Write(0);
                writer.Write(Appearance.NotChosen);
                for (int slot = 0; slot < Hotbar.SlotCount; slot++) writer.Write(Items.None);
                new Stomach().Write(writer);
                writer.Write(true);
                writer.Write("Alice");
                writer.Write(true);
                writer.Write(true);
            }

            stream.Position = 0;
            PlayerStore loaded = PlayerStore.Read(stream);

            Assert.IsTrue(loaded.TryGet("alice", out PlayerState state));
            Assert.IsTrue(state.ReceivedTorches);
            Assert.IsFalse(state.ReceivedKnife);

            state.ReceivedKnife = true;
            var again = new MemoryStream();
            loaded.Write(again);
            again.Position = 0;
            Assert.IsTrue(PlayerStore.Read(again).TryGet("alice", out PlayerState read));
            Assert.IsTrue(read.ReceivedKnife);
            Assert.IsTrue(read.ReceivedTorches);
        }

        // A save from before hammers (format 9): the knife is kept, the hammer is left to give.
        [Test]
        public void Read_SaveWithoutHammer_KeepsTheKnifeAndLeavesTheHammerToGive()
        {
            var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
            {
                writer.Write(0x504F4345u);
                writer.Write(9);
                writer.Write(1);
                writer.Write("alice");
                writer.Write(1f);
                writer.Write(2f);
                writer.Write(3f);
                writer.Write(90f);
                writer.Write((byte)Speciality.None);
                writer.Write(0);
                writer.Write(Appearance.NotChosen);
                for (int slot = 0; slot < Hotbar.SlotCount; slot++) writer.Write(Items.None);
                new Stomach().Write(writer);
                writer.Write(true);
                writer.Write("Alice");
                writer.Write(true);
                writer.Write(true);
                writer.Write(true);
            }

            stream.Position = 0;
            PlayerStore loaded = PlayerStore.Read(stream);

            Assert.IsTrue(loaded.TryGet("alice", out PlayerState state));
            Assert.IsTrue(state.ReceivedKnife);
            Assert.IsFalse(state.ReceivedHammer);

            state.ReceivedHammer = true;
            var again = new MemoryStream();
            loaded.Write(again);
            again.Position = 0;
            Assert.IsTrue(PlayerStore.Read(again).TryGet("alice", out PlayerState read));
            Assert.IsTrue(read.ReceivedHammer);
            Assert.IsTrue(read.ReceivedKnife);
        }

        // A save from before creative mode (format 10): the hammer is kept, the player plays in survival;
        // creative mode is saved from then on.
        [Test]
        public void Read_SaveWithoutCreative_KeepsTheHammerAndPlaysInSurvival()
        {
            var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
            {
                writer.Write(0x504F4345u);
                writer.Write(10);
                writer.Write(1);
                writer.Write("alice");
                writer.Write(1f);
                writer.Write(2f);
                writer.Write(3f);
                writer.Write(90f);
                writer.Write((byte)Speciality.None);
                writer.Write(0);
                writer.Write(Appearance.NotChosen);
                for (int slot = 0; slot < Hotbar.SlotCount; slot++) writer.Write(Items.None);
                new Stomach().Write(writer);
                writer.Write(true);
                writer.Write("Alice");
                writer.Write(true);
                writer.Write(true);
                writer.Write(true);
                writer.Write(true);
            }

            stream.Position = 0;
            PlayerStore loaded = PlayerStore.Read(stream);

            Assert.IsTrue(loaded.TryGet("alice", out PlayerState state));
            Assert.IsTrue(state.ReceivedHammer);
            Assert.IsFalse(state.Creative);

            state.Creative = true;
            var again = new MemoryStream();
            loaded.Write(again);
            again.Position = 0;
            Assert.IsTrue(PlayerStore.Read(again).TryGet("alice", out PlayerState read));
            Assert.IsTrue(read.Creative);
            Assert.IsTrue(read.ReceivedHammer);
        }

        // A save from before hunger (format 4): everything else is kept, the stomach starts half full.
        [Test]
        public void Read_SaveWithoutStomach_KeepsTheRestWithAStartingStomach()
        {
            var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
            {
                writer.Write(0x504F4345u);
                writer.Write(4);
                writer.Write(1);
                writer.Write("alice");
                writer.Write(1f);
                writer.Write(2f);
                writer.Write(3f);
                writer.Write(90f);
                writer.Write((byte)Speciality.Gardener);
                writer.Write(1);
                writer.Write((int)Blocks.Stone);
                writer.Write(5);
                writer.Write(new Appearance(4, Sex.Female, HairStyle.Ponytail).Encode());
                writer.Write((int)Blocks.Stone);
                for (int slot = 1; slot < Hotbar.SlotCount; slot++) writer.Write(Items.None);
            }

            stream.Position = 0;
            PlayerStore loaded = PlayerStore.Read(stream);

            Assert.IsTrue(loaded.TryGet("alice", out PlayerState state));
            Assert.AreEqual(5, state.Inventory.Count(Blocks.Stone));
            Assert.AreEqual((int)Blocks.Stone, state.Hotbar[0]);
            Assert.AreEqual(Stomach.StartingCalories, state.Stomach.Calories);
            Assert.AreEqual(0.25f, state.Stomach.Share(0), 1e-6f);
            Assert.IsFalse(state.ReceivedTools, "they get their tools at their next connection");
        }

        // A save from before the hotbar (format 3): everything else is kept, the hotbar starts empty.
        [Test]
        public void Read_SaveWithoutHotbar_KeepsTheRestWithAnEmptyHotbar()
        {
            var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
            {
                writer.Write(0x504F4345u);
                writer.Write(3);
                writer.Write(1);
                writer.Write("alice");
                writer.Write(1f);
                writer.Write(2f);
                writer.Write(3f);
                writer.Write(90f);
                writer.Write((byte)Speciality.Gardener);
                writer.Write(1);
                writer.Write((int)Blocks.Stone);
                writer.Write(5);
                writer.Write(new Appearance(4, Sex.Female, HairStyle.Ponytail).Encode());
            }

            stream.Position = 0;
            PlayerStore loaded = PlayerStore.Read(stream);

            Assert.IsTrue(loaded.TryGet("alice", out PlayerState state));
            Assert.AreEqual(5, state.Inventory.Count(Blocks.Stone));
            Assert.AreEqual(new Appearance(4, Sex.Female, HairStyle.Ponytail), state.Appearance);
            for (int slot = 0; slot < Hotbar.SlotCount; slot++)
            {
                Assert.AreEqual(Items.None, state.Hotbar[slot]);
            }
        }

        // A save from before appearances: the player keeps everything else and chooses one at the next connection.
        [Test]
        public void Read_SaveWithoutAppearance_KeepsTheRestAndLeavesTheAppearanceUnchosen()
        {
            var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
            {
                writer.Write(0x504F4345u);
                writer.Write(2);
                writer.Write(1);
                writer.Write("alice");
                writer.Write(1f);
                writer.Write(2f);
                writer.Write(3f);
                writer.Write(90f);
                writer.Write((byte)Speciality.Gardener);
                writer.Write(1);
                writer.Write((int)Blocks.Stone);
                writer.Write(5);
            }

            stream.Position = 0;
            PlayerStore loaded = PlayerStore.Read(stream);

            Assert.IsTrue(loaded.TryGet("alice", out PlayerState state));
            Assert.AreEqual(Speciality.Gardener, state.Speciality);
            Assert.AreEqual(5, state.Inventory.Count(Blocks.Stone));
            Assert.IsNull(state.Appearance);
        }

        // A save from when crafting existed: the Mason chooses again, and the planks are gone, the rest is kept.
        [Test]
        public void Read_SaveFromTheCraftingDays_DropsTheRemovedSpecialityAndItems()
        {
            var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
            {
                writer.Write(0x504F4345u);
                writer.Write(2);
                writer.Write(1);
                writer.Write("alice");
                writer.Write(1f);
                writer.Write(2f);
                writer.Write(3f);
                writer.Write(90f);
                writer.Write((byte)Speciality.Mason);
                writer.Write(2);
                writer.Write(4);
                writer.Write(7);
                writer.Write((int)Blocks.Stone);
                writer.Write(5);
            }

            stream.Position = 0;
            PlayerStore loaded = PlayerStore.Read(stream);

            Assert.IsTrue(loaded.TryGet("alice", out PlayerState state));
            Assert.AreEqual(Speciality.None, state.Speciality);
            Assert.AreEqual(1, state.Inventory.DistinctCount);
            Assert.AreEqual(5, state.Inventory.Count(Blocks.Stone));
        }

        [Test]
        public void Read_InvalidAppearance_Throws()
        {
            var store = new PlayerStore();
            store.GetOrAdd("alice");
            var stream = new MemoryStream();
            store.Write(stream);
            byte[] data = stream.ToArray();

            // The appearance code comes just before the hotbar (10 ints), the stomach (5 floats) and
            // the tools flag (1 byte), low byte first: a chosen code with hair style 7, which does not exist.
            int appearance = data.Length - Hotbar.SlotCount * 4 - (1 + Nutrition.NutrientCount) * 4 - 1 - 2;
            data[appearance] = 0x70;
            data[appearance + 1] = 0x80;

            Assert.Throws<InvalidDataException>(() => PlayerStore.Read(new MemoryStream(data)));
        }

        [Test]
        public void GetOrAdd_ReturnsTheSameStateEveryTime()
        {
            var store = new PlayerStore();

            PlayerState first = store.GetOrAdd("alice");
            first.Inventory.Add(Items.WoodOf(Items.OakSpecies), 1);

            Assert.AreSame(first, store.GetOrAdd("alice"));
            Assert.AreEqual(1, store.Count);
        }

        // A save from before inventories: the player keeps their position and starts empty-handed.
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

using System;
using System.IO;
using NUnit.Framework;

namespace Voxel.Core.Tests
{
    public class WorldSlotsTests
    {
        private const int GeneratorVersion = 1;

        private string data;

        [SetUp]
        public void CreateDataFolder()
        {
            data = Path.Combine(Path.GetTempPath(), "WorldSlotsTests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(data);
        }

        [TearDown]
        public void DeleteDataFolder()
        {
            if (Directory.Exists(data)) Directory.Delete(data, true);
        }

        private static void SaveWorld(string directory, int seed, int generatorVersion = GeneratorVersion)
        {
            var world = new World(1, 1, 2);
            world.Generate(new FlatGenerator(generatorVersion), seed);
            WorldSave.SaveToFile(Path.Combine(directory, WorldSlots.WorldFileName), world);
        }

        [Test]
        public void List_IsEmpty_WithoutAnyWorld()
        {
            Assert.IsEmpty(WorldSlots.List(data, GeneratorVersion));
        }

        [Test]
        public void List_ReadsEachSave_TheMostRecentFirst()
        {
            string older = WorldSlots.CreateDirectory(data, "Older");
            SaveWorld(older, 12);
            File.SetLastWriteTimeUtc(Path.Combine(older, WorldSlots.WorldFileName), new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            string newer = WorldSlots.CreateDirectory(data, "Newer");
            SaveWorld(newer, -7);

            var slots = WorldSlots.List(data, GeneratorVersion);

            Assert.AreEqual(2, slots.Count);
            Assert.AreEqual("Newer", slots[0].Name);
            Assert.AreEqual(-7, slots[0].Seed);
            Assert.IsTrue(slots[0].HasSave);
            Assert.AreEqual(1, slots[0].ChunksX);
            Assert.AreEqual(2, slots[0].ChunksZ);
            Assert.AreEqual("Older", slots[1].Name);
            Assert.AreEqual(12, slots[1].Seed);
            Assert.AreEqual(older, slots[1].Directory);
        }

        [Test]
        public void List_IncludesTheWorldSavedInTheDataFolderItself()
        {
            SaveWorld(data, 5);

            var slots = WorldSlots.List(data, GeneratorVersion);

            Assert.AreEqual(1, slots.Count);
            Assert.AreEqual(WorldSlots.LegacyName, slots[0].Name);
            Assert.AreEqual(data, slots[0].Directory);
            Assert.AreEqual(5, slots[0].Seed);
        }

        [Test]
        public void List_FlagsAnOutdatedSave_AndKeepsAFolderWithoutSave()
        {
            SaveWorld(WorldSlots.CreateDirectory(data, "Old"), 1, generatorVersion: GeneratorVersion - 1);
            WorldSlots.CreateDirectory(data, "Empty");
            File.WriteAllText(Path.Combine(WorldSlots.CreateDirectory(data, "Broken"), WorldSlots.WorldFileName), "not a save");

            var slots = WorldSlots.List(data, GeneratorVersion);

            Assert.AreEqual(3, slots.Count);
            foreach (WorldSlots.Slot slot in slots)
            {
                if (slot.Name == "Old") Assert.IsTrue(slot.HasSave && slot.IsOutdated);
                else Assert.IsFalse(slot.HasSave, slot.Name);
            }
        }

        [Test]
        public void CreateDirectory_NumbersANameAlreadyTaken()
        {
            string first = WorldSlots.CreateDirectory(data, "Valley");
            string second = WorldSlots.CreateDirectory(data, "Valley");
            SaveWorld(data, 3);
            string legacy = WorldSlots.CreateDirectory(data, WorldSlots.LegacyName);

            Assert.AreEqual("Valley", Path.GetFileName(first));
            Assert.AreEqual("Valley (2)", Path.GetFileName(second));
            Assert.AreEqual(WorldSlots.LegacyName + " (2)", Path.GetFileName(legacy), "the data folder's own world already shows as World");
            Assert.IsTrue(Directory.Exists(second));
        }

        [TestCase("  My   world  ", "My world")]
        [TestCase("a/b\\c:d*e?f\"g<h>i|j", "abcdefghij")]
        [TestCase("...", WorldSlots.DefaultName)]
        [TestCase("", WorldSlots.DefaultName)]
        [TestCase(null, WorldSlots.DefaultName)]
        [TestCase("con", WorldSlots.DefaultName)]
        [TestCase("COM3.txt", WorldSlots.DefaultName)]
        [TestCase("Island.", "Island")]
        public void FolderNameOf_KeepsWhatAFolderNameCanHold(string typed, string expected)
        {
            Assert.AreEqual(expected, WorldSlots.FolderNameOf(typed));
        }

        [Test]
        public void FolderNameOf_IsCutToTheMaximumLength()
        {
            Assert.AreEqual(WorldSlots.MaxNameLength, WorldSlots.FolderNameOf(new string('x', 100)).Length);
        }

        [Test]
        public void SeedOf_ReadsNumbers_HashesWords_AndLeavesNothingToChance()
        {
            Assert.AreEqual(1234, WorldSlots.SeedOf(" 1234 ", 99));
            Assert.AreEqual(-5, WorldSlots.SeedOf("-5", 99));
            Assert.AreEqual(99, WorldSlots.SeedOf("   ", 99));
            Assert.AreEqual(99, WorldSlots.SeedOf(null, 99));
            Assert.AreEqual(WorldSlots.SeedOf("forest", 0), WorldSlots.SeedOf("forest", 1), "the same word, the same seed");
            Assert.AreNotEqual(WorldSlots.SeedOf("forest", 0), WorldSlots.SeedOf("desert", 0));

            // Pinned: a word must give the same world on every machine and in every version.
            Assert.AreEqual(unchecked((int)0xE40C292C), WorldSlots.SeedOf("a", 0));
        }

        // One layer of stone: enough to be saved.
        private sealed class FlatGenerator : IChunkGenerator
        {
            public FlatGenerator(int version)
            {
                Version = version;
            }

            public int Version { get; }

            public void Prepare(int seed, int sizeX, int sizeZ)
            {
            }

            public void Generate(Chunk chunk, int seed)
            {
                for (int z = 0; z < Chunk.Size; z++)
                {
                    for (int x = 0; x < Chunk.Size; x++)
                    {
                        chunk.Set(x, 0, z, Blocks.Stone);
                    }
                }
            }
        }
    }
}

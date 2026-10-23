using System.IO;
using NUnit.Framework;

namespace Voxel.Core.Tests
{
    public class StorageTests
    {
        // Crop 0 weighs 0.2 kg, crop 1 one kg.
        private static readonly ItemWeights Weights = new ItemWeights(new[] { 0.2f, 1f });

        [Test]
        public void Chest_HoldsItemsButNotBlocks()
        {
            Assert.IsTrue(StorageRules.AcceptsInChest(Items.Arrow));
            Assert.IsTrue(StorageRules.AcceptsInChest(Items.Axe));
            Assert.IsTrue(StorageRules.AcceptsInChest(Items.Seed(3)));
            Assert.IsTrue(StorageRules.AcceptsInChest(Items.Chest), "a chest can hold another, folded");
            Assert.IsFalse(StorageRules.AcceptsInChest(Blocks.Dirt));
            Assert.IsFalse(StorageRules.AcceptsInChest(Items.WoodOf(Items.OakSpecies)), "wood goes with the blocks");
            Assert.IsTrue(StorageRules.AcceptsInStockpile(Items.WoodOf(Items.OakSpecies)));
            Assert.IsTrue(StorageRules.AcceptsInStockpile(Blocks.Granite));
            Assert.IsFalse(StorageRules.AcceptsInStockpile(Items.Arrow));
        }

        [Test]
        public void ChestRoom_IsByWeight_AndTakesWhatExactlyFits()
        {
            var contents = new Inventory();
            contents.Add(Items.Produce(1), 100);
            contents.Add(Items.Produce(0), 50);

            Assert.AreEqual(110f, StorageRules.ChestWeight(contents, Weights), 1e-3f);
            Assert.AreEqual(40, StorageRules.ChestRoom(contents, Items.Produce(1), Weights));
            Assert.AreEqual(200, StorageRules.ChestRoom(contents, Items.Produce(0), Weights), "40 kg of 0.2 kg crops");
            Assert.AreEqual(0, StorageRules.ChestRoom(contents, Blocks.Dirt, Weights));

            contents.Add(Items.Produce(1), 40);
            Assert.AreEqual(0, StorageRules.ChestRoom(contents, Items.Produce(1), Weights));
        }

        [Test]
        public void Piles_FillTheirOwnBlockFirst_AndTakeAnotherOnlyWhenEmpty()
        {
            var piles = new StockpilePiles();

            Assert.AreEqual(StorageRules.StockpileCapacity, piles.Room(Blocks.Dirt));
            Assert.AreEqual(30, piles.Add(Blocks.Dirt, 30));
            Assert.AreEqual(27, piles.CountAt(0));
            Assert.AreEqual(3, piles.CountAt(1));
            Assert.AreEqual(5, piles.Add(Blocks.Granite, 5));
            Assert.AreEqual(Blocks.Granite, piles.ItemAt(2));
            Assert.AreEqual(5, piles.Add(Blocks.Dirt, 5));
            Assert.AreEqual(8, piles.CountAt(1), "dirt tops up its own pile");
            Assert.AreEqual(StorageRules.PileSize * 6 + 19, piles.Room(Blocks.Dirt));
            Assert.AreEqual(StorageRules.PileSize * 6 + 22, piles.Room(Blocks.Granite));
            Assert.AreEqual(0, piles.Room(Items.Arrow));

            Assert.AreEqual(5, piles.Take(2, 10));
            Assert.AreEqual(Items.None, piles.ItemAt(2), "an emptied pile takes any block again");
            Assert.AreEqual(35, piles.Total);
        }

        [Test]
        public void Piles_TakeNoMoreThanTheirRoom()
        {
            var piles = new StockpilePiles();

            Assert.AreEqual(StorageRules.StockpileCapacity, piles.Add(Blocks.Sand, 1000));
            Assert.AreEqual(0, piles.Room(Blocks.Sand));
            Assert.AreEqual(0, piles.Add(Blocks.Sand, 1));
        }

        [Test]
        public void Storages_KnowTheCellsTheyCover_AndWhichAreSolid()
        {
            var store = new Storages();
            Storage chest = store.Add(StorageKind.Chest, new BlockPos(5, 9, 5), 1, "alice");
            Storage stockpile = store.Add(StorageKind.Stockpile, new BlockPos(10, 9, 10), 0, "bob");
            stockpile.Piles.Add(Blocks.Dirt, 30);

            Assert.IsTrue(store.IsSolid(5, 9, 5));
            Assert.IsTrue(store.IsSolid(10, 9, 10), "a pile holding something is solid");
            Assert.IsTrue(store.IsSolid(11, 9, 10));
            Assert.IsFalse(store.IsSolid(12, 9, 12), "an empty pile is not");
            Assert.IsTrue(store.TryAt(new BlockPos(12, 9, 12), out Storage found, out int index));
            Assert.AreSame(stockpile, found);
            Assert.AreEqual(8, index);
            Assert.IsTrue(store.AnyCovered(new[] { new BlockPos(12, 9, 11) }));
            Assert.IsFalse(store.AnyCovered(new[] { new BlockPos(13, 9, 11) }));

            Assert.IsTrue(store.Remove(chest.Id));
            Assert.IsFalse(store.IsSolid(5, 9, 5));
        }

        [Test]
        public void Storages_WriteThenRead_KeepEverything()
        {
            var store = new Storages();
            Storage chest = store.Add(StorageKind.Chest, new BlockPos(5, 9, 5), 3, "alice");
            chest.Allowed.Add("bob");
            chest.Contents.Add(Items.Arrow, 20);
            chest.Contents.Add(Items.Seed(2), 7);
            Storage stockpile = store.Add(StorageKind.Stockpile, new BlockPos(10, 9, 10), 2, "bob");
            stockpile.Piles.Add(Items.WoodOf(Items.OakSpecies), 40);
            var stream = new MemoryStream();
            store.Write(stream);
            stream.Position = 0;

            Storages loaded = Storages.Read(stream);

            Assert.AreEqual(2, loaded.Count);
            Assert.IsTrue(loaded.TryGet(chest.Id, out Storage readChest));
            Assert.AreEqual(StorageKind.Chest, readChest.Kind);
            Assert.AreEqual(new BlockPos(5, 9, 5), readChest.Position);
            Assert.AreEqual(3, readChest.Facing);
            Assert.AreEqual("alice", readChest.OwnerId);
            Assert.IsTrue(readChest.CanOpen("bob"));
            Assert.IsFalse(readChest.CanOpen("carol"));
            Assert.AreEqual(20, readChest.Contents.Count(Items.Arrow));
            Assert.AreEqual(7, readChest.Contents.Count(Items.Seed(2)));
            Assert.IsTrue(loaded.TryGet(stockpile.Id, out Storage readPile));
            Assert.AreEqual(40, readPile.Piles.CountOf(Items.WoodOf(Items.OakSpecies)));
            Assert.IsTrue(loaded.IsSolid(11, 9, 10));

            Storage added = loaded.Add(StorageKind.Chest, new BlockPos(0, 9, 0), 0, "carol");
            Assert.Greater(added.Id, stockpile.Id, "ids are never reused");
        }

        [Test]
        public void Storages_ReadOfAnotherFile_Throws()
        {
            var stream = new MemoryStream(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });

            Assert.Throws<InvalidDataException>(() => Storages.Read(stream));
        }

        [Test]
        public void Placement_ChestOnSolidGround_StockpileOnFlatGround()
        {
            World world = FlatGenerator.CreateWorld();
            int ground = FlatGenerator.GroundHeight;

            Assert.IsTrue(StorageRules.CanPlaceChest(world, new BlockPos(4, ground, 4)));
            Assert.IsFalse(StorageRules.CanPlaceChest(world, new BlockPos(4, ground + 1, 4)), "nothing under it");
            Assert.IsFalse(StorageRules.CanPlaceChest(world, new BlockPos(4, ground - 1, 4)), "inside the ground");

            BlockPos corner = StorageRules.StockpileCorner(new BlockPos(6, ground, 6), 0);
            Assert.AreEqual(new BlockPos(5, ground, 6), corner);
            Assert.IsTrue(StorageRules.CanPlaceStockpile(world, corner));
            world.SetBlock(7, ground, 8, Blocks.Dirt);
            Assert.IsFalse(StorageRules.CanPlaceStockpile(world, corner), "a block stands in it");
            world.SetBlock(7, ground, 8, Blocks.Air);
            world.SetBlock(7, ground - 1, 8, Blocks.Air);
            Assert.IsFalse(StorageRules.CanPlaceStockpile(world, corner), "a hole under it");
        }

        [Test]
        public void StockpileCorner_PutsTheTargetInTheMiddleOfTheNearEdge()
        {
            var target = new BlockPos(10, 5, 10);

            Assert.AreEqual(new BlockPos(9, 5, 10), StorageRules.StockpileCorner(target, 0));
            Assert.AreEqual(new BlockPos(10, 5, 9), StorageRules.StockpileCorner(target, 1));
            Assert.AreEqual(new BlockPos(9, 5, 8), StorageRules.StockpileCorner(target, 2));
            Assert.AreEqual(new BlockPos(8, 5, 9), StorageRules.StockpileCorner(target, 3));
            Assert.AreEqual(0, StorageRules.FacingOf(10f));
            Assert.AreEqual(1, StorageRules.FacingOf(80f));
            Assert.AreEqual(3, StorageRules.FacingOf(-90f));
            Assert.AreEqual(2, StorageRules.FacingOf(190f));
        }

        [Test]
        public void Names_AreTrimmed_AndRefusedWhenOddOrTooLong()
        {
            Assert.IsTrue(PlayerNames.TryNormalize("  Élodie_2 ", out string name));
            Assert.AreEqual("Élodie_2", name);
            Assert.IsTrue(PlayerNames.TryNormalize("Jean Paul", out _));
            Assert.IsFalse(PlayerNames.TryNormalize("Jean  Paul", out _), "a single space between words");
            Assert.IsFalse(PlayerNames.TryNormalize("A", out _));
            Assert.IsFalse(PlayerNames.TryNormalize("Seventeen letters", out _));
            Assert.IsFalse(PlayerNames.TryNormalize("bad<name>", out _));
            Assert.IsFalse(PlayerNames.TryNormalize(null, out _));
            Assert.IsTrue(PlayerNames.SameName("élodie", "ÉLODIE"));
            Assert.IsFalse(PlayerNames.SameName("", ""));
        }

        [Test]
        public void StorageItems_AreValid_AndWeighed()
        {
            Assert.IsTrue(Items.IsValid(Items.Chest));
            Assert.IsTrue(Items.IsValid(Items.Stockpile));
            Assert.IsFalse(CarryLimits.CountsAsBlock(Items.Chest));
            Assert.AreEqual(ItemWeights.Chest, Weights.Of(Items.Chest));
            Assert.AreEqual(ItemWeights.Stockpile, Weights.Of(Items.Stockpile));
            Assert.AreEqual("Chest", Items.NameOf(Items.Chest));
        }
    }
}

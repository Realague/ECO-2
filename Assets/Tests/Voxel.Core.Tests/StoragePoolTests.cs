using System.Collections.Generic;
using NUnit.Framework;

namespace Voxel.Core.Tests
{
    public class StoragePoolTests
    {
        // Crop 0 weighs 0.2 kg, crop 1 one kg.
        private static readonly ItemWeights Weights = new ItemWeights(new[] { 0.2f, 1f });

        private static readonly int Heavy = Items.Produce(1);

        private static List<Storage> Chests(int count)
        {
            var chests = new List<Storage>();
            for (int i = 0; i < count; i++)
            {
                chests.Add(new Storage(i + 1, StorageKind.Chest, new BlockPos(i, 10, 0), 0, "alice"));
            }

            return chests;
        }

        private static List<Storage> Stockpiles(int count)
        {
            var stockpiles = new List<Storage>();
            for (int i = 0; i < count; i++)
            {
                stockpiles.Add(new Storage(i + 1, StorageKind.Stockpile, new BlockPos(i * 3, 10, 0), 0, "alice"));
            }

            return stockpiles;
        }

        [Test]
        public void Chests_TakeTogetherWhatOneCannot()
        {
            List<Storage> chests = Chests(3);

            Assert.AreEqual(450, StoragePool.ChestRoom(chests, Heavy, Weights));
            Assert.AreEqual(160, StoragePool.AddToChests(chests, Heavy, 160, Weights));
            Assert.AreEqual(150, chests[0].Contents.Count(Heavy), "the first chest fills first");
            Assert.AreEqual(10, chests[1].Contents.Count(Heavy), "the rest goes into the next");
            Assert.AreEqual(0, chests[2].Contents.Count(Heavy));
            Assert.AreEqual(160, StoragePool.Count(chests, Heavy));
            Assert.AreEqual(160f, StoragePool.Weight(chests, Weights), 1e-3f);
            Assert.AreEqual(290, StoragePool.ChestRoom(chests, Heavy, Weights));
            Assert.AreEqual(160, StoragePool.Contents(chests).Count(Heavy));
        }

        [Test]
        public void Taking_DrawsFromSeveralChests()
        {
            List<Storage> chests = Chests(2);
            chests[0].Contents.Add(Heavy, 5);
            chests[1].Contents.Add(Heavy, 7);

            Assert.AreEqual(8, StoragePool.TakeFromChests(chests, Heavy, 8));
            Assert.AreEqual(0, chests[0].Contents.Count(Heavy));
            Assert.AreEqual(4, chests[1].Contents.Count(Heavy));
            Assert.AreEqual(4, StoragePool.TakeFromChests(chests, Heavy, 10), "no more than there is");
        }

        [Test]
        public void StockpilePiles_FollowOneAnother()
        {
            List<Storage> stockpiles = Stockpiles(2);

            Assert.AreEqual(18, StoragePool.PileCount(stockpiles));
            Assert.AreEqual(2 * StorageRules.StockpileCapacity, StoragePool.StockpileRoom(stockpiles, Blocks.Dirt));
            Assert.AreEqual(250, StoragePool.AddToStockpiles(stockpiles, Blocks.Dirt, 250));
            Assert.AreEqual(StorageRules.StockpileCapacity, stockpiles[0].Piles.Total, "the first stockpile fills first");
            Assert.AreEqual(250 - StorageRules.StockpileCapacity, stockpiles[1].Piles.Total);

            Assert.IsTrue(StoragePool.TryFindPile(stockpiles, 10, out Storage stockpile, out int index));
            Assert.AreSame(stockpiles[1], stockpile);
            Assert.AreEqual(1, index);
            Assert.IsFalse(StoragePool.TryFindPile(stockpiles, 18, out _, out _));
            Assert.IsFalse(StoragePool.TryFindPile(stockpiles, -1, out _, out _));
        }

        [Test]
        public void LargeAndSmallStockpiles_ShareTheirPiles()
        {
            var stockpiles = new List<Storage>
            {
                new Storage(1, StorageKind.Stockpile, new BlockPos(0, 10, 0), 0, "alice"),
                new Storage(2, StorageKind.SmallStockpile, new BlockPos(5, 10, 0), 0, "alice")
            };

            Assert.AreEqual(13, StoragePool.PileCount(stockpiles));
            Assert.AreEqual(StorageRules.StockpileCapacity + 80, StoragePool.Capacity(stockpiles));
            Assert.AreEqual(StorageRules.StockpileCapacity + 80, StoragePool.StockpileRoom(stockpiles, Blocks.Dirt));
            Assert.IsTrue(StoragePool.TryFindPile(stockpiles, 9, out Storage stockpile, out int index));
            Assert.AreSame(stockpiles[1], stockpile);
            Assert.AreEqual(0, index);
            Assert.IsTrue(StoragePool.TryFindPile(stockpiles, 12, out _, out index));
            Assert.AreEqual(3, index);
            Assert.IsFalse(StoragePool.TryFindPile(stockpiles, 13, out _, out _));

            stockpiles[1].Piles.Add(Blocks.Granite, 50);
            var large = new List<Storage> { stockpiles[0] };
            Assert.IsTrue(StoragePool.TryEmptyInto(stockpiles[1], large, Weights));
            Assert.IsTrue(stockpiles[1].IsEmpty);
            Assert.AreEqual(50, stockpiles[0].Piles.CountOf(Blocks.Granite));
        }

        [Test]
        public void Emptying_IntoTheOthers_MovesEverythingOrNothing()
        {
            List<Storage> chests = Chests(3);
            chests[0].Contents.Add(Heavy, 100);
            chests[1].Contents.Add(Heavy, 120);
            chests[2].Contents.Add(Heavy, 140);
            var others = new List<Storage> { chests[1], chests[2] };

            Assert.IsFalse(StoragePool.TryEmptyInto(chests[0], others, Weights), "40 kg of room for 100");
            Assert.AreEqual(100, chests[0].Contents.Count(Heavy), "nothing moved");
            Assert.AreEqual(120, chests[1].Contents.Count(Heavy));
            Assert.AreEqual(140, chests[2].Contents.Count(Heavy));

            chests[0].Contents.TryRemove(Heavy, 70);
            Assert.IsTrue(StoragePool.TryEmptyInto(chests[0], others, Weights));
            Assert.IsTrue(chests[0].IsEmpty);
            Assert.AreEqual(290, StoragePool.Count(others, Heavy));

            List<Storage> stockpiles = Stockpiles(2);
            stockpiles[0].Piles.Add(Blocks.Dirt, 30);
            stockpiles[1].Piles.Add(Blocks.Granite, StorageRules.StockpileCapacity - 20);
            var rest = new List<Storage> { stockpiles[1] };
            Assert.IsFalse(StoragePool.TryEmptyInto(stockpiles[0], rest, Weights), "no free pile for the dirt");
            Assert.AreEqual(30, stockpiles[0].Piles.Total);
            Assert.AreEqual(StorageRules.StockpileCapacity - 20, stockpiles[1].Piles.Total);

            stockpiles[1].Piles.Take(8, StorageRules.PileSize);
            stockpiles[1].Piles.Take(7, StorageRules.PileSize);
            Assert.IsTrue(StoragePool.TryEmptyInto(stockpiles[0], rest, Weights));
            Assert.IsTrue(stockpiles[0].IsEmpty);
            Assert.AreEqual(30, stockpiles[1].Piles.CountOf(Blocks.Dirt));
        }

        [Test]
        public void Taking_AnItemFromStockpiles_DrawsFromEveryPileOfIt()
        {
            List<Storage> stockpiles = Stockpiles(2);
            StoragePool.AddToStockpiles(stockpiles, Blocks.Dirt, StorageRules.StockpileCapacity + 10);
            stockpiles[0].Piles.Take(0, 27);
            stockpiles[0].Piles.Add(Blocks.Sand, 5);

            Assert.AreEqual(StorageRules.StockpileCapacity - 17, StoragePool.StockpileCount(stockpiles, Blocks.Dirt));
            Assert.AreEqual(200, StoragePool.TakeFromStockpiles(stockpiles, Blocks.Dirt, 200));
            Assert.AreEqual(StorageRules.StockpileCapacity - 217, StoragePool.StockpileCount(stockpiles, Blocks.Dirt));
            Assert.AreEqual(5, StoragePool.StockpileCount(stockpiles, Blocks.Sand), "other piles untouched");
            Assert.AreEqual(StorageRules.StockpileCapacity - 217, StoragePool.TakeFromStockpiles(stockpiles, Blocks.Dirt, 999), "no more than there is");
        }
    }
}

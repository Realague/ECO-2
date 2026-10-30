using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;

namespace Voxel.Core.Tests
{
    public class CarpentryTests
    {
        private static readonly int Oak = Items.WoodOf(0);
        private static readonly int Birch = Items.WoodOf(2);

        private static int IndexOf(int output) => Recipes.Carpentry.ToList().FindIndex(recipe => recipe.Output == output);

        private static bool Room(Recipe recipe) => true;

        [Test]
        public void WoodBlocks_ArePlanksAndLogs_OfEveryWood()
        {
            Assert.AreEqual(WoodBlocks.Count, WoodBlocks.Species.Count);
            Assert.AreEqual(Blocks.Door, Blocks.LastType);
            Assert.Less((int)Blocks.LastType, Items.LegacyWood, "block types stay below the item ranges");
            for (int n = 0; n < WoodBlocks.Count; n++)
            {
                int species = WoodBlocks.Species[n].Index;
                Assert.IsTrue(WoodBlocks.TryPlanksOf(species, out ushort planks));
                Assert.IsTrue(WoodBlocks.TryLogOf(species, out ushort log));
                Assert.IsTrue(WoodBlocks.IsPlanks(planks) && !WoodBlocks.IsLog(planks));
                Assert.IsTrue(WoodBlocks.IsLog(log) && !WoodBlocks.IsPlanks(log));
                Assert.AreEqual(species, WoodBlocks.SpeciesOf(planks));
                Assert.AreEqual(species, WoodBlocks.SpeciesOf(log));
                foreach (ushort block in new[] { planks, log })
                {
                    Assert.IsTrue(Blocks.IsSolid(block) && Blocks.IsOpaque(block) && Blocks.IsPlaceable(block));
                    Assert.IsTrue(Items.IsBlock(block) && Items.IsValid(block));
                    Assert.IsTrue(CarryLimits.CountsAsBlock(block));
                    Assert.IsTrue(StorageRules.AcceptsInStockpile(block));
                    Assert.AreEqual(Items.Hammer, ToolRules.ToBreak(block));
                    Assert.AreEqual((int)block, Items.DropOf(block));
                }

                Assert.AreEqual(1, MiningRules.HitsToBreak(planks), "a hammer takes it back at once");
                Assert.AreEqual(1, MiningRules.HitsToBreak(log));
            }

            Assert.IsFalse(WoodBlocks.TryPlanksOf(1, out _), "bushes give no wood");
        }

        [Test]
        public void TheCarpentryTable_MakesWoodBlocksADoorAndTheStations()
        {
            Assert.AreEqual(2 * WoodBlocks.Count + 3, Recipes.Carpentry.Count);
            Recipe oakPlanks = Recipes.Carpentry[0];
            Assert.AreEqual((int)Blocks.FirstPlanks, oakPlanks.Output);
            Assert.AreEqual(2, oakPlanks.OutputCount);
            Assert.AreEqual(Oak, oakPlanks.Ingredients[0].Item);
            Assert.AreEqual((int)Blocks.FirstLog, Recipes.Carpentry[1].Output);
            Assert.GreaterOrEqual(IndexOf(Blocks.Door), 0);
            Assert.GreaterOrEqual(IndexOf(Items.Stockpile), 0);
            Assert.GreaterOrEqual(IndexOf(Items.ButcherTable), 0);
            Assert.IsTrue(Recipes.All.Any(recipe => recipe.Output == Items.CarpentryTable), "crafted from the bag");
            Assert.IsTrue(Recipes.All.Any(recipe => recipe.Output == Items.SmallStockpile), "still crafted from the bag");
            Assert.IsFalse(Recipes.All.Any(recipe => recipe.Output == Items.Stockpile));
            foreach (Recipe recipe in Recipes.Carpentry)
            {
                Assert.IsTrue(Items.IsValid(recipe.Output), Items.NameOf(recipe.Output));
                Assert.Greater(recipe.Seconds, 0f);
                Assert.Greater(recipe.Calories, 0f);
            }

            Assert.IsTrue(StorageRules.TryKindOf(Items.CarpentryTable, out StorageKind kind));
            Assert.AreEqual(StorageKind.CarpentryTable, kind);
            Assert.IsTrue(StorageRules.IsStation(kind));
            Assert.IsFalse(Station.CarpentryTable().BurnsFuel);
        }

        [Test]
        public void Pick_TakesWhatIsHeldMostOf_OrWhatWasChosen()
        {
            var available = new Inventory();
            available.Add(Oak, 5);
            available.Add(Birch, 30);
            available.Add(Blocks.Granite, 10);
            Recipe stockpile = Recipes.Carpentry[IndexOf(Items.Stockpile)];

            List<(int Item, int Count)> auto = Recipes.Pick(available, stockpile, null, 1);
            Assert.AreEqual(1, auto.Count, "birch alone covers it");
            Assert.AreEqual((Birch, 24), auto[0]);

            available.TryRemove(Birch, 20);
            Assert.IsNull(Recipes.Pick(available, stockpile, null, 1), "10 birch and 5 oak are not enough");

            available.Add(Oak, 10);
            List<(int Item, int Count)> both = Recipes.Pick(available, stockpile, null, 1);
            Assert.AreEqual(2, both.Count);
            Assert.AreEqual((Oak, 15), both[0], "the wood held most of first");
            Assert.AreEqual((Birch, 9), both[1]);
            Assert.IsNull(Recipes.Pick(available, stockpile, null, 2));

            available.Add(Oak, 30);
            Assert.AreEqual((Oak, 24), Recipes.Pick(available, stockpile, new[] { Oak }, 1)[0]);
            Assert.IsNull(Recipes.Pick(available, stockpile, new[] { Birch }, 1), "a chosen wood must cover it alone");
            Assert.IsNull(Recipes.Pick(available, stockpile, new[] { (int)Blocks.Granite }, 1), "granite is no wood");
        }

        [Test]
        public void AnOrder_KeepsWhatItTook_AndUsesItUpOneByOne()
        {
            var table = Station.CarpentryTable();
            int door = IndexOf(Blocks.Door);
            var planks = new List<(int, int)> { (Blocks.FirstPlanks, 8), (Blocks.FirstPlanks + 1, 4) };
            StationJob job = table.Queue(door, 2, "alice", null, 2, planks);
            table.AddLabour(job.Id, 1000f);

            Assert.AreEqual(2, job.Stocked);
            Assert.AreEqual(12, job.Held().Sum(entry => entry.Value));
            Assert.AreSame(job, table.Tick(10f, Room));
            Assert.AreEqual(1, job.Stocked);
            Assert.AreEqual(6, job.Held().Sum(entry => entry.Value), "one door's planks are used up");
            Assert.AreSame(job, table.Tick(10f, Room));
            Assert.AreEqual(0, table.Jobs.Count);
        }

        [Test]
        public void AnOrderWaiting_TakesWhatTheCallerGives()
        {
            var table = Station.CarpentryTable();
            StationJob job = table.Queue(IndexOf(Items.Stockpile), 1, "alice", new[] { Oak }, 0, null);
            table.AddLabour(job.Id, 1000f);

            Assert.IsNull(table.Tick(1f, Room, order => null));
            Assert.AreEqual(StationStatus.NoIngredients, table.Status);
            Assert.AreEqual(Oak, job.Choices[0]);

            Assert.IsNull(table.Tick(1f, Room, order => Recipes.Pick(Inventory(Oak, 30), order.Made, order.Choices, 1)));
            Assert.AreEqual(1, job.Stocked);
            Assert.AreEqual(24, job.Taken.Count(Oak));
        }

        [Test]
        public void ACarpentryTable_IsSavedWithItsOrdersChoicesAndWhatTheyTook()
        {
            var store = new Storages();
            Storage storage = store.Add(StorageKind.CarpentryTable, new BlockPos(4, 9, 4), 2, "alice");
            StationJob job = storage.Station.Queue(IndexOf(Items.ButcherTable), 2, "alice", new[] { Birch, Items.None }, 1,
                new List<(int, int)> { (Birch, 10), (Blocks.Granite, 4) });
            var stream = new MemoryStream();
            store.Write(stream);
            stream.Position = 0;

            Storages loaded = Storages.Read(stream);

            Assert.IsTrue(loaded.TryGet(storage.Id, out Storage read));
            Assert.AreEqual(StorageKind.CarpentryTable, read.Kind);
            StationJob readJob = read.Station.Jobs[0];
            Assert.AreEqual(job.Count, readJob.Count);
            Assert.AreEqual(1, readJob.Stocked);
            Assert.AreEqual(new[] { Birch, Items.None }, readJob.Choices);
            Assert.AreEqual(10, readJob.Taken.Count(Birch));
            Assert.AreEqual(4, readJob.Taken.Count(Blocks.Granite));
        }

        [Test]
        public void Stockpiles_HaveRoomForEverythingTogether_OrNot()
        {
            var store = new Storages();
            Storage pile = store.Add(StorageKind.SmallStockpile, new BlockPos(0, 9, 0), 0, "alice");
            var piles = new List<Storage> { pile };

            Assert.IsTrue(StoragePool.StockpileRoomFor(piles, new List<(int, int)> { (Blocks.FirstPlanks, 40), (Blocks.FirstLog, 40) }));
            Assert.IsFalse(StoragePool.StockpileRoomFor(piles, new List<(int, int)> { (Blocks.FirstPlanks, 60), (Blocks.FirstLog, 40) }),
                "four piles of 20: 60 planks take three, 40 logs two");
            Assert.IsFalse(StoragePool.StockpileRoomFor(piles, new List<(int, int)> { (Items.Chest, 1) }), "a chest does not go on a stockpile");
            Assert.AreEqual(0, StoragePool.StockpileCount(piles, Blocks.FirstPlanks), "only checked on copies");
        }

        private static Inventory Inventory(int item, int count)
        {
            var inventory = new Inventory();
            inventory.Add(item, count);
            return inventory;
        }
    }
}

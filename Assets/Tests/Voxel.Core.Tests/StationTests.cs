using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;

namespace Voxel.Core.Tests
{
    public class StationTests
    {
        private static readonly int Oak = Items.WoodOf(0);
        private static readonly int Birch = Items.WoodOf(2);

        // Baked potato: 150 kcal of work, 15 s of cooking.
        private const int Potato = 0;

        private static bool Room(Recipe recipe) => true;

        private static bool NoRoom(Recipe recipe) => false;

        [Test]
        public void ADish_DoesNotCook_UntilItsWorkIsDone()
        {
            var fire = Station.Campfire();
            fire.AddFuel(Oak, 5);
            StationJob job = fire.Queue(Potato, 1, "alice", 1);

            Assert.IsNull(fire.Tick(60f, Room));
            Assert.AreEqual(StationStatus.NeedsLabour, fire.Status);
            Assert.AreEqual(5, fire.FuelCount, "no wood burns while nothing cooks");

            Assert.AreEqual(100f, fire.AddLabour(job.Id, 100f));
            Assert.AreEqual(50f, fire.AddLabour(job.Id, 100f), "no more than it still needs");
            Assert.IsTrue(job.HasLabour);
            Assert.AreEqual(0f, fire.AddLabour(job.Id, 100f));
        }

        [Test]
        public void Cooking_BurnsWood_OnlyWhileItCooks_AndFinishes()
        {
            var fire = Station.Campfire();
            fire.AddFuel(Oak, 2);
            StationJob job = fire.Queue(Potato, 1, "alice", 1);
            fire.AddLabour(job.Id, 1000f);

            Assert.IsNull(fire.Tick(10f, Room));
            Assert.AreEqual(StationStatus.Working, fire.Status);
            Assert.AreEqual(1, fire.FuelCount, "a log was put on");
            Assert.AreEqual(20f, fire.Burning, 1e-3f);

            StationJob done = fire.Tick(10f, Room);
            Assert.AreSame(job, done, "15 s of cooking");
            Assert.AreEqual(15f, fire.Burning, 1e-3f, "the log burnt only those 15 s");
            Assert.AreEqual(0, fire.Jobs.Count);

            fire.Tick(100f, Room);
            Assert.AreEqual(StationStatus.Idle, fire.Status);
            Assert.AreEqual(15f, fire.Burning, 1e-3f, "nothing burns with nothing queued");
        }

        [Test]
        public void WithoutRoomForTheDish_ItWaits_AndBurnsNothing()
        {
            var fire = Station.Campfire();
            fire.AddFuel(Oak, 2);
            StationJob job = fire.Queue(Potato, 1, "alice", 1);
            fire.AddLabour(job.Id, 1000f);

            Assert.IsNull(fire.Tick(60f, NoRoom));
            Assert.AreEqual(StationStatus.NoRoom, fire.Status);
            Assert.AreEqual(2, fire.FuelCount);
            Assert.AreEqual(15f, job.SecondsLeft, 1e-3f);
        }

        [Test]
        public void WithoutWood_ItWaits()
        {
            var fire = Station.Campfire();
            StationJob job = fire.Queue(Potato, 1, "alice", 1);
            fire.AddLabour(job.Id, 1000f);

            Assert.IsNull(fire.Tick(5f, Room));
            Assert.AreEqual(StationStatus.NoFuel, fire.Status);
            fire.AddFuel(Oak, 1);
            fire.Tick(5f, Room);
            Assert.AreEqual(StationStatus.Working, fire.Status);
            Assert.AreEqual(10f, job.SecondsLeft, 1e-3f);
        }

        [Test]
        public void ADishReady_CooksBefore_OneStillWaitingForWork()
        {
            var fire = Station.Campfire();
            fire.AddFuel(Oak, 3);
            StationJob first = fire.Queue(Potato, 1, "alice", 1);
            StationJob second = fire.Queue(Potato, 1, "bob", 1);
            fire.AddLabour(second.Id, 1000f);

            Assert.AreSame(second, fire.Next);
            Assert.AreSame(second, fire.Tick(20f, Room));
            Assert.AreEqual(1, fire.Jobs.Count);
            Assert.AreSame(first, fire.Jobs[0]);
            Assert.AreEqual(StationStatus.NeedsLabour, fire.Status);
        }

        [Test]
        public void Logs_AreTakenFromTheWoodHeldMost()
        {
            var fire = Station.Campfire();
            fire.AddFuel(Oak, 1);
            fire.AddFuel(Birch, 3);
            StationJob job = fire.Queue(Potato, 1, "alice", 1);
            fire.AddLabour(job.Id, 1000f);

            fire.Tick(1f, Room);
            Assert.AreEqual(2, fire.Fuel.Count(Birch));
            Assert.AreEqual(1, fire.Fuel.Count(Oak));
        }

        [Test]
        public void Limits_OfWoodAndQueue()
        {
            var fire = Station.Campfire();
            Assert.AreEqual(Station.MaxFuel, fire.AddFuel(Oak, 100));
            Assert.AreEqual(0, fire.AddFuel(Birch, 1));
            Assert.AreEqual(0, fire.AddFuel(Blocks.Granite, 1), "only wood burns");

            for (int i = 0; i < Station.MaxJobs; i++)
            {
                Assert.IsNotNull(fire.Queue(Potato, 1, "alice", 1));
            }

            Assert.IsNull(fire.Queue(Potato, 1, "alice", 1), "the queue is full");
            Assert.IsNull(Station.Campfire().Queue(Recipes.Cooking.Count, 1, "alice", 1), "no such dish");
        }

        [Test]
        public void ACampfire_IsSavedWithTheStorage()
        {
            var store = new Storages();
            Storage storage = store.Add(StorageKind.Campfire, new BlockPos(4, 9, 4), 1, "alice");
            Station fire = storage.Station;
            fire.AddFuel(Oak, 4);
            StationJob job = fire.Queue(Potato, 3, "alice", 2);
            fire.AddLabour(job.Id, 60f);
            var stream = new MemoryStream();
            store.Write(stream);
            stream.Position = 0;

            Storages loaded = Storages.Read(stream);

            Assert.IsTrue(loaded.TryGet(storage.Id, out Storage read));
            Assert.AreEqual(StorageKind.Campfire, read.Kind);
            Assert.AreEqual(1, read.CellCount);
            Assert.IsTrue(loaded.IsSolid(4, 9, 4));
            Assert.AreEqual(4, read.Station.FuelCount);
            Assert.AreEqual(1, read.Station.Jobs.Count);
            StationJob readJob = read.Station.Jobs[0];
            Assert.AreEqual(390f, readJob.LabourLeft, 1e-3f);
            Assert.AreEqual("alice", readJob.CreatorId);
            Assert.AreEqual(3, readJob.Count);
            Assert.AreEqual(2, readJob.Stocked);
            Assert.AreEqual(4, readJob.Held()[0].Value, "two dishes' potatoes");
            Assert.AreNotEqual(job.Id, read.Station.Queue(Potato, 1, "bob", 1).Id, "dish ids are never reused");
        }

        [Test]
        public void AStorageSave_OfFormat1_StillLoads()
        {
            var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                writer.Write(0x524F5453u);
                writer.Write(1);
                writer.Write(2);
                writer.Write(1);
                writer.Write(1);
                writer.Write((byte)StorageKind.Chest);
                writer.Write(3);
                writer.Write(9);
                writer.Write(3);
                writer.Write((byte)0);
                writer.Write("alice");
                writer.Write(0);
                writer.Write(1);
                writer.Write(Items.Arrow);
                writer.Write(5);
            }

            stream.Position = 0;
            Storages loaded = Storages.Read(stream);

            Assert.IsTrue(loaded.TryGet(1, out Storage chest));
            Assert.AreEqual(5, chest.Contents.Count(Items.Arrow));
        }

        [Test]
        public void ACampfireOfFormat2_LoadsItsDishesAsOrdersOfOne()
        {
            var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                writer.Write(0x524F5453u);
                writer.Write(2);
                writer.Write(2);
                writer.Write(1);
                writer.Write(1);
                writer.Write((byte)StorageKind.Campfire);
                writer.Write(3);
                writer.Write(9);
                writer.Write(3);
                writer.Write((byte)0);
                writer.Write("alice");
                writer.Write(0);
                writer.Write(4); // next dish id
                writer.Write(12f); // the log alight
                writer.Write(1);
                writer.Write(Oak);
                writer.Write(3);
                writer.Write(2); // two dishes
                foreach (int taken in new[] { 1, 0 })
                {
                    writer.Write(taken + 1);
                    writer.Write(Potato);
                    writer.Write(40f);
                    writer.Write(15f);
                    writer.Write("alice");
                    writer.Write(taken);
                    for (int t = 0; t < taken; t++)
                    {
                        writer.Write(Items.Produce(CropSpecies.Potato));
                        writer.Write(2);
                    }
                }
            }

            stream.Position = 0;
            Storages loaded = Storages.Read(stream);

            Assert.IsTrue(loaded.TryGet(1, out Storage storage));
            Station fire = storage.Station;
            Assert.AreEqual(3, fire.FuelCount);
            Assert.AreEqual(12f, fire.Burning, 1e-4f);
            Assert.AreEqual(2, fire.Jobs.Count);
            Assert.AreEqual(1, fire.Jobs[0].Count);
            Assert.AreEqual(1, fire.Jobs[0].Stocked, "its ingredients were taken");
            Assert.AreEqual(0, fire.Jobs[1].Stocked);
            Assert.AreEqual(40f, fire.Jobs[1].LabourLeft, 1e-4f);
        }

        [Test]
        public void AnOrder_CooksEachDish_OnceItsShareOfWorkIsIn()
        {
            var fire = Station.Campfire();
            fire.AddFuel(Oak, 5);
            StationJob job = fire.Queue(Potato, 3, "alice", 3);
            Assert.AreEqual(450f, job.LabourLeft, 1e-3f);

            fire.AddLabour(job.Id, 100f);
            Assert.IsNull(fire.Tick(60f, Room));
            Assert.AreEqual(StationStatus.NeedsLabour, fire.Status, "100 of the first dish's 150");

            fire.AddLabour(job.Id, 50f);
            Assert.AreSame(job, fire.Tick(20f, Room));
            Assert.AreEqual(2, job.Count);
            Assert.AreEqual(2, job.Stocked);
            Assert.IsNull(fire.Tick(60f, Room));
            Assert.AreEqual(StationStatus.NeedsLabour, fire.Status, "the second dish is not paid for");

            fire.AddLabour(job.Id, 1000f);
            Assert.AreSame(job, fire.Tick(15f, Room));
            Assert.AreSame(job, fire.Tick(15f, Room));
            Assert.AreEqual(0, fire.Jobs.Count);
        }

        [Test]
        public void ADishWithoutIngredients_WaitsForThem()
        {
            var fire = Station.Campfire();
            fire.AddFuel(Oak, 5);
            StationJob job = fire.Queue(Potato, 2, "alice", 0);
            fire.AddLabour(job.Id, 1000f);
            int asked = 0;

            Assert.IsNull(fire.Tick(5f, Room, order =>
            {
                asked++;
                return null;
            }));
            Assert.AreEqual(StationStatus.NoIngredients, fire.Status);
            Assert.AreEqual(1, asked);
            Assert.AreEqual(5, fire.FuelCount, "nothing burns while it waits");

            Assert.IsNull(fire.Tick(5f, Room, order => new List<(int, int)> { (Items.Produce(CropSpecies.Potato), 2) }));
            Assert.AreEqual(StationStatus.Working, fire.Status);
            Assert.AreEqual(1, job.Stocked);
            Assert.AreEqual(10f, job.SecondsLeft, 1e-3f);
            Assert.AreEqual(2, job.Held()[0].Value, "one dish's potatoes");
        }

        [Test]
        public void AnOrderWithItsIngredients_CooksBeforeOneWithout()
        {
            var fire = Station.Campfire();
            fire.AddFuel(Oak, 5);
            StationJob waiting = fire.Queue(Potato, 1, "alice", 0);
            StationJob stocked = fire.Queue(Potato, 1, "bob", 1);
            fire.AddLabour(waiting.Id, 1000f);
            fire.AddLabour(stocked.Id, 1000f);

            Assert.AreSame(stocked, fire.Next);
            Assert.AreSame(stocked, fire.Tick(15f, Room, order => null));
            Assert.IsNull(fire.Tick(1f, Room, order => null));
            Assert.AreEqual(StationStatus.NoIngredients, fire.Status);
        }

        [Test]
        public void Fuels_SayHowLongTheyBurn()
        {
            Assert.AreEqual(Fuels.WoodSeconds, Fuels.SecondsOf(Oak));
            Assert.AreEqual(Fuels.WoodSeconds, Fuels.SecondsOf(Birch));
            Assert.AreEqual(0f, Fuels.SecondsOf(Blocks.Granite));
            Assert.IsFalse(Fuels.IsFuel(Items.Produce(CropSpecies.Potato)));

            var fire = Station.Campfire();
            fire.AddFuel(Oak, 3);
            Assert.AreEqual(3 * Fuels.WoodSeconds, fire.FuelSeconds, 1e-3f);
            StationJob job = fire.Queue(Potato, 1, "alice", 1);
            fire.AddLabour(job.Id, 1000f);
            fire.Tick(5f, Room);
            Assert.AreEqual(Fuels.WoodSeconds, fire.LogSeconds, 1e-3f);
            Assert.AreEqual(3 * Fuels.WoodSeconds - 5f, fire.FuelSeconds, 1e-3f, "the log alight counts what is left of it");
        }

        [Test]
        public void Wood_IsTakenBack()
        {
            var fire = Station.Campfire();
            fire.AddFuel(Oak, 3);
            Assert.AreEqual(2, fire.TakeFuel(Oak, 2));
            Assert.AreEqual(1, fire.TakeFuel(Oak, 5));
            Assert.AreEqual(0, fire.TakeFuel(Birch, 1));
            Assert.AreEqual(0, fire.FuelCount);
            Assert.IsNull(fire.Queue(Potato, 0, "alice", 0), "at least one");
            Assert.IsNull(fire.Queue(Potato, Station.MaxCount + 1, "alice", 0));
        }

        [Test]
        public void Dishes_FeedMoreThanTheirCrops_AndAreItems()
        {
            Assert.AreEqual(Dishes.All.Count, Recipes.Cooking.Count);
            for (int i = 0; i < Recipes.Cooking.Count; i++)
            {
                Recipe recipe = Recipes.Cooking[i];
                Assert.IsTrue(Items.IsValid(recipe.Output), Items.NameOf(recipe.Output));
                Assert.IsTrue(Dishes.FoodOf(recipe.Output).IsFood);
                Assert.Greater(new ItemWeights(new float[0]).Of(recipe.Output), 0f);
                Assert.Greater(recipe.Calories, 0f);
                Assert.Greater(recipe.Seconds, 0f);
                foreach (Ingredient ingredient in recipe.Ingredients)
                {
                    Assert.IsTrue(Items.IsValid(ingredient.Item), Items.NameOf(ingredient.Item));
                }
            }

            Assert.AreEqual("Baked potato", Items.NameOf(Items.Dish(0)));
            Assert.IsFalse(Items.IsValid(Items.Dish(Dishes.All.Count)), "no such dish");
            Assert.IsTrue(Items.IsValid(Items.Campfire));
            Assert.IsTrue(StorageRules.TryKindOf(Items.Campfire, out StorageKind kind));
            Assert.AreEqual(StorageKind.Campfire, kind);
            Assert.IsTrue(StorageRules.AcceptsInChest(Items.Dish(0)), "dishes go in chests");
        }

        // A deer (species 2): large, 20 s and 150 kcal on a butcher's table.
        private const int Deer = 2;

        [Test]
        public void AButcherTable_WorksWithoutFuel()
        {
            var table = Station.ButcherTable();
            Assert.AreEqual(0, table.AddFuel(Oak, 5), "it burns nothing");

            StationJob job = table.Queue(Deer, 1, "alice", 1);
            table.AddLabour(job.Id, 150f);

            Assert.IsNull(table.Tick(10f, Room));
            Assert.AreEqual(StationStatus.Working, table.Status);
            Assert.AreSame(job, table.Tick(10f, Room));
            Assert.AreEqual(0, table.Jobs.Count);
            Assert.AreEqual(4, job.Made.Outputs[0].Count, "a deer's meat");
        }

        [Test]
        public void AButcherTable_WaitsUntilEverythingItMakesHasRoom()
        {
            var store = new Storages();
            Storage chest = store.Add(StorageKind.Chest, new BlockPos(1, 9, 1), 0, "alice");
            var weights = new ItemWeights(new float[0]);
            chest.Contents.Add(Items.Dish(0), (int)((StorageRules.ChestMaxWeight - 2f) / weights.Of(Items.Dish(0))));
            var chests = new List<Storage> { chest };
            Recipe deer = Recipes.Butchery[Deer];

            Assert.IsFalse(StoragePool.ChestRoomFor(chests, deer.Outputs, weights), "1.2 kg of meat fits, not the hide and bones too");

            var table = Station.ButcherTable();
            StationJob job = table.Queue(Deer, 1, "alice", 1);
            table.AddLabour(job.Id, 150f);
            Assert.IsNull(table.Tick(30f, recipe => StoragePool.ChestRoomFor(chests, recipe.Outputs, weights)));
            Assert.AreEqual(StationStatus.NoRoom, table.Status);

            chest.Contents.TryRemove(Items.Dish(0), 10);
            Assert.IsTrue(StoragePool.ChestRoomFor(chests, deer.Outputs, weights));
        }

        [Test]
        public void AButcherTable_IsSavedWithTheStorage()
        {
            var store = new Storages();
            Storage storage = store.Add(StorageKind.ButcherTable, new BlockPos(4, 9, 4), 1, "alice");
            StationJob job = storage.Station.Queue(Deer, 2, "alice", 1);
            storage.Station.AddLabour(job.Id, 100f);
            var stream = new MemoryStream();
            store.Write(stream);
            stream.Position = 0;

            Storages loaded = Storages.Read(stream);

            Assert.IsTrue(loaded.TryGet(storage.Id, out Storage read));
            Assert.AreEqual(StorageKind.ButcherTable, read.Kind);
            Assert.IsTrue(loaded.IsSolid(4, 9, 4));
            Assert.IsFalse(read.Station.BurnsFuel);
            Assert.AreEqual(1, read.Station.Jobs.Count);
            Assert.AreEqual(200f, read.Station.Jobs[0].LabourLeft, 1e-3f);
            Assert.AreEqual(Items.Carcass(Deer), read.Station.Jobs[0].Held()[0].Key);
        }

        [Test]
        public void TheButcherTable_IsCraftedAndPlacedLikeACampfire()
        {
            Assert.IsTrue(Items.IsStorage(Items.ButcherTable));
            Assert.IsTrue(StorageRules.TryKindOf(Items.ButcherTable, out StorageKind kind));
            Assert.AreEqual(StorageKind.ButcherTable, kind);
            Assert.AreEqual(Items.ButcherTable, StorageRules.ItemOf(kind));
            Assert.IsTrue(StorageRules.IsStation(kind));
            Assert.IsTrue(StorageRules.IsSingleCell(kind));
            Assert.IsFalse(StorageRules.IsStation(StorageKind.Chest));
            Assert.IsFalse(Recipes.All.Any(recipe => recipe.Output == Items.ButcherTable), "made at a carpentry table");
            Assert.IsTrue(Recipes.Carpentry.Any(recipe => recipe.Output == Items.ButcherTable));
            Assert.IsTrue(Recipes.All.Any(recipe => recipe.Output == Items.Knife));
        }
    }
}

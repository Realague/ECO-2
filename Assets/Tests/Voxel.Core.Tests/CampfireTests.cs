using System.Collections.Generic;
using System.IO;
using System.Text;
using NUnit.Framework;

namespace Voxel.Core.Tests
{
    public class CampfireTests
    {
        private static readonly int Oak = Items.WoodOf(0);
        private static readonly int Birch = Items.WoodOf(2);

        // Baked potato: 150 kcal of work, 15 s of cooking.
        private const int Potato = 0;

        private static bool Room(int item, int count) => true;

        private static bool NoRoom(int item, int count) => false;

        [Test]
        public void ADish_DoesNotCook_UntilItsWorkIsDone()
        {
            var fire = new Campfire();
            fire.AddFuel(Oak, 5);
            CookJob job = fire.Queue(Potato, "alice", null);

            Assert.IsNull(fire.Tick(60f, Room));
            Assert.AreEqual(CampfireStatus.NeedsLabour, fire.Status);
            Assert.AreEqual(5, fire.FuelCount, "no wood burns while nothing cooks");

            Assert.AreEqual(100f, fire.AddLabour(job.Id, 100f));
            Assert.AreEqual(50f, fire.AddLabour(job.Id, 100f), "no more than it still needs");
            Assert.IsTrue(job.HasLabour);
            Assert.AreEqual(0f, fire.AddLabour(job.Id, 100f));
        }

        [Test]
        public void Cooking_BurnsWood_OnlyWhileItCooks_AndFinishes()
        {
            var fire = new Campfire();
            fire.AddFuel(Oak, 2);
            CookJob job = fire.Queue(Potato, "alice", null);
            fire.AddLabour(job.Id, 1000f);

            Assert.IsNull(fire.Tick(10f, Room));
            Assert.AreEqual(CampfireStatus.Cooking, fire.Status);
            Assert.AreEqual(1, fire.FuelCount, "a log was put on");
            Assert.AreEqual(20f, fire.Burning, 1e-3f);

            CookJob done = fire.Tick(10f, Room);
            Assert.AreSame(job, done, "15 s of cooking");
            Assert.AreEqual(15f, fire.Burning, 1e-3f, "the log burnt only those 15 s");
            Assert.AreEqual(0, fire.Jobs.Count);

            fire.Tick(100f, Room);
            Assert.AreEqual(CampfireStatus.Idle, fire.Status);
            Assert.AreEqual(15f, fire.Burning, 1e-3f, "nothing burns with nothing queued");
        }

        [Test]
        public void WithoutRoomForTheDish_ItWaits_AndBurnsNothing()
        {
            var fire = new Campfire();
            fire.AddFuel(Oak, 2);
            CookJob job = fire.Queue(Potato, "alice", null);
            fire.AddLabour(job.Id, 1000f);

            Assert.IsNull(fire.Tick(60f, NoRoom));
            Assert.AreEqual(CampfireStatus.NoRoom, fire.Status);
            Assert.AreEqual(2, fire.FuelCount);
            Assert.AreEqual(15f, job.SecondsLeft, 1e-3f);
        }

        [Test]
        public void WithoutWood_ItWaits()
        {
            var fire = new Campfire();
            CookJob job = fire.Queue(Potato, "alice", null);
            fire.AddLabour(job.Id, 1000f);

            Assert.IsNull(fire.Tick(5f, Room));
            Assert.AreEqual(CampfireStatus.NoFuel, fire.Status);
            fire.AddFuel(Oak, 1);
            fire.Tick(5f, Room);
            Assert.AreEqual(CampfireStatus.Cooking, fire.Status);
            Assert.AreEqual(10f, job.SecondsLeft, 1e-3f);
        }

        [Test]
        public void ADishReady_CooksBefore_OneStillWaitingForWork()
        {
            var fire = new Campfire();
            fire.AddFuel(Oak, 3);
            CookJob first = fire.Queue(Potato, "alice", null);
            CookJob second = fire.Queue(Potato, "bob", null);
            fire.AddLabour(second.Id, 1000f);

            Assert.AreSame(second, fire.Next);
            Assert.AreSame(second, fire.Tick(20f, Room));
            Assert.AreEqual(1, fire.Jobs.Count);
            Assert.AreSame(first, fire.Jobs[0]);
            Assert.AreEqual(CampfireStatus.NeedsLabour, fire.Status);
        }

        [Test]
        public void Logs_AreTakenFromTheWoodHeldMost()
        {
            var fire = new Campfire();
            fire.AddFuel(Oak, 1);
            fire.AddFuel(Birch, 3);
            CookJob job = fire.Queue(Potato, "alice", null);
            fire.AddLabour(job.Id, 1000f);

            fire.Tick(1f, Room);
            Assert.AreEqual(2, fire.Fuel.Count(Birch));
            Assert.AreEqual(1, fire.Fuel.Count(Oak));
        }

        [Test]
        public void Limits_OfWoodAndQueue()
        {
            var fire = new Campfire();
            Assert.AreEqual(Campfire.MaxFuel, fire.AddFuel(Oak, 100));
            Assert.AreEqual(0, fire.AddFuel(Birch, 1));
            Assert.AreEqual(0, fire.AddFuel(Blocks.Granite, 1), "only wood burns");

            for (int i = 0; i < Campfire.MaxJobs; i++)
            {
                Assert.IsNotNull(fire.Queue(Potato, "alice", null));
            }

            Assert.IsNull(fire.Queue(Potato, "alice", null), "the queue is full");
            Assert.IsNull(new Campfire().Queue(Recipes.Cooking.Count, "alice", null), "no such dish");
        }

        [Test]
        public void ACampfire_IsSavedWithTheStorage()
        {
            var store = new Storages();
            Storage storage = store.Add(StorageKind.Campfire, new BlockPos(4, 9, 4), 1, "alice");
            Campfire fire = storage.Campfire;
            fire.AddFuel(Oak, 4);
            CookJob job = fire.Queue(Potato, "alice", new[] { new KeyValuePair<int, int>(Items.Produce(CropSpecies.Potato), 2) });
            fire.AddLabour(job.Id, 60f);
            var stream = new MemoryStream();
            store.Write(stream);
            stream.Position = 0;

            Storages loaded = Storages.Read(stream);

            Assert.IsTrue(loaded.TryGet(storage.Id, out Storage read));
            Assert.AreEqual(StorageKind.Campfire, read.Kind);
            Assert.AreEqual(1, read.CellCount);
            Assert.IsTrue(loaded.IsSolid(4, 9, 4));
            Assert.AreEqual(4, read.Campfire.FuelCount);
            Assert.AreEqual(1, read.Campfire.Jobs.Count);
            CookJob readJob = read.Campfire.Jobs[0];
            Assert.AreEqual(90f, readJob.LabourLeft, 1e-3f);
            Assert.AreEqual("alice", readJob.CreatorId);
            Assert.AreEqual(2, readJob.Taken[0].Value);
            Assert.AreNotEqual(job.Id, read.Campfire.Queue(Potato, "bob", null).Id, "dish ids are never reused");
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
                    Assert.IsTrue(Items.IsProduce(ingredient.Item));
                }
            }

            Assert.AreEqual("Baked potato", Items.NameOf(Items.Dish(0)));
            Assert.IsFalse(Items.IsValid(Items.Dish(Dishes.All.Count)), "no such dish");
            Assert.IsTrue(Items.IsValid(Items.Campfire));
            Assert.IsTrue(StorageRules.TryKindOf(Items.Campfire, out StorageKind kind));
            Assert.AreEqual(StorageKind.Campfire, kind);
            Assert.IsTrue(StorageRules.AcceptsInChest(Items.Dish(0)), "dishes go in chests");
        }
    }
}

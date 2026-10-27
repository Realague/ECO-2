using NUnit.Framework;

namespace Voxel.Core.Tests
{
    public class CraftQueueTests
    {
        private const int Shovel = 0;
        private const int Bow = 5;

        private static CraftRefusal Can(CraftOrder order) => CraftRefusal.None;

        private static CraftRefusal Missing(CraftOrder order) => CraftRefusal.Missing;

        [Test]
        public void AnOrder_WaitsForItsIngredients_ThenStarts()
        {
            var queue = new CraftQueue();
            CraftOrder order = queue.Add(Bow, null, 1);

            Assert.IsNull(queue.Update(10f, Missing, Can));
            Assert.AreEqual(CraftRefusal.Missing, order.Waiting);
            Assert.IsNull(queue.Active);

            Assert.IsNull(queue.Update(0.1f, Can, Can), "starting takes no time off");
            Assert.AreSame(order, queue.Active);
            Assert.AreEqual(CraftRefusal.None, order.Waiting);
            Assert.IsNull(queue.Update(3f, Missing, Can), "only its time runs once it is under way");
            Assert.AreSame(order, queue.Update(1f, Missing, Can));
            Assert.IsTrue(queue.IsEmpty);
        }

        [Test]
        public void SeveralItems_AreMadeOneAfterAnother()
        {
            var queue = new CraftQueue();
            CraftOrder order = queue.Add(Shovel, null, 3);
            int made = 0;
            for (int i = 0; i < 20; i++)
            {
                if (queue.Update(1.5f, Can, Can) != null) made++;
            }

            Assert.AreEqual(3, made);
            Assert.AreEqual(0, order.Count);
            Assert.IsTrue(queue.IsEmpty);
        }

        [Test]
        public void AnItemThatCannotBeMadeAtTheEnd_WaitsAndStartsOver()
        {
            var queue = new CraftQueue();
            CraftOrder order = queue.Add(Shovel, null, 2);
            queue.Update(0f, Can, Can);
            queue.Update(2f, Can, Can);

            Assert.IsNull(queue.Update(2f, Can, Missing));
            Assert.AreEqual(CraftRefusal.Missing, order.Waiting);
            Assert.IsFalse(order.Running);
            Assert.AreEqual(2, order.Count);
            Assert.AreEqual(3f, order.SecondsLeft, 1e-4f, "its time starts over");
        }

        [Test]
        public void TheFirstOrderThatCanStart_GoesFirst()
        {
            var queue = new CraftQueue();
            CraftOrder bow = queue.Add(Bow, null, 1);
            CraftOrder shovel = queue.Add(Shovel, null, 1);

            queue.Update(0f, o => o.Recipe == Bow ? CraftRefusal.Missing : CraftRefusal.None, Can);

            Assert.AreSame(shovel, queue.Active);
            Assert.AreEqual(CraftRefusal.Missing, bow.Waiting);
        }

        [Test]
        public void Limits_AndCancelling()
        {
            var queue = new CraftQueue();
            Assert.IsNull(queue.Add(Bow, null, 0));
            Assert.IsNull(queue.Add(Bow, null, CraftQueue.MaxCount + 1));
            Assert.IsNull(queue.Add(Recipes.All.Count, null, 1), "no such recipe");
            for (int i = 0; i < CraftQueue.MaxOrders; i++)
            {
                Assert.IsNotNull(queue.Add(Bow, new[] { Items.WoodOf(0) }, 1));
            }

            Assert.IsNull(queue.Add(Bow, null, 1), "the queue is full");
            Assert.AreEqual(Items.WoodOf(0), queue.Orders[0].Choices[0]);

            CraftOrder first = queue.Orders[0];
            queue.Update(0f, Can, Can);
            Assert.AreSame(first, queue.Active);
            long key = queue.StateKey;
            Assert.IsTrue(queue.Remove(first.Id));
            Assert.IsNull(queue.Active, "cancelling what is under way");
            Assert.AreNotEqual(key, queue.StateKey);
            Assert.IsFalse(queue.Remove(first.Id));
        }
    }
}

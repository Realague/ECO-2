using System;
using System.Collections.Generic;

namespace Voxel.Core
{
    // An order on a player's crafting queue: so many of a recipe (in Recipes.All), with the item chosen
    // for each ingredient, crafted one after another. Running: the next one is under way (SecondsLeft);
    // Waiting: why it could not start when last looked at.
    public sealed class CraftOrder
    {
        public int Id;
        public int Recipe;
        public int[] Choices;
        public int Count;
        public float SecondsLeft;
        public bool Running;
        public CraftRefusal Waiting;
    }

    // A player's crafting queue, in real time (CraftingSync passes the seconds; never the simulation's
    // clock). Orders may be queued without their ingredients: one runs at a time, the first that can
    // start (check), and its item is made when its time is up (make: takes the ingredients, gives it);
    // if it cannot be made then, it waits again and starts over. Nothing is taken before an item is
    // made, so an order is cancelled at no cost.
    public sealed class CraftQueue
    {
        public const int MaxOrders = 8;
        public const int MaxCount = 99;

        private readonly List<CraftOrder> orders = new List<CraftOrder>();
        private int nextId = 1;

        public IReadOnlyList<CraftOrder> Orders => orders;

        public bool IsEmpty => orders.Count == 0;

        public bool IsFull => orders.Count >= MaxOrders;

        // The order whose next item is under way, if any.
        public CraftOrder Active => orders.Find(o => o.Running);

        // Queues an order; null if the queue is full, the count out of range or there is no such recipe.
        public CraftOrder Add(int recipe, IReadOnlyList<int> choices, int count)
        {
            if (IsFull || count < 1 || count > MaxCount || !Recipes.TryGet(recipe, out Recipe crafted)) return null;

            var picked = new int[crafted.Ingredients.Count];
            for (int i = 0; i < picked.Length && choices != null && i < choices.Count; i++)
            {
                picked[i] = choices[i];
            }

            var order = new CraftOrder { Id = nextId++, Recipe = recipe, Choices = picked, Count = count, SecondsLeft = crafted.Seconds };
            orders.Add(order);
            return order;
        }

        public bool Remove(int id) => orders.RemoveAll(o => o.Id == id) > 0;

        public void Clear() => orders.Clear();

        // Crafts for a while. With nothing under way, each order is checked in turn and the first that
        // can start does; while an item is under way, only its time runs. Returns the order of which an
        // item was made, if one was, else null.
        public CraftOrder Update(float seconds, Func<CraftOrder, CraftRefusal> check, Func<CraftOrder, CraftRefusal> make)
        {
            CraftOrder active = Active;
            if (active == null)
            {
                foreach (CraftOrder order in orders)
                {
                    order.Waiting = active == null ? check(order) : CraftRefusal.None;
                    if (order.Waiting != CraftRefusal.None || active != null) continue;

                    active = order;
                    order.Running = true;
                    order.SecondsLeft = Recipes.All[order.Recipe].Seconds;
                }

                return null;
            }

            active.SecondsLeft -= seconds;
            if (active.SecondsLeft > 0f) return null;

            active.Running = false;
            active.SecondsLeft = Recipes.All[active.Recipe].Seconds;
            active.Waiting = make(active);
            if (active.Waiting != CraftRefusal.None) return null;

            active.Count--;
            if (active.Count <= 0) orders.Remove(active);
            return active;
        }

        // Changes whenever what a player is shown of the queue changes (orders, counts, the one under
        // way, why they wait), not as time runs.
        public long StateKey
        {
            get
            {
                long key = 17;
                foreach (CraftOrder order in orders)
                {
                    key = key * 31 + order.Id;
                    key = key * 31 + order.Count;
                    key = key * 31 + (order.Running ? 1 : 0);
                    key = key * 31 + (int)order.Waiting;
                }

                return key;
            }
        }
    }
}

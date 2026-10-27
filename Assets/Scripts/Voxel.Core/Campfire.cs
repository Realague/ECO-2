using System;
using System.Collections.Generic;
using System.IO;

namespace Voxel.Core
{
    // What a campfire is doing, for its window and its flames.
    public enum CampfireStatus : byte
    {
        Idle,
        Cooking,
        NeedsLabour,
        NoFuel,
        NoRoom,
        NoIngredients
    }

    // An order on a campfire: so many of a dish (its recipe in Recipes.Cooking), cooked one after
    // another. The work still to put in is the whole order's (calories); a dish cooks once its own share
    // is in. Stocked is how many dishes' ingredients were taken (given back if it is cancelled): those
    // that could be when it was queued, the others taken from the linked chests when their turn comes.
    public sealed class CookJob
    {
        public int Id;
        public int Recipe;
        public int Count;
        public int Stocked;
        public float LabourLeft;
        public float SecondsLeft;
        public string CreatorId;

        public bool HasLabour => LabourLeft <= 0f;

        // Whether the work of the dish at the front of the order is all in.
        public bool UnitPaid => LabourLeft <= Recipes.Cooking[Recipe].Calories * (Count - 1) + 1e-3f;

        // Whether the dish at the front has started cooking.
        public bool Started => SecondsLeft < Recipes.Cooking[Recipe].Seconds - 1e-4f;

        // What was taken for it and not cooked yet.
        public List<KeyValuePair<int, int>> Held()
        {
            var held = new List<KeyValuePair<int, int>>();
            if (Stocked <= 0) return held;

            foreach (Ingredient ingredient in Recipes.Cooking[Recipe].Ingredients)
            {
                held.Add(new KeyValuePair<int, int>(ingredient.Item, ingredient.Count * Stocked));
            }

            return held;
        }
    }

    // A campfire's wood and its queue of orders, in real time (the server ticks it every frame, never by
    // the simulation's clock). Players queue orders and put calories into them; the first dish whose
    // work is in and whose ingredients are there cooks, burning wood (a log lasts Fuels.SecondsOf it, a
    // new one taken when it is out) only while it cooks, and only while what it makes has somewhere to
    // go (canDeliver); else it waits.
    public sealed class Campfire
    {
        public const int MaxFuel = 20;
        public const int MaxJobs = 8;
        public const int MaxCount = 50;

        private readonly List<CookJob> jobs = new List<CookJob>();
        private int nextJobId = 1;

        // The wood laid by the fire (any wood), and how long the log alight still burns.
        public readonly Inventory Fuel = new Inventory();

        public float Burning { get; private set; }

        // How long the log alight burns in all (not saved: after a load, the longest it can be).
        public float LogSeconds { get; private set; } = Fuels.WoodSeconds;

        public CampfireStatus Status { get; private set; }

        public IReadOnlyList<CookJob> Jobs => jobs;

        public int FuelCount
        {
            get
            {
                int count = 0;
                foreach (KeyValuePair<int, int> entry in Fuel.Entries)
                {
                    count += entry.Value;
                }

                return count;
            }
        }

        public bool IsEmpty => jobs.Count == 0 && FuelCount == 0;

        // The order that cooks now, or would once it has wood and room: one whose dish has started, else
        // the first whose dish is paid for and has its ingredients.
        public CookJob Next
        {
            get
            {
                foreach (CookJob job in jobs)
                {
                    if (job.UnitPaid && job.Stocked > 0 && job.Started) return job;
                }

                foreach (CookJob job in jobs)
                {
                    if (job.UnitPaid && job.Stocked > 0) return job;
                }

                return null;
            }
        }

        // How long everything by the fire would burn, the log alight included, in seconds.
        public float FuelSeconds
        {
            get
            {
                float seconds = Burning;
                foreach (KeyValuePair<int, int> entry in Fuel.Entries)
                {
                    seconds += Fuels.SecondsOf(entry.Key) * entry.Value;
                }

                return seconds;
            }
        }

        // Lays fuel by the fire, what fits. Returns how many.
        public int AddFuel(int wood, int count)
        {
            if (!Fuels.IsFuel(wood) || count <= 0) return 0;

            int added = Math.Min(count, MaxFuel - FuelCount);
            if (added > 0) Fuel.Add(wood, added);
            return added;
        }

        // Takes wood back from the fire, what it has. Returns how many.
        public int TakeFuel(int wood, int count)
        {
            int taken = Math.Min(count, Fuel.Count(wood));
            if (taken > 0) Fuel.TryRemove(wood, taken);
            return Math.Max(0, taken);
        }

        // Queues an order of count dishes, stocked of them with their ingredients taken; null if the
        // queue is full, the count out of range or there is no such recipe.
        public CookJob Queue(int recipe, int count, string creatorId, int stocked)
        {
            if (jobs.Count >= MaxJobs || recipe < 0 || recipe >= Recipes.Cooking.Count || count < 1 || count > MaxCount) return null;

            Recipe cooking = Recipes.Cooking[recipe];
            var job = new CookJob
            {
                Id = nextJobId++,
                Recipe = recipe,
                Count = count,
                Stocked = Math.Max(0, Math.Min(count, stocked)),
                LabourLeft = cooking.Calories * count,
                SecondsLeft = cooking.Seconds,
                CreatorId = creatorId ?? ""
            };
            jobs.Add(job);
            return job;
        }

        public bool TryGetJob(int id, out CookJob job)
        {
            job = jobs.Find(j => j.Id == id);
            return job != null;
        }

        // Puts calories into an order, no more than it still needs. Returns how many went in.
        public float AddLabour(int jobId, float calories)
        {
            if (calories <= 0f || !TryGetJob(jobId, out CookJob job)) return 0f;

            float added = Math.Min(calories, job.LabourLeft);
            job.LabourLeft -= added;
            if (job.LabourLeft < 1e-3f) job.LabourLeft = 0f;
            return added;
        }

        public bool Remove(int jobId) => jobs.RemoveAll(j => j.Id == jobId) > 0;

        // Cooks for a while. A dish paid for whose ingredients were not taken asks for them
        // (takeIngredients: one dish's, all or nothing). Returns the order of which a dish was finished,
        // if one was (at most one a call, so that the caller delivers it before the next asks for room),
        // else null.
        public CookJob Tick(float seconds, Func<int, int, bool> canDeliver, Func<int, bool> takeIngredients = null)
        {
            CookJob job = Next;
            if (job == null)
            {
                bool paid = false;
                foreach (CookJob candidate in jobs)
                {
                    if (!candidate.UnitPaid) continue;

                    paid = true;
                    if (takeIngredients == null || !takeIngredients(candidate.Recipe)) continue;

                    candidate.Stocked = 1;
                    job = candidate;
                    break;
                }

                if (job == null)
                {
                    Status = paid ? CampfireStatus.NoIngredients : jobs.Count > 0 ? CampfireStatus.NeedsLabour : CampfireStatus.Idle;
                    return null;
                }
            }

            Recipe recipe = Recipes.Cooking[job.Recipe];
            if (canDeliver != null && !canDeliver(recipe.Output, recipe.OutputCount))
            {
                Status = CampfireStatus.NoRoom;
                return null;
            }

            while (seconds > 0f)
            {
                if (Burning <= 0f)
                {
                    int log = TakeLog();
                    if (log == Items.None)
                    {
                        Status = CampfireStatus.NoFuel;
                        return null;
                    }

                    LogSeconds = Fuels.SecondsOf(log);
                    Burning += LogSeconds;
                }

                float step = Math.Min(seconds, Math.Min(Burning, job.SecondsLeft));
                Burning -= step;
                job.SecondsLeft -= step;
                seconds -= step;
                if (job.SecondsLeft <= 1e-4f)
                {
                    job.Count--;
                    job.Stocked--;
                    job.SecondsLeft = recipe.Seconds;
                    if (job.Count <= 0) jobs.Remove(job);
                    Status = Next != null ? CampfireStatus.Cooking : jobs.Count > 0 ? CampfireStatus.NeedsLabour : CampfireStatus.Idle;
                    return job;
                }
            }

            Status = CampfireStatus.Cooking;
            return null;
        }

        // Takes a log from the wood held most of, the lowest item first when even; Items.None if none.
        private int TakeLog()
        {
            int best = Items.None;
            int most = 0;
            foreach (KeyValuePair<int, int> entry in Fuel.Entries)
            {
                if (entry.Value > most || (entry.Value == most && entry.Value > 0 && entry.Key < best))
                {
                    best = entry.Key;
                    most = entry.Value;
                }
            }

            return most > 0 && Fuel.TryRemove(best) ? best : Items.None;
        }

        public void Write(BinaryWriter writer)
        {
            writer.Write(nextJobId);
            writer.Write(Burning);
            writer.Write(Fuel.DistinctCount);
            foreach (KeyValuePair<int, int> entry in Fuel.Entries)
            {
                writer.Write(entry.Key);
                writer.Write(entry.Value);
            }

            writer.Write(jobs.Count);
            foreach (CookJob job in jobs)
            {
                writer.Write(job.Id);
                writer.Write(job.Recipe);
                writer.Write(job.Count);
                writer.Write(job.Stocked);
                writer.Write(job.LabourLeft);
                writer.Write(job.SecondsLeft);
                writer.Write(job.CreatorId);
            }
        }

        // Throws InvalidDataException if it cannot be a campfire. An order whose recipe no longer exists
        // is dropped. In format 2 a dish was an order of one, with what was taken for it.
        public void Read(BinaryReader reader, int format)
        {
            nextJobId = reader.ReadInt32();
            Burning = Math.Max(0f, reader.ReadSingle());
            LogSeconds = Math.Max(Burning, Fuels.WoodSeconds);
            int fuel = reader.ReadInt32();
            if (fuel < 0) throw new InvalidDataException("Invalid campfire wood.");

            for (int i = 0; i < fuel; i++)
            {
                int item = Items.Upgrade(reader.ReadInt32());
                int logs = reader.ReadInt32();
                if (logs > 0 && Fuels.IsFuel(item)) Fuel.Add(item, logs);
            }

            int queued = reader.ReadInt32();
            if (queued < 0 || queued > MaxJobs) throw new InvalidDataException("Invalid campfire queue.");

            for (int i = 0; i < queued; i++)
            {
                var job = new CookJob { Id = reader.ReadInt32(), Recipe = reader.ReadInt32() };
                if (format >= 3)
                {
                    job.Count = reader.ReadInt32();
                    job.Stocked = reader.ReadInt32();
                }

                job.LabourLeft = Math.Max(0f, reader.ReadSingle());
                job.SecondsLeft = Math.Max(0f, reader.ReadSingle());
                job.CreatorId = reader.ReadString();
                if (format < 3)
                {
                    int taken = reader.ReadInt32();
                    if (taken < 0) throw new InvalidDataException("Invalid campfire dish.");

                    for (int t = 0; t < taken; t++)
                    {
                        reader.ReadInt32();
                        reader.ReadInt32();
                    }

                    job.Count = 1;
                    job.Stocked = taken > 0 ? 1 : 0;
                }

                if (job.Count < 1 || job.Count > MaxCount || job.Stocked < 0 || job.Stocked > job.Count) throw new InvalidDataException("Invalid campfire order.");

                if (job.Recipe >= 0 && job.Recipe < Recipes.Cooking.Count) jobs.Add(job);
                if (job.Id >= nextJobId) nextJobId = job.Id + 1;
            }
        }
    }
}

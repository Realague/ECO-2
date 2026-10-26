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
        NoRoom
    }

    // A dish waiting on a campfire: its recipe (in Recipes.Cooking), the work still to put in (calories),
    // how long it still cooks once that is done, who queued it, and what was taken for it (given back
    // if it is cancelled).
    public sealed class CookJob
    {
        public int Id;
        public int Recipe;
        public float LabourLeft;
        public float SecondsLeft;
        public string CreatorId;
        public readonly List<KeyValuePair<int, int>> Taken = new List<KeyValuePair<int, int>>();

        public bool HasLabour => LabourLeft <= 0f;
    }

    // A campfire's wood and its queue of dishes, in real time (the server ticks it every frame, never by
    // the simulation's clock). Players queue dishes (their ingredients already taken) and put calories
    // into them; the first dish whose work is done cooks, burning wood (a log lasts SecondsPerWood, a
    // new one taken when it is out) only while it cooks, and only while what it makes has somewhere to
    // go (canDeliver); else it waits.
    public sealed class Campfire
    {
        public const int MaxFuel = 20;
        public const int MaxJobs = 8;
        public const float SecondsPerWood = 30f;

        private readonly List<CookJob> jobs = new List<CookJob>();
        private int nextJobId = 1;

        // The wood laid by the fire (any wood), and how long the log alight still burns.
        public readonly Inventory Fuel = new Inventory();

        public float Burning { get; private set; }

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

        // The dish that cooks now, or would once it has wood and room: the first whose work is done.
        public CookJob Next
        {
            get
            {
                foreach (CookJob job in jobs)
                {
                    if (job.HasLabour) return job;
                }

                return null;
            }
        }

        // Lays wood by the fire, what fits. Returns how many.
        public int AddFuel(int wood, int count)
        {
            if (!Items.IsWood(wood) || count <= 0) return 0;

            int added = Math.Min(count, MaxFuel - FuelCount);
            if (added > 0) Fuel.Add(wood, added);
            return added;
        }

        // Queues a dish whose ingredients were taken; null if the queue is full or there is no such recipe.
        public CookJob Queue(int recipe, string creatorId, IEnumerable<KeyValuePair<int, int>> taken)
        {
            if (jobs.Count >= MaxJobs || recipe < 0 || recipe >= Recipes.Cooking.Count) return null;

            Recipe cooking = Recipes.Cooking[recipe];
            var job = new CookJob { Id = nextJobId++, Recipe = recipe, LabourLeft = cooking.Calories, SecondsLeft = cooking.Seconds, CreatorId = creatorId ?? "" };
            if (taken != null) job.Taken.AddRange(taken);
            jobs.Add(job);
            return job;
        }

        public bool TryGetJob(int id, out CookJob job)
        {
            job = jobs.Find(j => j.Id == id);
            return job != null;
        }

        // Puts calories into a dish, no more than it still needs. Returns how many went in.
        public float AddLabour(int jobId, float calories)
        {
            if (calories <= 0f || !TryGetJob(jobId, out CookJob job)) return 0f;

            float added = Math.Min(calories, job.LabourLeft);
            job.LabourLeft -= added;
            if (job.LabourLeft < 1e-3f) job.LabourLeft = 0f;
            return added;
        }

        public bool Remove(int jobId) => jobs.RemoveAll(j => j.Id == jobId) > 0;

        // Cooks for a while. Returns the dish finished, if one was (at most one a call, so that the
        // caller delivers it before the next asks for room), else null.
        public CookJob Tick(float seconds, Func<int, int, bool> canDeliver)
        {
            CookJob job = Next;
            if (job == null)
            {
                Status = jobs.Count > 0 ? CampfireStatus.NeedsLabour : CampfireStatus.Idle;
                return null;
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
                    if (!TakeLog())
                    {
                        Status = CampfireStatus.NoFuel;
                        return null;
                    }

                    Burning += SecondsPerWood;
                }

                float step = Math.Min(seconds, Math.Min(Burning, job.SecondsLeft));
                Burning -= step;
                job.SecondsLeft -= step;
                seconds -= step;
                if (job.SecondsLeft <= 1e-4f)
                {
                    jobs.Remove(job);
                    Status = Next != null ? CampfireStatus.Cooking : jobs.Count > 0 ? CampfireStatus.NeedsLabour : CampfireStatus.Idle;
                    return job;
                }
            }

            Status = CampfireStatus.Cooking;
            return null;
        }

        // Takes a log from the wood held most of, the lowest item first when even.
        private bool TakeLog()
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

            return most > 0 && Fuel.TryRemove(best);
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
                writer.Write(job.LabourLeft);
                writer.Write(job.SecondsLeft);
                writer.Write(job.CreatorId);
                writer.Write(job.Taken.Count);
                foreach (KeyValuePair<int, int> taken in job.Taken)
                {
                    writer.Write(taken.Key);
                    writer.Write(taken.Value);
                }
            }
        }

        // Throws InvalidDataException if it cannot be a campfire. A dish whose recipe no longer exists
        // is dropped, and items that no longer exist with it.
        public void Read(BinaryReader reader)
        {
            nextJobId = reader.ReadInt32();
            Burning = Math.Max(0f, reader.ReadSingle());
            int fuel = reader.ReadInt32();
            if (fuel < 0) throw new InvalidDataException("Invalid campfire wood.");

            for (int i = 0; i < fuel; i++)
            {
                int item = Items.Upgrade(reader.ReadInt32());
                int logs = reader.ReadInt32();
                if (logs > 0 && Items.IsWood(item)) Fuel.Add(item, logs);
            }

            int queued = reader.ReadInt32();
            if (queued < 0 || queued > MaxJobs) throw new InvalidDataException("Invalid campfire queue.");

            for (int i = 0; i < queued; i++)
            {
                var job = new CookJob
                {
                    Id = reader.ReadInt32(),
                    Recipe = reader.ReadInt32(),
                    LabourLeft = Math.Max(0f, reader.ReadSingle()),
                    SecondsLeft = Math.Max(0f, reader.ReadSingle()),
                    CreatorId = reader.ReadString()
                };
                int taken = reader.ReadInt32();
                if (taken < 0) throw new InvalidDataException("Invalid campfire dish.");

                for (int t = 0; t < taken; t++)
                {
                    int item = Items.Upgrade(reader.ReadInt32());
                    int amount = reader.ReadInt32();
                    if (amount > 0 && Items.IsValid(item)) job.Taken.Add(new KeyValuePair<int, int>(item, amount));
                }

                if (job.Recipe >= 0 && job.Recipe < Recipes.Cooking.Count) jobs.Add(job);
                if (job.Id >= nextJobId) nextJobId = job.Id + 1;
            }
        }
    }
}

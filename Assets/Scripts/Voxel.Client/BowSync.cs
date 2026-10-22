using System;
using System.Collections.Generic;
using System.IO;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using Voxel.Core;

namespace Voxel.Client
{
    // Hunting with the bow. The local player draws (holding the left button, full draw in
    // FullDrawSeconds) and shoots one arrow: their machine flies it at once (ArrowFlight) and tests
    // it against the animals it shows, the head first (twice the damage); the server takes the
    // arrow from their inventory, works the same flight out on its world, shows it to the other
    // players and, once the shooter says what it hit, checks that the animal was near the flight
    // and wounds it (Ecosystem.Damage, a carcass if that kills it). The arrow then lies where it
    // stopped, or at the animal's feet, until a player picks it up with a right click; the server
    // keeps those arrows (StuckArrows) and saves them with the world (arrows.dat).
    public class BowSync : MonoBehaviour
    {
        private const string ShootMessage = "Bow.Shoot";
        private const string ShotMessage = "Bow.Shot";
        private const string LandedMessage = "Bow.Landed";
        private const string ArrowsMessage = "Bow.Arrows";
        private const string AddedMessage = "Bow.Added";
        private const string RemovedMessage = "Bow.Removed";
        private const string PickupMessage = "Bow.Pickup";
        private const string SaveFileName = "arrows.dat";

        public const float FullDrawSeconds = 0.8f;
        public const float ArrowDamage = 10f;
        public const float HeadMultiplier = 2f;

        // The server waits this long after an arrow should have landed for the shooter's verdict.
        private const float VerdictSeconds = 10f;

        // An arrow is shot from the shooter's eyes, give or take this (the camera sits at the head).
        private const float OriginTolerance = 2f;

        // How close a pointer ray must pass to an arrow to pick it up.
        private const float PickRadius = 0.3f;

        [SerializeField] private NetworkManager networkManager;
        [SerializeField] private InventorySync inventorySync;
        [SerializeField] private EcosystemSync ecosystemSync;
        [SerializeField] private WorldSync worldSync;
        [SerializeField] private Material shaftMaterial;
        [SerializeField] private Material headMaterial;
        [SerializeField] private Material fletchingMaterial;

        private sealed class Flight
        {
            public Vector3 Origin;
            public Vector3 Velocity;
            public float Start;
            public float Duration;
            public Vector3 Last;
            public bool Mine;
            public int Shot;
        }

        private sealed class PendingShot
        {
            public System.Numerics.Vector3 Velocity;
            public float Power;
            public readonly List<System.Numerics.Vector3> Path = new List<System.Numerics.Vector3>();
            public bool Stopped;
            public System.Numerics.Vector3 Landing;
            public float Time;
            public float Deadline;
        }

        // Every machine: the arrows lying in the world, and those in flight.
        private readonly Dictionary<int, StuckArrow> shown = new Dictionary<int, StuckArrow>();
        private readonly List<Flight> flights = new List<Flight>();

        // Server side.
        private readonly Dictionary<(ulong, int), PendingShot> pending = new Dictionary<(ulong, int), PendingShot>();
        private readonly List<(ulong, int)> expired = new List<(ulong, int)>();
        private StuckArrows store;
        private string savePath;

        private ArrowViews views;
        private float drawStart = -1f;
        private int nextShot = 1;
        private bool running;

        // How far the bow is drawn, from 0 to 1, or -1 when it is not being drawn (for the HUD).
        public float DrawPower => drawStart < 0f ? -1f : Mathf.Clamp01((Time.time - drawStart) / FullDrawSeconds);

        private bool IsServer => store != null;

        private CustomMessagingManager Messaging => networkManager.CustomMessagingManager;

        private void Awake()
        {
            if (!Application.isBatchMode && shaftMaterial != null) views = new ArrowViews(shaftMaterial, headMaterial, fletchingMaterial);
        }

        // To call right after the server or host has started, with the world's folder.
        public void StartServer(string directory)
        {
            running = true;
            savePath = Path.Combine(directory, SaveFileName);
            store = Load(savePath);
            foreach (StuckArrow arrow in store.All)
            {
                shown[arrow.Id] = arrow;
            }

            Messaging.RegisterNamedMessageHandler(ShootMessage, OnShoot);
            Messaging.RegisterNamedMessageHandler(LandedMessage, OnLanded);
            Messaging.RegisterNamedMessageHandler(PickupMessage, OnPickup);
            networkManager.OnClientConnectedCallback += OnClientConnected;
        }

        // To call right after a client's connection has been launched.
        public void StartClient()
        {
            running = true;
            Messaging.RegisterNamedMessageHandler(ShotMessage, OnShot);
            Messaging.RegisterNamedMessageHandler(ArrowsMessage, OnArrows);
            Messaging.RegisterNamedMessageHandler(AddedMessage, OnAdded);
            Messaging.RegisterNamedMessageHandler(RemovedMessage, OnRemoved);
        }

        public void Stop()
        {
            if (!running) return;

            if (IsServer) networkManager.OnClientConnectedCallback -= OnClientConnected;

            store = null;
            pending.Clear();
            shown.Clear();
            flights.Clear();
            drawStart = -1f;
            running = false;
        }

        // Server side: writes the arrows lying in the world next to the other saves.
        public void Save()
        {
            if (store == null) return;

            try
            {
                store.SaveToFile(savePath);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                Debug.LogError($"Failed to save the arrows: {e.Message} ({savePath})");
            }
        }

        // The local player starts drawing the bow (they hold it and have an arrow).
        public void BeginDraw()
        {
            if (running) drawStart = Time.time;
        }

        public void CancelDraw()
        {
            drawStart = -1f;
        }

        // The local player lets go: one arrow is shot from origin toward direction, as hard as the
        // bow was drawn.
        public void Release(Vector3 origin, Vector3 direction)
        {
            if (!running || drawStart < 0f) return;

            float power = DrawPower;
            drawStart = -1f;
            if (!inventorySync.Inventory.Has(Items.Arrow)) return;

            int shot = nextShot++;
            StartFlight(origin, ArrowFlight.VelocityOf(direction.ToNumerics(), power).ToUnity(), true, shot);

            if (IsServer)
            {
                Shoot(networkManager.LocalClientId, shot, origin, direction, power);
                return;
            }

            using (var writer = new FastBufferWriter(sizeof(int) + 7 * sizeof(float), Allocator.Temp))
            {
                writer.WriteValueSafe(shot);
                WriteVector(writer, origin);
                WriteVector(writer, direction);
                writer.WriteValueSafe(power);
                Messaging.SendNamedMessage(ShootMessage, NetworkManager.ServerClientId, writer, NetworkDelivery.ReliableSequenced);
            }
        }

        // Right click: picks up the arrow the local player points at, at most maxDistance away.
        // Returns false if they point at none.
        public bool TryPickupAimed(Vector3 origin, Vector3 direction, float maxDistance)
        {
            int best = 0;
            float nearest = maxDistance;
            foreach (StuckArrow arrow in shown.Values)
            {
                // Around the middle of the arrow, which lies behind its point.
                Vector3 back = -arrow.Direction.ToUnity().normalized;
                Vector3 middle = arrow.Position.ToUnity() + back * (ArrowViews.Length * 0.4f);
                var min = (middle - Vector3.one * PickRadius).ToNumerics();
                var max = (middle + Vector3.one * PickRadius).ToNumerics();
                float distance = ArrowFlight.SegmentEntry(origin.ToNumerics(), (origin + direction * maxDistance).ToNumerics(), min, max);
                if (distance >= 0f && distance < nearest)
                {
                    nearest = distance;
                    best = arrow.Id;
                }
            }

            if (best == 0) return false;

            if (IsServer)
            {
                Pickup(networkManager.LocalClientId, best);
                return true;
            }

            using (var writer = new FastBufferWriter(sizeof(int), Allocator.Temp))
            {
                writer.WriteValueSafe(best);
                Messaging.SendNamedMessage(PickupMessage, NetworkManager.ServerClientId, writer, NetworkDelivery.ReliableSequenced);
            }

            return true;
        }

        private void Update()
        {
            if (!running) return;

            FlyArrows();
            if (IsServer) ExpireShots();
        }

        private void LateUpdate()
        {
            if (views == null || !running) return;

            foreach (StuckArrow arrow in shown.Values)
            {
                views.Add(arrow.Position.ToUnity(), arrow.Direction.ToUnity());
            }

            float now = Time.time;
            foreach (Flight flight in flights)
            {
                float t = Mathf.Min(now - flight.Start, flight.Duration);
                views.Add(ArrowFlight.PositionAt(flight.Origin.ToNumerics(), flight.Velocity.ToNumerics(), t).ToUnity(),
                    ArrowFlight.VelocityAt(flight.Velocity.ToNumerics(), t).ToUnity());
            }

            views.Render();
        }

        // Moves the arrows in flight. The local player's own are tested against the animals they
        // see; each tells the server what it hit, or that it hit nothing, when its flight ends.
        private void FlyArrows()
        {
            float now = Time.time;
            for (int i = flights.Count - 1; i >= 0; i--)
            {
                Flight flight = flights[i];
                float t = Mathf.Min(now - flight.Start, flight.Duration);
                Vector3 position = ArrowFlight.PositionAt(flight.Origin.ToNumerics(), flight.Velocity.ToNumerics(), t).ToUnity();
                if (flight.Mine && ecosystemSync.TryHitArrow(flight.Last, position, out int animalId, out bool inHead, out _))
                {
                    SendLanded(flight.Shot, animalId, inHead);
                    flights.RemoveAt(i);
                    continue;
                }

                flight.Last = position;
                if (now - flight.Start < flight.Duration) continue;

                if (flight.Mine) SendLanded(flight.Shot, 0, false);
                flights.RemoveAt(i);
            }
        }

        private void StartFlight(Vector3 origin, Vector3 velocity, bool mine, int shot)
        {
            World world = worldSync.World;
            float duration = ArrowFlight.MaxTime;
            if (world != null) ArrowFlight.Trace(world, origin.ToNumerics(), velocity.ToNumerics(), out _, out duration);
            flights.Add(new Flight { Origin = origin, Velocity = velocity, Start = Time.time, Duration = duration, Last = origin, Mine = mine, Shot = shot });
        }

        private void SendLanded(int shot, int animalId, bool inHead)
        {
            if (IsServer)
            {
                Land(networkManager.LocalClientId, shot, animalId, inHead);
                return;
            }

            using (var writer = new FastBufferWriter(2 * sizeof(int) + sizeof(byte), Allocator.Temp))
            {
                writer.WriteValueSafe(shot);
                writer.WriteValueSafe(animalId);
                writer.WriteValueSafe((byte)(inHead ? 1 : 0));
                Messaging.SendNamedMessage(LandedMessage, NetworkManager.ServerClientId, writer, NetworkDelivery.ReliableSequenced);
            }
        }

        // Server side: a player shoots. The arrow is taken from them, its flight worked out and shown
        // to the others; it lands once the shooter has said what it hit.
        private void Shoot(ulong clientId, int shot, Vector3 origin, Vector3 direction, float power)
        {
            if (!networkManager.ConnectedClients.TryGetValue(clientId, out NetworkClient client) || client.PlayerObject == null) return;
            if (!IsFinite(origin) || !IsFinite(direction) || direction.sqrMagnitude < 1e-6f || !(power >= 0f && power <= 1f)) return;

            Vector3 eyes = client.PlayerObject.transform.position + Vector3.up * PlayerRules.EyeHeight;
            if ((origin - eyes).sqrMagnitude > OriginTolerance * OriginTolerance) return;
            if (pending.ContainsKey((clientId, shot)) || !inventorySync.Owns(clientId, Items.Bow)) return;
            if (!inventorySync.TryConsume(clientId, Items.Arrow)) return;

            World world = worldSync.World;
            var flight = new PendingShot { Velocity = ArrowFlight.VelocityOf(direction.ToNumerics(), power), Power = power };
            flight.Stopped = ArrowFlight.Trace(world, origin.ToNumerics(), flight.Velocity, out flight.Landing, out flight.Time, flight.Path);
            flight.Deadline = Time.unscaledTime + flight.Time + VerdictSeconds;
            pending[(clientId, shot)] = flight;

            Vector3 velocity = flight.Velocity.ToUnity();
            if (clientId != networkManager.LocalClientId && networkManager.IsHost) StartFlight(origin, velocity, false, 0);
            foreach (ulong other in networkManager.ConnectedClientsIds)
            {
                if (other == clientId || other == networkManager.LocalClientId) continue;

                using (var writer = new FastBufferWriter(6 * sizeof(float), Allocator.Temp))
                {
                    WriteVector(writer, origin);
                    WriteVector(writer, velocity);
                    Messaging.SendNamedMessage(ShotMessage, other, writer, NetworkDelivery.ReliableSequenced);
                }
            }
        }

        // Server side: the shooter says what their arrow hit. A plausible hit wounds the animal and the
        // arrow falls at its feet; otherwise it lies where the server's flight stopped.
        private void Land(ulong clientId, int shot, int animalId, bool inHead)
        {
            if (!pending.TryGetValue((clientId, shot), out PendingShot flight)) return;

            pending.Remove((clientId, shot));
            float damage = ArrowDamage * ArrowFlight.ClampPower(flight.Power) * (inHead ? HeadMultiplier : 1f);
            if (animalId != 0 && ecosystemSync.TryWoundAnimal(animalId, flight.Path, damage, out Vector3 feet))
            {
                // On the ground at the animal's feet, pointing down the way it flew.
                Vector3 along = new Vector3(flight.Velocity.X, 0f, flight.Velocity.Z).normalized;
                AddArrow(feet + Vector3.up * 0.06f, along + Vector3.down * 0.25f);
                return;
            }

            if (flight.Stopped) AddArrow(flight.Landing.ToUnity(), ArrowFlight.VelocityAt(flight.Velocity, flight.Time).ToUnity());
        }

        // Server side: shooters who never said what their arrow hit (gone, or their message lost).
        private void ExpireShots()
        {
            expired.Clear();
            foreach (KeyValuePair<(ulong, int), PendingShot> entry in pending)
            {
                if (Time.unscaledTime > entry.Value.Deadline) expired.Add(entry.Key);
            }

            foreach ((ulong clientId, int shot) in expired)
            {
                Land(clientId, shot, 0, false);
            }
        }

        private void AddArrow(Vector3 point, Vector3 direction)
        {
            StuckArrow arrow = store.Add(point.ToNumerics(), direction.normalized.ToNumerics());
            shown[arrow.Id] = arrow;
            foreach (ulong clientId in networkManager.ConnectedClientsIds)
            {
                if (clientId == networkManager.LocalClientId) continue;

                using (var writer = new FastBufferWriter(sizeof(int) + 6 * sizeof(float), Allocator.Temp))
                {
                    WriteArrow(writer, arrow);
                    Messaging.SendNamedMessage(AddedMessage, clientId, writer, NetworkDelivery.ReliableSequenced);
                }
            }
        }

        // Server side: a player picks up an arrow within reach, if they can carry it.
        private void Pickup(ulong clientId, int arrowId)
        {
            if (!store.TryGet(arrowId, out StuckArrow arrow)) return;
            if (!networkManager.ConnectedClients.TryGetValue(clientId, out NetworkClient client) || client.PlayerObject == null) return;

            Vector3 eyes = client.PlayerObject.transform.position + Vector3.up * PlayerRules.EyeHeight;
            float reach = PlayerRules.Reach + BlockEditRules.ReachTolerance + ArrowViews.Length;
            if ((arrow.Position.ToUnity() - eyes).sqrMagnitude > reach * reach) return;

            if (inventorySync.RoomFor(clientId, Items.Arrow) <= 0)
            {
                inventorySync.TellFull(clientId, FullReason.Weight);
                return;
            }

            inventorySync.Give(clientId, Items.Arrow, 1);
            store.Remove(arrowId);
            shown.Remove(arrowId);
            foreach (ulong other in networkManager.ConnectedClientsIds)
            {
                if (other == networkManager.LocalClientId) continue;

                using (var writer = new FastBufferWriter(sizeof(int), Allocator.Temp))
                {
                    writer.WriteValueSafe(arrowId);
                    Messaging.SendNamedMessage(RemovedMessage, other, writer, NetworkDelivery.ReliableSequenced);
                }
            }
        }

        // Server side: a joining player is sent every arrow lying in the world.
        private void OnClientConnected(ulong clientId)
        {
            if (clientId == networkManager.LocalClientId) return;

            using (var writer = new FastBufferWriter(sizeof(int) + store.Count * (sizeof(int) + 6 * sizeof(float)), Allocator.Temp))
            {
                writer.WriteValueSafe(store.Count);
                foreach (StuckArrow arrow in store.All)
                {
                    WriteArrow(writer, arrow);
                }

                Messaging.SendNamedMessage(ArrowsMessage, clientId, writer, NetworkDelivery.ReliableFragmentedSequenced);
            }
        }

        private void OnShoot(ulong senderId, FastBufferReader reader)
        {
            reader.ReadValueSafe(out int shot);
            Vector3 origin = ReadVector(reader);
            Vector3 direction = ReadVector(reader);
            reader.ReadValueSafe(out float power);
            Shoot(senderId, shot, origin, direction, power);
        }

        private void OnLanded(ulong senderId, FastBufferReader reader)
        {
            reader.ReadValueSafe(out int shot);
            reader.ReadValueSafe(out int animalId);
            reader.ReadValueSafe(out byte inHead);
            Land(senderId, shot, animalId, inHead != 0);
        }

        private void OnPickup(ulong senderId, FastBufferReader reader)
        {
            reader.ReadValueSafe(out int arrowId);
            Pickup(senderId, arrowId);
        }

        private void OnShot(ulong senderId, FastBufferReader reader)
        {
            Vector3 origin = ReadVector(reader);
            Vector3 velocity = ReadVector(reader);
            if (IsFinite(origin) && IsFinite(velocity)) StartFlight(origin, velocity, false, 0);
        }

        private void OnArrows(ulong senderId, FastBufferReader reader)
        {
            reader.ReadValueSafe(out int count);
            shown.Clear();
            for (int i = 0; i < count; i++)
            {
                StuckArrow arrow = ReadArrow(reader);
                shown[arrow.Id] = arrow;
            }
        }

        private void OnAdded(ulong senderId, FastBufferReader reader)
        {
            StuckArrow arrow = ReadArrow(reader);
            shown[arrow.Id] = arrow;
        }

        private void OnRemoved(ulong senderId, FastBufferReader reader)
        {
            reader.ReadValueSafe(out int arrowId);
            shown.Remove(arrowId);
        }

        private static StuckArrows Load(string path)
        {
            if (!File.Exists(path)) return new StuckArrows();

            try
            {
                return StuckArrows.LoadFromFile(path);
            }
            catch (Exception e) when (e is IOException || e is InvalidDataException || e is UnauthorizedAccessException)
            {
                Debug.LogWarning($"The arrows could not be loaded ({e.Message}): the world starts without any.");
                return new StuckArrows();
            }
        }

        private static bool IsFinite(Vector3 v) => float.IsFinite(v.x) && float.IsFinite(v.y) && float.IsFinite(v.z);

        private static void WriteArrow(FastBufferWriter writer, StuckArrow arrow)
        {
            writer.WriteValueSafe(arrow.Id);
            WriteVector(writer, arrow.Position.ToUnity());
            WriteVector(writer, arrow.Direction.ToUnity());
        }

        private static StuckArrow ReadArrow(FastBufferReader reader)
        {
            reader.ReadValueSafe(out int id);
            Vector3 position = ReadVector(reader);
            Vector3 direction = ReadVector(reader);
            return new StuckArrow(id, position.ToNumerics(), direction.ToNumerics());
        }

        private static void WriteVector(FastBufferWriter writer, Vector3 v)
        {
            writer.WriteValueSafe(v.x);
            writer.WriteValueSafe(v.y);
            writer.WriteValueSafe(v.z);
        }

        private static Vector3 ReadVector(FastBufferReader reader)
        {
            reader.ReadValueSafe(out float x);
            reader.ReadValueSafe(out float y);
            reader.ReadValueSafe(out float z);
            return new Vector3(x, y, z);
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using Voxel.Core;

namespace Voxel.Client
{
    // Why the server refused something done with storage, said to the player.
    public enum StorageRefusal : byte
    {
        NotYours = 1,
        NotEmpty = 2,
        NoRoom = 3,
        PlantInTheWay = 4,
        ChestFull = 5,
        StockpileFull = 6,
        NotAccepted = 7
    }

    // Chests and stockpiles placed in the world (StorageRules, Storages). The server keeps them and
    // saves them with the world (storage.dat): it places one where a player asks if they own it and the
    // place is free (no block, player, animal, plant or log in it, flat ground for a stockpile), lets
    // its owner and the players they allow open it, moves items between it and their bags (a chest by
    // weight, a stockpile by piles), lets the owner allow others and pick it up once empty. Every
    // machine knows where each one stands, whose it is, whether it is open and a stockpile's piles (to
    // draw them and collide with them); only the players who opened one get what a chest holds and its
    // access list. Nothing sprouts where storage stands (Ecosystem.BlockColumn).
    public class StorageSync : MonoBehaviour
    {
        private const string AllMessage = "Store.All";
        private const string AddedMessage = "Store.Added";
        private const string RemovedMessage = "Store.Removed";
        private const string StateMessage = "Store.State";
        private const string PlaceMessage = "Store.Place";
        private const string OpenMessage = "Store.Open";
        private const string CloseMessage = "Store.Close";
        private const string ContentsMessage = "Store.Contents";
        private const string ClosedMessage = "Store.Closed";
        private const string MoveMessage = "Store.Move";
        private const string AccessMessage = "Store.Access";
        private const string PickupMessage = "Store.Pickup";
        private const string RefusedMessage = "Store.Refused";
        private const string SaveFileName = "storage.dat";

        // A chest's box in its cell, for aiming.
        public const float ChestInset = 0.1f;
        public const float ChestHeight = 0.8f;

        // How often the server checks that open storage is still in reach of who opened it.
        private const float ReachCheckSeconds = 0.5f;

        [SerializeField] private NetworkManager networkManager;
        [SerializeField] private WorldSync worldSync;
        [SerializeField] private InventorySync inventorySync;
        [SerializeField] private EcosystemSync ecosystemSync;
        [SerializeField] private LogSync logSync;
        [SerializeField] private Material woodMaterial;
        [SerializeField] private Material ironMaterial;
        [SerializeField] private VoxelWorld voxelWorld;

        // What every machine shows of a storage besides where it stands.
        private sealed class Shown
        {
            public string OwnerName = "";
            public bool Yours;
            public bool Open;
        }

        // A player on the access list of an opened storage.
        public readonly struct AccessEntry
        {
            public readonly string Name;
            public readonly bool Online;
            public readonly bool Allowed;

            public AccessEntry(string name, bool online, bool allowed)
            {
                Name = name;
                Online = online;
                Allowed = allowed;
            }
        }

        // Every machine: the storage in the world (on the server, the saved store itself).
        private Storages shown = new Storages();
        private readonly Dictionary<int, Shown> details = new Dictionary<int, Shown>();

        // Server side.
        private Storages store;
        private PlayerRegistry registry;
        private World serverWorld;
        private string savePath;
        private readonly Dictionary<int, HashSet<ulong>> viewers = new Dictionary<int, HashSet<ulong>>();
        private readonly Dictionary<ulong, int> openedBy = new Dictionary<ulong, int>();
        private readonly List<ulong> leaving = new List<ulong>();
        private float nextReachCheck;

        // The storage the local player has open, as the server last sent it.
        private readonly Inventory openedContents = new Inventory();
        private readonly StockpilePiles openedPiles = new StockpilePiles();
        private readonly List<AccessEntry> openedAccess = new List<AccessEntry>();

        private StorageViews views;
        private bool running;

        private bool IsServer => store != null;

        private CustomMessagingManager Messaging => networkManager.CustomMessagingManager;

        // The storage the local player has open (0 for none), whose it is and what it holds.
        public int OpenedId { get; private set; }

        public bool OpenedIsOwner { get; private set; }

        public StorageKind OpenedKind { get; private set; }

        public Inventory OpenedContents => openedContents;

        public StockpilePiles OpenedPiles => openedPiles;

        public IReadOnlyList<AccessEntry> OpenedAccess => openedAccess;

        // Raised when what the open storage holds or its access list changed, or it opened.
        public event Action OpenedChanged;

        // Raised when the server closed the open storage (picked up, out of reach, access taken away).
        public event Action OpenedClosed;

        // Raised on the player's machine with what the server refused.
        public event Action<StorageRefusal> Refused;

        public static string TextOf(StorageRefusal refusal)
        {
            switch (refusal)
            {
                case StorageRefusal.NotYours: return "Not yours";
                case StorageRefusal.NotEmpty: return "Empty it first";
                case StorageRefusal.PlantInTheWay: return "Something is growing there";
                case StorageRefusal.ChestFull: return $"Chest is full ({StorageRules.ChestMaxWeight:0} kg)";
                case StorageRefusal.StockpileFull: return "No room on the stockpile";
                case StorageRefusal.NotAccepted: return "It doesn't go in there";
                default: return "No room here";
            }
        }

        // To call right after the server or host has started, with the world's folder.
        internal void StartServer(string directory, PlayerRegistry playerRegistry)
        {
            running = true;
            registry = playerRegistry;
            savePath = Path.Combine(directory, SaveFileName);
            store = Load(savePath);
            shown = store;
            serverWorld = worldSync.World;
            foreach (Storage storage in store.All)
            {
                details[storage.Id] = new Shown { OwnerName = registry.NameOf(storage.OwnerId) };
                BlockColumns(storage, true);
            }

            Messaging.RegisterNamedMessageHandler(PlaceMessage, OnPlace);
            Messaging.RegisterNamedMessageHandler(OpenMessage, OnOpen);
            Messaging.RegisterNamedMessageHandler(CloseMessage, OnClose);
            Messaging.RegisterNamedMessageHandler(MoveMessage, OnMove);
            Messaging.RegisterNamedMessageHandler(AccessMessage, OnAccess);
            Messaging.RegisterNamedMessageHandler(PickupMessage, OnPickup);
            networkManager.OnClientConnectedCallback += OnClientConnected;
            networkManager.OnClientDisconnectCallback += OnClientDisconnected;
        }

        // To call right after a client's connection has been launched.
        public void StartClient()
        {
            running = true;
            Messaging.RegisterNamedMessageHandler(AllMessage, OnAll);
            Messaging.RegisterNamedMessageHandler(AddedMessage, OnAdded);
            Messaging.RegisterNamedMessageHandler(RemovedMessage, OnRemoved);
            Messaging.RegisterNamedMessageHandler(StateMessage, OnState);
            Messaging.RegisterNamedMessageHandler(ContentsMessage, OnContents);
            Messaging.RegisterNamedMessageHandler(ClosedMessage, OnClosed);
            Messaging.RegisterNamedMessageHandler(RefusedMessage, OnRefused);
        }

        public void Stop()
        {
            if (!running) return;

            if (IsServer)
            {
                networkManager.OnClientConnectedCallback -= OnClientConnected;
                networkManager.OnClientDisconnectCallback -= OnClientDisconnected;
            }

            store = null;
            registry = null;
            serverWorld = null;
            shown = new Storages();
            details.Clear();
            viewers.Clear();
            openedBy.Clear();
            OpenedId = 0;
            views?.Clear();
            running = false;
        }

        // Server side: writes the storage next to the other saves.
        public void Save()
        {
            if (store == null) return;

            try
            {
                store.SaveToFile(savePath);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                Debug.LogError($"Failed to save the storage: {e.Message} ({savePath})");
            }
        }

        // ---- What every machine can ask ----

        // Whether storage makes this cell solid (a chest, or a pile holding something): for collisions
        // and so that no block is placed into it.
        public bool IsSolid(int x, int y, int z) => running && shown.IsSolid(x, y, z);

        // Whether storage covers this cell, solid or not (an empty pile).
        public bool IsCovered(BlockPos cell) => running && shown.TryAt(cell, out _, out _);

        // Whether storage stands on this block, which then cannot be broken.
        public bool StandsOn(BlockPos block) => IsCovered(new BlockPos(block.X, block.Y + 1, block.Z));

        // What a storage is, for the aiming label: its kind, its owner's name, whether it is the local
        // player's, and how many blocks a stockpile holds.
        public bool TryDescribe(int id, out StorageKind kind, out string ownerName, out bool yours, out int blocks)
        {
            kind = StorageKind.Chest;
            ownerName = "";
            yours = false;
            blocks = 0;
            if (!shown.TryGet(id, out Storage storage) || !details.TryGetValue(id, out Shown detail)) return false;

            kind = storage.Kind;
            ownerName = detail.OwnerName;
            yours = detail.Yours;

            // The server knows them as they are now (a name chosen since).
            if (IsServer)
            {
                ownerName = registry.NameOf(storage.OwnerId);
                yours = registry.TryGetPlayerId(networkManager.LocalClientId, out string localId) && localId == storage.OwnerId;
            }

            blocks = storage.Kind == StorageKind.Stockpile ? storage.Piles.Total : 0;
            return true;
        }

        // The storage the local player points at, at most maxDistance away (distance along the ray).
        public bool TryAimed(Vector3 origin, Vector3 direction, float maxDistance, out int storageId, out float distance)
        {
            storageId = 0;
            distance = maxDistance;
            System.Numerics.Vector3 from = origin.ToNumerics();
            System.Numerics.Vector3 to = (origin + direction * maxDistance).ToNumerics();
            foreach (Storage storage in shown.All)
            {
                for (int i = 0; i < storage.CellCount; i++)
                {
                    BoxOf(storage, i, out System.Numerics.Vector3 min, out System.Numerics.Vector3 max);
                    float along = ArrowFlight.SegmentEntry(from, to, min, max);
                    if (along >= 0f && along < distance)
                    {
                        distance = along;
                        storageId = storage.Id;
                    }
                }
            }

            return storageId != 0;
        }

        // The box of one cell of a storage: a chest's, or a pile up to how high it stands (at least
        // its frame, so that an empty stockpile can be aimed at).
        private static void BoxOf(Storage storage, int index, out System.Numerics.Vector3 min, out System.Numerics.Vector3 max)
        {
            BlockPos cell = storage.Cell(index);
            if (storage.Kind == StorageKind.Chest)
            {
                min = new System.Numerics.Vector3(cell.X + ChestInset, cell.Y, cell.Z + ChestInset);
                max = new System.Numerics.Vector3(cell.X + 1 - ChestInset, cell.Y + ChestHeight, cell.Z + 1 - ChestInset);
                return;
            }

            float height = Mathf.Max(StorageViews.FrameHeight, StorageViews.PileHeight(storage.Piles.CountAt(index)));
            min = new System.Numerics.Vector3(cell.X, cell.Y, cell.Z);
            max = new System.Numerics.Vector3(cell.X + 1, cell.Y + height, cell.Z + 1);
        }

        // Where storage held by the local player would go if placed at target (the cell over the
        // aimed block) facing that way: its position (a stockpile's corner) and why not, if it cannot,
        // as far as this machine knows (the server checks animals and logs too).
        public bool CanPlaceHere(StorageKind kind, BlockPos target, int facing, Vector3 playerFeet, out BlockPos position, out StorageRefusal refusal)
        {
            World world = worldSync.World;
            position = kind == StorageKind.Chest ? target : StorageRules.StockpileCorner(target, facing);
            refusal = StorageRefusal.NoRoom;
            if (world == null) return false;

            bool fits = kind == StorageKind.Chest ? StorageRules.CanPlaceChest(world, position) : StorageRules.CanPlaceStockpile(world, position);
            if (!fits) return false;

            var probe = new Storage(0, kind, position, facing, "?");
            for (int i = 0; i < probe.CellCount; i++)
            {
                BlockPos cell = probe.Cell(i);
                if (shown.TryAt(cell, out _, out _)) return false;
                if (VoxelCollision.IntersectsBlock(playerFeet.ToNumerics(), PlayerRules.HalfWidth, PlayerRules.Height, cell)) return false;

                if (ecosystemSync.IsPlantAt(cell.X, cell.Z))
                {
                    refusal = StorageRefusal.PlantInTheWay;
                    return false;
                }
            }

            return true;
        }

        // Draws where held storage would go, this frame (to call every frame while it is held).
        public void ShowPreview(StorageKind kind, BlockPos position, bool valid)
        {
            previewing = true;
            previewKind = kind;
            previewPosition = position;
            previewValid = valid;
        }

        private bool previewing;
        private StorageKind previewKind;
        private BlockPos previewPosition;
        private bool previewValid;

        // ---- Requests of the local player ----

        public void RequestPlace(StorageKind kind, BlockPos target, int facing)
        {
            if (!running) return;

            if (IsServer)
            {
                Place(networkManager.LocalClientId, kind, target, facing);
                return;
            }

            Send(PlaceMessage, NetworkManager.ServerClientId, writer =>
            {
                writer.WriteValueSafe((byte)kind);
                writer.WriteValueSafe(target.X);
                writer.WriteValueSafe(target.Y);
                writer.WriteValueSafe(target.Z);
                writer.WriteValueSafe((byte)facing);
            });
        }

        public void RequestOpen(int storageId)
        {
            if (!running) return;

            if (IsServer) Open(networkManager.LocalClientId, storageId);
            else SendInt(OpenMessage, storageId);
        }

        // Closes the open storage (the screen closed).
        public void RequestClose()
        {
            if (!running || OpenedId == 0) return;

            int id = OpenedId;
            OpenedId = 0;
            if (IsServer) Close(networkManager.LocalClientId, id);
            else SendInt(CloseMessage, id);
        }

        // Moves items of the open storage: deposit count of an item from the bag, or take count from
        // the storage (a chest's item, or a stockpile's pile).
        public void RequestMove(bool deposit, int itemOrPile, int count)
        {
            if (!running || OpenedId == 0 || count <= 0) return;

            if (IsServer)
            {
                Move(networkManager.LocalClientId, OpenedId, deposit, itemOrPile, count);
                return;
            }

            int id = OpenedId;
            Send(MoveMessage, NetworkManager.ServerClientId, writer =>
            {
                writer.WriteValueSafe(id);
                writer.WriteValueSafe(deposit);
                writer.WriteValueSafe(itemOrPile);
                writer.WriteValueSafe(count);
            });
        }

        // The owner allows a player (by name) to open the open storage, or no longer.
        public void RequestAccess(string playerName, bool allowed)
        {
            if (!running || OpenedId == 0) return;

            if (IsServer)
            {
                SetAccess(networkManager.LocalClientId, OpenedId, playerName, allowed);
                return;
            }

            int id = OpenedId;
            Send(AccessMessage, NetworkManager.ServerClientId, writer =>
            {
                writer.WriteValueSafe(id);
                writer.WriteValueSafe(playerName);
                writer.WriteValueSafe(allowed);
            });
        }

        // The owner picks the open storage up, once empty.
        public void RequestPickup()
        {
            if (!running || OpenedId == 0) return;

            if (IsServer) Pickup(networkManager.LocalClientId, OpenedId);
            else SendInt(PickupMessage, OpenedId);
        }

        private void LateUpdate()
        {
            if (!running) return;

            if (IsServer && Time.unscaledTime >= nextReachCheck)
            {
                nextReachCheck = Time.unscaledTime + ReachCheckSeconds;
                CheckViewers();
            }

            if (Application.isBatchMode) return;

            if (views == null) views = new StorageViews(woodMaterial, ironMaterial, voxelWorld.BlockMaterial, ecosystemSync.PlantAssets);
            foreach (Storage storage in shown.All)
            {
                views.Add(storage, details.TryGetValue(storage.Id, out Shown detail) && detail.Open);
            }

            if (previewing) views.AddPreview(previewKind, previewPosition, previewValid);
            previewing = false;
            views.Render();
        }

        // ---- Server ----

        private void Place(ulong clientId, StorageKind kind, BlockPos target, int facing)
        {
            if (!registry.TryGetPlayerId(clientId, out string playerId) || !Eyes(clientId, out Vector3 eyes)) return;

            int item = StorageRules.ItemOf(kind);
            if (!inventorySync.Owns(clientId, item) || facing < 0 || facing > 3) return;

            var target3 = new Vector3(target.X + 0.5f, target.Y + 0.5f, target.Z + 0.5f);
            float reach = PlayerRules.Reach + BlockEditRules.ReachTolerance + 1f;
            if ((target3 - eyes).sqrMagnitude > reach * reach) return;

            BlockPos position = kind == StorageKind.Chest ? target : StorageRules.StockpileCorner(target, facing);
            bool fits = kind == StorageKind.Chest ? StorageRules.CanPlaceChest(serverWorld, position) : StorageRules.CanPlaceStockpile(serverWorld, position);
            var probe = new Storage(0, kind, position, facing, playerId);
            var cells = new List<BlockPos>();
            for (int i = 0; i < probe.CellCount; i++)
            {
                cells.Add(probe.Cell(i));
            }

            StorageRefusal? refusal = fits ? (StorageRefusal?)null : StorageRefusal.NoRoom;
            if (refusal == null && store.AnyCovered(cells)) refusal = StorageRefusal.NoRoom;
            foreach (BlockPos cell in cells)
            {
                if (refusal != null) break;

                if (IsInsideAnyPlayer(cell) || ecosystemSync.IsAnimalInside(cell) || logSync.HasLogIn(cell)) refusal = StorageRefusal.NoRoom;
                else if (ecosystemSync.IsPlantAt(cell.X, cell.Z)) refusal = StorageRefusal.PlantInTheWay;
            }

            if (refusal != null)
            {
                Refuse(clientId, refusal.Value);
                return;
            }

            if (!inventorySync.TryConsume(clientId, item)) return;

            Storage storage = store.Add(kind, position, facing, playerId);
            details[storage.Id] = new Shown { OwnerName = registry.NameOf(playerId) };
            BlockColumns(storage, true);
            foreach (ulong other in networkManager.ConnectedClientsIds)
            {
                if (other == networkManager.LocalClientId) continue;

                Send(AddedMessage, other, writer => WriteStorage(writer, storage, other));
            }

        }

        private void Open(ulong clientId, int storageId)
        {
            if (!store.TryGet(storageId, out Storage storage) || !registry.TryGetPlayerId(clientId, out string playerId)) return;
            if (!InReach(clientId, storage)) return;

            if (!storage.CanOpen(playerId))
            {
                Refuse(clientId, StorageRefusal.NotYours);
                return;
            }

            if (openedBy.TryGetValue(clientId, out int previous) && previous != storageId) Close(clientId, previous);

            openedBy[clientId] = storageId;
            if (!viewers.TryGetValue(storageId, out HashSet<ulong> watching))
            {
                watching = new HashSet<ulong>();
                viewers.Add(storageId, watching);
            }

            watching.Add(clientId);
            SendContents(clientId, storage);
            SetOpen(storage, true);
        }

        private void Close(ulong clientId, int storageId)
        {
            if (!openedBy.TryGetValue(clientId, out int opened) || opened != storageId) return;

            openedBy.Remove(clientId);
            if (!viewers.TryGetValue(storageId, out HashSet<ulong> watching)) return;

            watching.Remove(clientId);
            if (watching.Count > 0) return;

            viewers.Remove(storageId);
            if (store.TryGet(storageId, out Storage storage)) SetOpen(storage, false);
        }

        // The server closes a player's storage (it went, they left its reach or lost access).
        private void ForceClose(ulong clientId, int storageId)
        {
            Close(clientId, storageId);
            if (clientId == networkManager.LocalClientId)
            {
                if (OpenedId == storageId) ClosedHere();
                return;
            }

            SendInt(ClosedMessage, storageId, clientId);
        }

        private void Move(ulong clientId, int storageId, bool deposit, int itemOrPile, int count)
        {
            if (count <= 0 || !store.TryGet(storageId, out Storage storage) || !registry.TryGetPlayerId(clientId, out string playerId)) return;
            if (!storage.CanOpen(playerId) || !InReach(clientId, storage) || !registry.TryGetState(clientId, out PlayerState state)) return;

            if (deposit)
            {
                int item = itemOrPile;
                bool accepted = storage.Kind == StorageKind.Chest ? StorageRules.AcceptsInChest(item) : StorageRules.AcceptsInStockpile(item);
                if (!accepted)
                {
                    Refuse(clientId, StorageRefusal.NotAccepted);
                    return;
                }

                int room = storage.Kind == StorageKind.Chest
                    ? StorageRules.ChestRoom(storage.Contents, item, ecosystemSync.ItemWeights)
                    : storage.Piles.Room(item);
                int moved = Math.Min(count, Math.Min(state.Inventory.Count(item), room));
                if (room <= 0)
                {
                    Refuse(clientId, storage.Kind == StorageKind.Chest ? StorageRefusal.ChestFull : StorageRefusal.StockpileFull);
                    return;
                }

                if (moved <= 0 || !inventorySync.TryConsume(clientId, item, moved)) return;

                if (storage.Kind == StorageKind.Chest) storage.Contents.Add(item, moved);
                else storage.Piles.Add(item, moved);
            }
            else
            {
                int item = storage.Kind == StorageKind.Chest ? itemOrPile : storage.Piles.ItemAt(itemOrPile);
                int held = storage.Kind == StorageKind.Chest ? storage.Contents.Count(item) : storage.Piles.CountAt(itemOrPile);
                if (item == Items.None || held <= 0) return;

                int room = inventorySync.RoomFor(clientId, item);
                if (room <= 0)
                {
                    inventorySync.TellFull(clientId, InventorySync.ReasonFor(item));
                    return;
                }

                int moved = Math.Min(count, Math.Min(held, room));
                if (storage.Kind == StorageKind.Chest) storage.Contents.TryRemove(item, moved);
                else storage.Piles.Take(itemOrPile, moved);
                inventorySync.Give(clientId, item, moved);
            }

            Changed(storage);
        }

        private void SetAccess(ulong clientId, int storageId, string playerName, bool allowed)
        {
            if (!store.TryGet(storageId, out Storage storage) || !registry.TryGetPlayerId(clientId, out string playerId)) return;
            if (storage.OwnerId != playerId)
            {
                Refuse(clientId, StorageRefusal.NotYours);
                return;
            }

            if (string.IsNullOrEmpty(playerName) || !registry.TryFindByName(playerName, out string other) || other == playerId) return;

            if (allowed) storage.Allowed.Add(other);
            else storage.Allowed.Remove(other);

            // Someone no longer allowed loses it at once.
            if (!allowed && viewers.TryGetValue(storageId, out HashSet<ulong> watching))
            {
                leaving.Clear();
                foreach (ulong viewer in watching)
                {
                    if (registry.TryGetPlayerId(viewer, out string viewerId) && viewerId == other) leaving.Add(viewer);
                }

                foreach (ulong viewer in leaving)
                {
                    ForceClose(viewer, storageId);
                }
            }

            Changed(storage);
        }

        private void Pickup(ulong clientId, int storageId)
        {
            if (!store.TryGet(storageId, out Storage storage) || !registry.TryGetPlayerId(clientId, out string playerId)) return;
            if (!InReach(clientId, storage)) return;

            if (storage.OwnerId != playerId)
            {
                Refuse(clientId, StorageRefusal.NotYours);
                return;
            }

            if (!storage.IsEmpty)
            {
                Refuse(clientId, StorageRefusal.NotEmpty);
                return;
            }

            int item = StorageRules.ItemOf(storage.Kind);
            if (inventorySync.RoomFor(clientId, item) <= 0)
            {
                inventorySync.TellFull(clientId, InventorySync.ReasonFor(item));
                return;
            }

            if (viewers.TryGetValue(storageId, out HashSet<ulong> watching))
            {
                leaving.Clear();
                leaving.AddRange(watching);
                foreach (ulong viewer in leaving)
                {
                    ForceClose(viewer, storageId);
                }
            }

            store.Remove(storageId);
            details.Remove(storageId);
            BlockColumns(storage, false);
            inventorySync.Give(clientId, item, 1);
            foreach (ulong other in networkManager.ConnectedClientsIds)
            {
                if (other != networkManager.LocalClientId) SendInt(RemovedMessage, storageId, other);
            }
        }

        // After what a storage holds or who may open it changed: its viewers get it again, and every
        // machine a stockpile's piles (they are drawn, and solid when not empty).
        private void Changed(Storage storage)
        {
            if (viewers.TryGetValue(storage.Id, out HashSet<ulong> watching))
            {
                foreach (ulong viewer in watching)
                {
                    SendContents(viewer, storage);
                }
            }

            if (storage.Kind == StorageKind.Stockpile) BroadcastState(storage);
        }

        private void SetOpen(Storage storage, bool open)
        {
            if (!details.TryGetValue(storage.Id, out Shown detail) || detail.Open == open) return;

            detail.Open = open;
            BroadcastState(storage);
        }

        private void BroadcastState(Storage storage)
        {
            bool open = details.TryGetValue(storage.Id, out Shown detail) && detail.Open;
            foreach (ulong clientId in networkManager.ConnectedClientsIds)
            {
                if (clientId == networkManager.LocalClientId) continue;

                Send(StateMessage, clientId, writer =>
                {
                    writer.WriteValueSafe(storage.Id);
                    writer.WriteValueSafe(open);
                    if (storage.Kind == StorageKind.Stockpile) WritePiles(writer, storage.Piles);
                });
            }
        }

        // What a viewer is sent: whether they own it, what it holds, and for the owner its access list
        // (the named players connected, then those allowed who are not).
        private void SendContents(ulong clientId, Storage storage)
        {
            bool owner = registry.TryGetPlayerId(clientId, out string playerId) && playerId == storage.OwnerId;
            var access = new List<AccessEntry>();
            if (owner)
            {
                foreach (KeyValuePair<ulong, string> player in registry.Connected)
                {
                    string name = registry.NameOf(player.Value);
                    if (player.Value == storage.OwnerId || name.Length == 0) continue;

                    access.Add(new AccessEntry(name, true, storage.Allowed.Contains(player.Value)));
                }

                foreach (string allowedId in storage.Allowed)
                {
                    if (registry.IsConnected(allowedId)) continue;

                    string name = registry.NameOf(allowedId);
                    if (name.Length > 0) access.Add(new AccessEntry(name, false, true));
                }

                access.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            }

            if (clientId == networkManager.LocalClientId)
            {
                ShowOpened(storage.Id, storage.Kind, owner, storage.Contents, storage.Piles, access);
                return;
            }

            Send(ContentsMessage, clientId, writer =>
            {
                writer.WriteValueSafe(storage.Id);
                writer.WriteValueSafe((byte)storage.Kind);
                writer.WriteValueSafe(owner);
                if (storage.Kind == StorageKind.Chest)
                {
                    writer.WriteValueSafe(storage.Contents.DistinctCount);
                    foreach (KeyValuePair<int, int> entry in storage.Contents.Entries)
                    {
                        writer.WriteValueSafe(entry.Key);
                        writer.WriteValueSafe(entry.Value);
                    }
                }
                else
                {
                    WritePiles(writer, storage.Piles);
                }

                writer.WriteValueSafe(access.Count);
                foreach (AccessEntry entry in access)
                {
                    writer.WriteValueSafe(entry.Name);
                    writer.WriteValueSafe(entry.Online);
                    writer.WriteValueSafe(entry.Allowed);
                }
            });
        }

        // Closes storage that is no longer in reach of who opened it.
        private void CheckViewers()
        {
            if (openedBy.Count == 0) return;

            leaving.Clear();
            foreach (KeyValuePair<ulong, int> opened in openedBy)
            {
                if (!store.TryGet(opened.Value, out Storage storage) || !InReach(opened.Key, storage, 1f)) leaving.Add(opened.Key);
            }

            foreach (ulong clientId in leaving)
            {
                ForceClose(clientId, openedBy[clientId]);
            }
        }

        private void Refuse(ulong clientId, StorageRefusal refusal)
        {
            if (clientId == networkManager.LocalClientId)
            {
                Refused?.Invoke(refusal);
                return;
            }

            Send(RefusedMessage, clientId, writer => writer.WriteValueSafe((byte)refusal));
        }

        private void BlockColumns(Storage storage, bool blocked)
        {
            for (int i = 0; i < storage.CellCount; i++)
            {
                BlockPos cell = storage.Cell(i);
                ecosystemSync.BlockColumn(cell.X, cell.Z, blocked);
            }
        }

        private bool Eyes(ulong clientId, out Vector3 eyes)
        {
            eyes = default;
            if (!networkManager.ConnectedClients.TryGetValue(clientId, out NetworkClient client) || client.PlayerObject == null) return false;

            eyes = client.PlayerObject.transform.position + Vector3.up * PlayerRules.EyeHeight;
            return true;
        }

        // Whether a player's eyes are within reach of any cell of a storage.
        private bool InReach(ulong clientId, Storage storage, float slack = 0f)
        {
            if (!Eyes(clientId, out Vector3 eyes)) return false;

            float reach = PlayerRules.Reach + BlockEditRules.ReachTolerance + slack;
            for (int i = 0; i < storage.CellCount; i++)
            {
                BlockPos cell = storage.Cell(i);
                var nearest = new Vector3(
                    Mathf.Clamp(eyes.x, cell.X, cell.X + 1),
                    Mathf.Clamp(eyes.y, cell.Y, cell.Y + 1),
                    Mathf.Clamp(eyes.z, cell.Z, cell.Z + 1));
                if ((nearest - eyes).sqrMagnitude <= reach * reach) return true;
            }

            return false;
        }

        private bool IsInsideAnyPlayer(BlockPos cell)
        {
            foreach (NetworkClient client in networkManager.ConnectedClientsList)
            {
                if (client.PlayerObject == null) continue;

                System.Numerics.Vector3 feet = client.PlayerObject.transform.position.ToNumerics();
                if (VoxelCollision.IntersectsBlock(feet, PlayerRules.HalfWidth, PlayerRules.Height, cell)) return true;
            }

            return false;
        }

        // A joining player is sent every storage in the world.
        private void OnClientConnected(ulong clientId)
        {
            if (clientId == networkManager.LocalClientId) return;

            Send(AllMessage, clientId, writer =>
            {
                writer.WriteValueSafe(store.Count);
                foreach (Storage storage in store.All)
                {
                    WriteStorage(writer, storage, clientId);
                }
            });
        }

        private void OnClientDisconnected(ulong clientId)
        {
            if (openedBy.TryGetValue(clientId, out int storageId)) Close(clientId, storageId);
        }

        // One storage as every machine knows it; "yours" is said to its owner only.
        private void WriteStorage(FastBufferWriter writer, Storage storage, ulong clientId)
        {
            bool yours = registry.TryGetPlayerId(clientId, out string playerId) && playerId == storage.OwnerId;
            details.TryGetValue(storage.Id, out Shown detail);
            writer.WriteValueSafe(storage.Id);
            writer.WriteValueSafe((byte)storage.Kind);
            writer.WriteValueSafe(storage.Position.X);
            writer.WriteValueSafe(storage.Position.Y);
            writer.WriteValueSafe(storage.Position.Z);
            writer.WriteValueSafe((byte)storage.Facing);
            writer.WriteValueSafe(registry.NameOf(storage.OwnerId));
            writer.WriteValueSafe(yours);
            writer.WriteValueSafe(detail != null && detail.Open);
            if (storage.Kind == StorageKind.Stockpile) WritePiles(writer, storage.Piles);
        }

        private static void WritePiles(FastBufferWriter writer, StockpilePiles piles)
        {
            for (int pile = 0; pile < StorageRules.PileCount; pile++)
            {
                writer.WriteValueSafe(piles.ItemAt(pile));
                writer.WriteValueSafe((byte)piles.CountAt(pile));
            }
        }

        // Fills piles from the wire; false, changing nothing, if anything is out of range.
        private static bool ReadPiles(FastBufferReader reader, StockpilePiles into)
        {
            var read = new StockpilePiles();
            bool valid = true;
            for (int pile = 0; pile < StorageRules.PileCount; pile++)
            {
                reader.ReadValueSafe(out int item);
                reader.ReadValueSafe(out byte count);
                valid &= read.Set(pile, item, count);
            }

            if (valid) into.CopyFrom(read);
            return valid;
        }

        // ---- Client handlers ----

        private void OnPlace(ulong senderId, FastBufferReader reader)
        {
            reader.ReadValueSafe(out byte kind);
            reader.ReadValueSafe(out int x);
            reader.ReadValueSafe(out int y);
            reader.ReadValueSafe(out int z);
            reader.ReadValueSafe(out byte facing);
            if (StorageRules.IsKind(kind)) Place(senderId, (StorageKind)kind, new BlockPos(x, y, z), facing);
        }

        private void OnOpen(ulong senderId, FastBufferReader reader)
        {
            reader.ReadValueSafe(out int id);
            Open(senderId, id);
        }

        private void OnClose(ulong senderId, FastBufferReader reader)
        {
            reader.ReadValueSafe(out int id);
            Close(senderId, id);
        }

        private void OnMove(ulong senderId, FastBufferReader reader)
        {
            reader.ReadValueSafe(out int id);
            reader.ReadValueSafe(out bool deposit);
            reader.ReadValueSafe(out int itemOrPile);
            reader.ReadValueSafe(out int count);
            Move(senderId, id, deposit, itemOrPile, count);
        }

        private void OnAccess(ulong senderId, FastBufferReader reader)
        {
            reader.ReadValueSafe(out int id);
            reader.ReadValueSafe(out string name);
            reader.ReadValueSafe(out bool allowed);
            SetAccess(senderId, id, name, allowed);
        }

        private void OnPickup(ulong senderId, FastBufferReader reader)
        {
            reader.ReadValueSafe(out int id);
            Pickup(senderId, id);
        }

        private void OnAll(ulong senderId, FastBufferReader reader)
        {
            reader.ReadValueSafe(out int count);
            shown = new Storages();
            details.Clear();
            for (int i = 0; i < count; i++)
            {
                if (!TryReadStorage(reader)) return;
            }
        }

        private void OnAdded(ulong senderId, FastBufferReader reader) => TryReadStorage(reader);

        private void OnRemoved(ulong senderId, FastBufferReader reader)
        {
            reader.ReadValueSafe(out int id);
            shown.Remove(id);
            details.Remove(id);
        }

        private void OnState(ulong senderId, FastBufferReader reader)
        {
            reader.ReadValueSafe(out int id);
            reader.ReadValueSafe(out bool open);
            if (!shown.TryGet(id, out Storage storage)) return;

            if (details.TryGetValue(id, out Shown detail)) detail.Open = open;
            if (storage.Kind == StorageKind.Stockpile) ReadPiles(reader, storage.Piles);
        }

        private void OnContents(ulong senderId, FastBufferReader reader)
        {
            reader.ReadValueSafe(out int id);
            reader.ReadValueSafe(out byte kind);
            reader.ReadValueSafe(out bool owner);
            if (!StorageRules.IsKind(kind)) return;

            var contents = new Inventory();
            var piles = new StockpilePiles();
            if (kind == (byte)StorageKind.Chest)
            {
                reader.ReadValueSafe(out int entries);
                for (int i = 0; i < entries; i++)
                {
                    reader.ReadValueSafe(out int item);
                    reader.ReadValueSafe(out int count);
                    if (count > 0 && StorageRules.AcceptsInChest(item)) contents.Add(item, count);
                }
            }
            else if (!ReadPiles(reader, piles))
            {
                return;
            }

            reader.ReadValueSafe(out int accessCount);
            var access = new List<AccessEntry>();
            for (int i = 0; i < accessCount; i++)
            {
                reader.ReadValueSafe(out string name);
                reader.ReadValueSafe(out bool online);
                reader.ReadValueSafe(out bool allowed);
                access.Add(new AccessEntry(name, online, allowed));
            }

            ShowOpened(id, (StorageKind)kind, owner, contents, piles, access);
        }

        private void OnClosed(ulong senderId, FastBufferReader reader)
        {
            reader.ReadValueSafe(out int id);
            if (OpenedId == id) ClosedHere();
        }

        private void OnRefused(ulong senderId, FastBufferReader reader)
        {
            reader.ReadValueSafe(out byte refusal);
            Refused?.Invoke((StorageRefusal)refusal);
        }

        private bool TryReadStorage(FastBufferReader reader)
        {
            reader.ReadValueSafe(out int id);
            reader.ReadValueSafe(out byte kind);
            reader.ReadValueSafe(out int x);
            reader.ReadValueSafe(out int y);
            reader.ReadValueSafe(out int z);
            reader.ReadValueSafe(out byte facing);
            reader.ReadValueSafe(out string ownerName);
            reader.ReadValueSafe(out bool yours);
            reader.ReadValueSafe(out bool open);
            if (!StorageRules.IsKind(kind) || facing > 3) return false;

            var storage = new Storage(id, (StorageKind)kind, new BlockPos(x, y, z), facing, "?");
            if (storage.Kind == StorageKind.Stockpile && !ReadPiles(reader, storage.Piles)) return false;

            shown.Put(storage);
            details[id] = new Shown { OwnerName = ownerName, Yours = yours, Open = open };
            return true;
        }

        // ---- The local player's open storage ----

        private void ShowOpened(int id, StorageKind kind, bool owner, Inventory contents, StockpilePiles piles, List<AccessEntry> access)
        {
            OpenedId = id;
            OpenedKind = kind;
            OpenedIsOwner = owner;
            if (contents != null) openedContents.CopyFrom(contents);
            else openedContents.Clear();
            if (piles != null) openedPiles.CopyFrom(piles);
            openedAccess.Clear();
            openedAccess.AddRange(access);
            OpenedChanged?.Invoke();
        }

        private void ClosedHere()
        {
            OpenedId = 0;
            OpenedClosed?.Invoke();
        }

        // ---- Sending ----

        private void Send(string message, ulong clientId, Action<FastBufferWriter> write)
        {
            using (var writer = new FastBufferWriter(256, Allocator.Temp, 1 << 20))
            {
                write(writer);
                Messaging.SendNamedMessage(message, clientId, writer, NetworkDelivery.ReliableFragmentedSequenced);
            }
        }

        private void SendInt(string message, int value, ulong clientId = NetworkManager.ServerClientId)
        {
            Send(message, clientId, writer => writer.WriteValueSafe(value));
        }

        private static Storages Load(string path)
        {
            if (!File.Exists(path)) return new Storages();

            try
            {
                return Storages.LoadFromFile(path);
            }
            catch (Exception e) when (e is IOException || e is InvalidDataException || e is UnauthorizedAccessException)
            {
                Debug.LogWarning($"The storage could not be loaded ({e.Message}): the world starts without any.");
                return new Storages();
            }
        }
    }
}

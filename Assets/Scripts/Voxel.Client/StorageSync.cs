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
        NotAccepted = 7,
        TooHungry = 8,

        // Only found by the client, which then says whose land it is (ClaimSync); the server tells it.
        Claimed = 9,

        // A station's (StorageSync.Stations.cs).
        QueueFull = 10,
        MissingIngredients = 11,
        NoRoomToGiveBack = 12,
        FuelFull = 13
    }

    // A chest or stockpile of the open pool, or a chest linked to the open station, as the server last
    // sent it: a chest's items, or a stockpile's piles.
    public sealed class StorageMember
    {
        public int Id;
        public StorageKind Kind;
        public readonly Inventory Contents = new Inventory();
        public StockpilePiles Piles;
    }

    // Chests and stockpiles placed in the world (StorageRules, Storages). The server keeps them and
    // saves them with the world (storage.dat): it places one where a player asks if they own it and the
    // place is free (no block, player, animal, plant or log in it, flat ground for a stockpile), lets
    // its owner and the players they allow open it (anyone, while it stands off claimed land), moves
    // items between it and their bags (a chest by weight, a stockpile by piles), lets the owner allow
    // others and pick it up once empty (anyone, while it is public). Every
    // machine knows where each one stands, whose it is, whether it is open and a stockpile's piles (to
    // draw them and collide with them); only the players who opened one get what a chest holds and its
    // access list. Nothing sprouts where storage stands (Ecosystem.BlockColumn). Stations (campfires,
    // butcher's tables) are placed, owned, opened and picked up the same way; what they do is in
    // StorageSync.Stations.cs.
    public partial class StorageSync : MonoBehaviour
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
        private const string PickupMessage = "Store.Pickup";
        private const string RefusedMessage = "Store.Refused";
        private const string SaveFileName = "storage.dat";

        // A chest's box in its cell, for aiming.
        public const float ChestInset = 0.1f;
        public const float ChestHeight = 0.8f;

        // A campfire's box (stones and logs), for aiming.
        public const float CampfireHeight = 0.5f;

        // How often the server checks that open storage is still in reach of who opened it.
        private const float ReachCheckSeconds = 0.5f;

        // Most storage a pool can hold as sent (a whole zone of 16 plots holds far fewer).
        private const int MaxPoolMembers = 4096;

        [SerializeField] private NetworkManager networkManager;
        [SerializeField] private WorldSync worldSync;
        [SerializeField] private InventorySync inventorySync;
        [SerializeField] private EcosystemSync ecosystemSync;
        [SerializeField] private LogSync logSync;
        [SerializeField] private ClaimSync claimSync;
        [SerializeField] private Material woodMaterial;
        [SerializeField] private Material ironMaterial;

        // A campfire's flame (TorchFlame.mat: unlit, so it looks lit by itself at night).
        [SerializeField] private Material flameMaterial;
        [SerializeField] private VoxelWorld voxelWorld;

        // What every machine shows of a storage besides where it stands.
        private sealed class Shown
        {
            public string OwnerName = "";
            public bool Yours;
            public bool Open;
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

        // The storage the local player has open, as the server last sent it: what its pool holds (the
        // chests or stockpiles of its claimed zone together, StoragePool).
        private readonly Inventory openedContents = new Inventory();
        private readonly List<StockpilePiles> openedStockpiles = new List<StockpilePiles>();
        private readonly List<StorageMember> openedMembers = new List<StorageMember>();

        private StorageViews views;
        private bool running;

        private bool IsServer => store != null;

        private CustomMessagingManager Messaging => networkManager.CustomMessagingManager;

        // The storage the local player has open (0 for none), whose it is and what it holds.
        public int OpenedId { get; private set; }

        public bool OpenedIsOwner { get; private set; }

        public StorageKind OpenedKind { get; private set; }

        // How many chests or stockpiles share what the open one shows (its claimed zone's, else 1).
        public int OpenedMembers { get; private set; } = 1;

        // What the open chest's pool holds.
        public Inventory OpenedContents => openedContents;

        // Each chest or stockpile of the open pool, in id order, with what it holds.
        public IReadOnlyList<StorageMember> OpenedMemberList => openedMembers;

        // The open stockpile's pool: each member's piles (large and small), and all of them one after
        // another (StoragePool's numbering).
        public IReadOnlyList<StockpilePiles> OpenedStockpiles => openedStockpiles;

        public int OpenedPileCount
        {
            get
            {
                int count = 0;
                foreach (StockpilePiles piles in openedStockpiles)
                {
                    count += piles.Count;
                }

                return count;
            }
        }

        public int OpenedPileCapacity
        {
            get
            {
                int capacity = 0;
                foreach (StockpilePiles piles in openedStockpiles)
                {
                    capacity += piles.Capacity;
                }

                return capacity;
            }
        }

        public int OpenedPileItem(int pile) => TryFindOpenedPile(pile, out StockpilePiles piles, out int index) ? piles.ItemAt(index) : Items.None;

        public int OpenedPileCountAt(int pile) => TryFindOpenedPile(pile, out StockpilePiles piles, out int index) ? piles.CountAt(index) : 0;

        // How many a pile of the open pool holds at most.
        public int OpenedPileSizeAt(int pile) => TryFindOpenedPile(pile, out StockpilePiles piles, out _) ? piles.PileSize : 0;

        private bool TryFindOpenedPile(int pile, out StockpilePiles piles, out int index)
        {
            piles = null;
            index = -1;
            if (pile < 0) return false;

            foreach (StockpilePiles member in openedStockpiles)
            {
                if (pile < member.Count)
                {
                    piles = member;
                    index = pile;
                    return true;
                }

                pile -= member.Count;
            }

            return false;
        }

        public int OpenedPileTotal
        {
            get
            {
                int total = 0;
                foreach (StockpilePiles piles in openedStockpiles)
                {
                    total += piles.Total;
                }

                return total;
            }
        }

        // Raised when what the open storage's pool holds changed, or it opened.
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
                case StorageRefusal.ChestFull: return Loc.T("Chest is full ({0} kg)", StorageRules.ChestMaxWeight.ToString("0"));
                case StorageRefusal.StockpileFull: return "No room on the stockpile";
                case StorageRefusal.NotAccepted: return "It doesn't go in there";
                case StorageRefusal.TooHungry: return InventorySync.TooHungryMessage;
                case StorageRefusal.Claimed: return ClaimSync.TextOf(ClaimRefusal.None, "");
                case StorageRefusal.QueueFull: return Loc.T("The queue is full ({0})", Station.MaxJobs);
                case StorageRefusal.MissingIngredients: return "Missing ingredients";
                case StorageRefusal.NoRoomToGiveBack: return "No room to give back the ingredients";
                case StorageRefusal.FuelFull: return Loc.T("The fire holds {0} wood at most", Station.MaxFuel);
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
            Messaging.RegisterNamedMessageHandler(PickupMessage, OnPickup);
            StartStationServer();
            networkManager.OnClientConnectedCallback += OnClientConnected;
            networkManager.OnClientDisconnectCallback += OnClientDisconnected;
            claimSync.ClaimsChanged += OnClaimsChanged;
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
            StartStationClient();
        }

        public void Stop()
        {
            if (!running) return;

            if (IsServer)
            {
                networkManager.OnClientConnectedCallback -= OnClientConnected;
                networkManager.OnClientDisconnectCallback -= OnClientDisconnected;
                claimSync.ClaimsChanged -= OnClaimsChanged;
            }

            store = null;
            registry = null;
            serverWorld = null;
            shown = new Storages();
            details.Clear();
            viewers.Clear();
            openedBy.Clear();
            OpenedId = 0;
            StopStations();
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

            // On claimed land it is the land owner's, whoever placed it.
            if (TryLandOf(storage, out int x, out int z) && claimSync.TryDescribe(x, z, out string landOwner, out ClaimRelation relation))
            {
                ownerName = landOwner;
                yours = relation == ClaimRelation.Yours;
            }
            else if (IsServer)
            {
                // The server knows them as they are now (a name chosen since).
                ownerName = registry.NameOf(storage.OwnerId);
                yours = registry.TryGetPlayerId(networkManager.LocalClientId, out string localId) && localId == storage.OwnerId;
            }

            blocks = StorageRules.IsStockpile(storage.Kind) ? storage.Piles.Total : 0;
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
            if (StorageRules.IsSingleCell(storage.Kind))
            {
                float top = storage.Kind == StorageKind.Campfire ? CampfireHeight : ChestHeight;
                min = new System.Numerics.Vector3(cell.X + ChestInset, cell.Y, cell.Z + ChestInset);
                max = new System.Numerics.Vector3(cell.X + 1 - ChestInset, cell.Y + top, cell.Z + 1 - ChestInset);
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
            position = StorageRules.IsSingleCell(kind) ? target : StorageRules.StockpileCorner(target, facing, StorageRules.SideOf(kind));
            refusal = StorageRefusal.NoRoom;
            if (world == null) return false;

            bool fits = StorageRules.IsSingleCell(kind) ? StorageRules.CanPlaceChest(world, position) : StorageRules.CanPlaceStockpile(world, position, StorageRules.SideOf(kind));
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

                if (!claimSync.LocalAllows(cell.X, cell.Z))
                {
                    claimSync.TryDescribe(cell.X, cell.Z, out string ownerName, out _);
                    ClaimedBy = ownerName;
                    refusal = StorageRefusal.Claimed;
                    return false;
                }
            }

            return true;
        }

        // Whose land CanPlaceHere last found in the way (StorageRefusal.Claimed).
        public string ClaimedBy { get; private set; } = "";

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
        // the storage (a chest's item, a stockpile's pile, or with byItem a stockpile's item from every
        // pile of it); into or out of one member of its pool (a storage id), or the whole pool (0).
        public void RequestMove(bool deposit, int itemOrPile, int count, int member = 0, bool byItem = false)
        {
            if (!running || OpenedId == 0 || count <= 0) return;

            if (IsServer)
            {
                Move(networkManager.LocalClientId, OpenedId, deposit, itemOrPile, count, member, byItem);
                return;
            }

            int id = OpenedId;
            Send(MoveMessage, NetworkManager.ServerClientId, writer =>
            {
                writer.WriteValueSafe(id);
                writer.WriteValueSafe(deposit);
                writer.WriteValueSafe(itemOrPile);
                writer.WriteValueSafe(count);
                writer.WriteValueSafe(member);
                writer.WriteValueSafe(byItem);
            });
        }

        // The owner, or anyone while it is public, picks the open storage up, once empty (or once what
        // it holds fits in the rest of its pool).
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

            if (IsServer) TickStations(Time.unscaledDeltaTime);

            if (Application.isBatchMode) return;

            if (views == null) views = new StorageViews(woodMaterial, ironMaterial, voxelWorld.BlockMaterial, flameMaterial, ecosystemSync.PlantAssets);
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
            if (inventorySync.IsStarving(clientId))
            {
                Refuse(clientId, StorageRefusal.TooHungry);
                return;
            }

            var target3 = new Vector3(target.X + 0.5f, target.Y + 0.5f, target.Z + 0.5f);
            float reach = PlayerRules.Reach + BlockEditRules.ReachTolerance + 1f;
            if ((target3 - eyes).sqrMagnitude > reach * reach) return;

            BlockPos position = StorageRules.IsSingleCell(kind) ? target : StorageRules.StockpileCorner(target, facing, StorageRules.SideOf(kind));
            bool fits = StorageRules.IsSingleCell(kind) ? StorageRules.CanPlaceChest(serverWorld, position) : StorageRules.CanPlaceStockpile(serverWorld, position, StorageRules.SideOf(kind));
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

            // Not on someone else's land, not even one cell of a stockpile.
            foreach (BlockPos cell in cells)
            {
                if (!claimSync.Check(clientId, cell.X, cell.Z)) return;
            }

            if (!inventorySync.TryConsumePlaced(clientId, item)) return;

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

            if (!CanUse(playerId, storage))
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

            // A chest's lid rises while it is open; a station's flag is whether it works.
            if (!StorageRules.IsStation(storage.Kind)) SetOpen(storage, true);
        }

        private void Close(ulong clientId, int storageId)
        {
            if (!openedBy.TryGetValue(clientId, out int opened) || opened != storageId) return;

            openedBy.Remove(clientId);
            if (!viewers.TryGetValue(storageId, out HashSet<ulong> watching)) return;

            watching.Remove(clientId);
            if (watching.Count > 0) return;

            viewers.Remove(storageId);
            if (store.TryGet(storageId, out Storage storage) && !StorageRules.IsStation(storage.Kind)) SetOpen(storage, false);
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

        // Items go into, or come out of, the storage's whole pool (StoragePool): the chests or the
        // stockpiles of its claimed zone; a pile is numbered across the pool.
        private void Move(ulong clientId, int storageId, bool deposit, int itemOrPile, int count, int member, bool byItem)
        {
            if (count <= 0 || !store.TryGet(storageId, out Storage storage) || !registry.TryGetPlayerId(clientId, out string playerId)) return;
            if (!CanUse(playerId, storage) || !InReach(clientId, storage)) return;
            if (StorageRules.IsStation(storage.Kind)) return;

            List<Storage> pool = PoolOf(storage);
            if (MoveWithin(clientId, pool, storage.Kind == StorageKind.Chest, deposit, itemOrPile, count, member, byItem)) Changed(storage, pool);
        }

        // Moves items between a player's bag and a pool of chests or of stockpiles, or one member of it
        // (member: its id, 0 for the whole pool). Returns whether anything moved.
        private bool MoveWithin(ulong clientId, List<Storage> pool, bool chest, bool deposit, int itemOrPile, int count, int member, bool byItem)
        {
            if (!registry.TryGetState(clientId, out PlayerState state)) return false;

            List<Storage> target = pool;
            if (member != 0)
            {
                Storage one = pool.Find(s => s.Id == member);
                if (one == null) return false;

                target = new List<Storage> { one };
            }

            if (deposit)
            {
                int item = itemOrPile;
                bool accepted = chest ? StorageRules.AcceptsInChest(item) : StorageRules.AcceptsInStockpile(item);
                if (!accepted)
                {
                    Refuse(clientId, StorageRefusal.NotAccepted);
                    return false;
                }

                int room = chest ? StoragePool.ChestRoom(target, item, ecosystemSync.ItemWeights) : StoragePool.StockpileRoom(target, item);
                int moved = Math.Min(count, Math.Min(state.Inventory.Count(item), room));
                if (room <= 0)
                {
                    Refuse(clientId, chest ? StorageRefusal.ChestFull : StorageRefusal.StockpileFull);
                    return false;
                }

                if (moved <= 0 || !inventorySync.TryConsume(clientId, item, moved)) return false;

                if (chest) StoragePool.AddToChests(target, item, moved, ecosystemSync.ItemWeights);
                else StoragePool.AddToStockpiles(target, item, moved);
                return true;
            }

            // A pile is numbered across the whole pool.
            Storage stockpile = null;
            int pile = -1;
            bool byPile = !chest && !byItem;
            if (byPile && !StoragePool.TryFindPile(pool, itemOrPile, out stockpile, out pile)) return false;

            int taken = byPile ? stockpile.Piles.ItemAt(pile) : itemOrPile;
            int held = byPile ? stockpile.Piles.CountAt(pile) : chest ? StoragePool.Count(target, taken) : StoragePool.StockpileCount(target, taken);
            if (taken == Items.None || held <= 0) return false;

            int bagRoom = inventorySync.RoomFor(clientId, taken);
            if (bagRoom <= 0)
            {
                inventorySync.TellFull(clientId, InventorySync.ReasonFor(taken));
                return false;
            }

            int given = Math.Min(count, Math.Min(held, bagRoom));
            if (byPile) stockpile.Piles.Take(pile, given);
            else if (chest) StoragePool.TakeFromChests(target, taken, given);
            else StoragePool.TakeFromStockpiles(target, taken, given);
            inventorySync.Give(clientId, taken, given);
            return true;
        }

        private void Pickup(ulong clientId, int storageId)
        {
            if (!store.TryGet(storageId, out Storage storage) || !registry.TryGetPlayerId(clientId, out string playerId)) return;
            if (!InReach(clientId, storage)) return;

            // Its owner (the land's, on claimed land) picks it up, or anyone while it is public.
            if (OwnerOf(storage) != playerId && !IsPublic(storage))
            {
                Refuse(clientId, StorageRefusal.NotYours);
                return;
            }

            int item = StorageRules.ItemOf(storage.Kind);
            if (inventorySync.RoomFor(clientId, item) <= 0)
            {
                inventorySync.TellFull(clientId, InventorySync.ReasonFor(item));
                return;
            }

            // What it holds goes to the rest of its pool, if it all fits there; a station's wood goes
            // back to the bag, once nothing is queued.
            List<Storage> pool = PoolOf(storage);
            pool.Remove(storage);
            bool emptied = StorageRules.IsStation(storage.Kind)
                ? TryEmptyStation(clientId, storage)
                : StoragePool.TryEmptyInto(storage, pool, ecosystemSync.ItemWeights);
            if (!emptied)
            {
                Refuse(clientId, StorageRefusal.NotEmpty);
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

            // The rest of the pool took what it held, and is one storage smaller.
            if (pool.Count > 0) Changed(pool[0], pool);
        }

        // After what a pool holds changed: whoever has one of its members open gets it again, and
        // every machine its stockpiles' piles (they are drawn, and solid when not empty).
        private void Changed(Storage storage, List<Storage> pool)
        {
            foreach (Storage member in pool)
            {
                if (viewers.TryGetValue(member.Id, out HashSet<ulong> watching))
                {
                    foreach (ulong viewer in watching)
                    {
                        SendContents(viewer, member);
                    }
                }

                if (StorageRules.IsStockpile(member.Kind)) BroadcastState(member);
            }

            // Stations open in a window show what their linked storage holds.
            if (!StorageRules.IsStation(storage.Kind)) RefreshStationsLinkedTo(pool);
        }

        // Plots changed hands: the pools may have too (a zone grown, split or given up), and the
        // owners of storage with them. Whoever has storage open gets it again.
        private void OnClaimsChanged()
        {
            foreach (KeyValuePair<ulong, int> opened in openedBy)
            {
                if (store.TryGet(opened.Value, out Storage storage)) SendContents(opened.Key, storage);
            }
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
                    if (StorageRules.IsStockpile(storage.Kind)) WritePiles(writer, storage.Piles);
                });
            }
        }

        // What a viewer is sent: whether it is theirs, and what its pool holds (a chest pool's items
        // added up, a stockpile pool's piles one stockpile after another).
        private void SendContents(ulong clientId, Storage storage)
        {
            if (StorageRules.IsStation(storage.Kind))
            {
                SendStation(clientId, storage);
                return;
            }

            bool owner = registry.TryGetPlayerId(clientId, out string playerId) && playerId == OwnerOf(storage);
            List<Storage> pool = PoolOf(storage);

            if (clientId == networkManager.LocalClientId)
            {
                ShowOpened(storage.Id, storage.Kind, owner, MembersOf(pool));
                return;
            }

            Send(ContentsMessage, clientId, writer =>
            {
                writer.WriteValueSafe(storage.Id);
                writer.WriteValueSafe((byte)storage.Kind);
                writer.WriteValueSafe(owner);
                WriteMembers(writer, pool);
            });
        }

        // Each storage of a pool (or each chest linked to a station), one after another: its id and kind,
        // then a chest's items or a stockpile's piles (large and small stockpiles have their own).
        private static void WriteMembers(FastBufferWriter writer, List<Storage> pool)
        {
            writer.WriteValueSafe(pool.Count);
            foreach (Storage member in pool)
            {
                writer.WriteValueSafe(member.Id);
                writer.WriteValueSafe((byte)member.Kind);
                if (StorageRules.IsStockpile(member.Kind))
                {
                    WritePiles(writer, member.Piles);
                    continue;
                }

                writer.WriteValueSafe(member.Contents.DistinctCount);
                foreach (KeyValuePair<int, int> entry in member.Contents.Entries)
                {
                    writer.WriteValueSafe(entry.Key);
                    writer.WriteValueSafe(entry.Value);
                }
            }
        }

        // Null if they cannot be a pool's storage.
        private static List<StorageMember> ReadMembers(FastBufferReader reader)
        {
            reader.ReadValueSafe(out int count);
            if (count < 0 || count > MaxPoolMembers) return null;

            var members = new List<StorageMember>();
            for (int i = 0; i < count; i++)
            {
                reader.ReadValueSafe(out int id);
                reader.ReadValueSafe(out byte kind);
                if (!StorageRules.IsKind(kind) || StorageRules.IsStation((StorageKind)kind)) return null;

                var member = new StorageMember { Id = id, Kind = (StorageKind)kind };
                if (StorageRules.IsStockpile(member.Kind))
                {
                    member.Piles = new StockpilePiles(member.Kind);
                    if (!ReadPiles(reader, member.Piles)) return null;
                }
                else
                {
                    reader.ReadValueSafe(out int entries);
                    if (entries < 0 || entries > 4096) return null;

                    for (int e = 0; e < entries; e++)
                    {
                        reader.ReadValueSafe(out int item);
                        reader.ReadValueSafe(out int amount);
                        if (amount > 0 && StorageRules.AcceptsInChest(item)) member.Contents.Add(item, amount);
                    }
                }

                members.Add(member);
            }

            return members;
        }

        // The host's own copies of a pool's storage, as a client would read them.
        private static List<StorageMember> MembersOf(List<Storage> pool)
        {
            var members = new List<StorageMember>();
            foreach (Storage storage in pool)
            {
                var member = new StorageMember { Id = storage.Id, Kind = storage.Kind };
                if (StorageRules.IsStockpile(storage.Kind))
                {
                    member.Piles = new StockpilePiles(storage.Piles.Count, storage.Piles.PileSize);
                    member.Piles.CopyFrom(storage.Piles);
                }
                else
                {
                    member.Contents.CopyFrom(storage.Contents);
                }

                members.Add(member);
            }

            return members;
        }

        // Who may open a storage and move items in and out: anyone while it stands off claimed land
        // (public), else the land's owner and the players they allow on their land.
        private bool CanUse(string playerId, Storage storage)
        {
            if (string.IsNullOrEmpty(playerId)) return false;

            return !TryLandOf(storage, out int x, out int z) || claimSync.CanAct(playerId, x, z);
        }

        // The claimed land a storage stands on: the column of its first cell on claimed land. Its
        // owner and its pool are that land's.
        private bool TryLandOf(Storage storage, out int x, out int z)
        {
            for (int i = 0; i < storage.CellCount; i++)
            {
                BlockPos cell = storage.Cell(i);
                if (!claimSync.IsClaimed(cell.X, cell.Z)) continue;

                x = cell.X;
                z = cell.Z;
                return true;
            }

            x = z = 0;
            return false;
        }

        // Server side: whose a storage is: the owner of the land it stands on, else who placed it.
        private string OwnerOf(Storage storage)
        {
            return TryLandOf(storage, out int x, out int z) ? claimSync.OwnerIdAt(x, z) ?? storage.OwnerId : storage.OwnerId;
        }

        // Server side: the storage sharing what this one holds: the chests, or the stockpiles (large
        // and small), on the same claimed zone, in id order; off claimed land, itself alone.
        private List<Storage> PoolOf(Storage storage)
        {
            var pool = new List<Storage>();
            if (StorageRules.IsStation(storage.Kind) || !TryLandOf(storage, out int x, out int z))
            {
                pool.Add(storage);
                return pool;
            }

            int zone = claimSync.ZoneAt(x, z);
            foreach (Storage other in store.All)
            {
                if (PoolClassOf(other.Kind) != PoolClassOf(storage.Kind)) continue;
                if (other == storage || (TryLandOf(other, out int ox, out int oz) && claimSync.ZoneAt(ox, oz) == zone)) pool.Add(other);
            }

            pool.Sort((a, b) => a.Id.CompareTo(b.Id));
            return pool;
        }

        // What shares with what: chests together, stockpiles (large and small) together, and a station
        // with nothing.
        private static int PoolClassOf(StorageKind kind) => StorageRules.IsStockpile(kind) ? 1 : kind == StorageKind.Chest ? 0 : 2;

        // Whether a storage is open to everyone (none of its cells lies on claimed land), as this
        // machine knows the claims: for the aiming label and the storage window.
        public bool IsPublic(int id) => running && shown.TryGet(id, out Storage storage) && IsPublic(storage);

        // Whether a storage is open to everyone: none of its cells lies on claimed land.
        private bool IsPublic(Storage storage)
        {
            for (int i = 0; i < storage.CellCount; i++)
            {
                BlockPos cell = storage.Cell(i);
                if (claimSync.IsClaimed(cell.X, cell.Z)) return false;
            }

            return true;
        }

        // Closes storage that is no longer in reach of who opened it, or that they may no longer use
        // (removed from its access list or from its owner's land).
        private void CheckViewers()
        {
            if (openedBy.Count == 0) return;

            leaving.Clear();
            foreach (KeyValuePair<ulong, int> opened in openedBy)
            {
                if (!store.TryGet(opened.Value, out Storage storage) || !InReach(opened.Key, storage, 1f)) leaving.Add(opened.Key);
                else if (!registry.TryGetPlayerId(opened.Key, out string playerId) || !CanUse(playerId, storage)) leaving.Add(opened.Key);
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
            if (StorageRules.IsStockpile(storage.Kind)) WritePiles(writer, storage.Piles);
        }

        // As many piles as the stockpile's kind has: the reader knows them from its kind.
        private static void WritePiles(FastBufferWriter writer, StockpilePiles piles)
        {
            for (int pile = 0; pile < piles.Count; pile++)
            {
                writer.WriteValueSafe(piles.ItemAt(pile));
                writer.WriteValueSafe((byte)piles.CountAt(pile));
            }
        }

        // Fills piles from the wire; false, changing nothing, if anything is out of range.
        private static bool ReadPiles(FastBufferReader reader, StockpilePiles into)
        {
            var read = new StockpilePiles(into.Count, into.PileSize);
            bool valid = true;
            for (int pile = 0; pile < into.Count; pile++)
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
            reader.ReadValueSafe(out int member);
            reader.ReadValueSafe(out bool byItem);
            Move(senderId, id, deposit, itemOrPile, count, member, byItem);
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
            if (StorageRules.IsStockpile(storage.Kind)) ReadPiles(reader, storage.Piles);
        }

        private void OnContents(ulong senderId, FastBufferReader reader)
        {
            reader.ReadValueSafe(out int id);
            reader.ReadValueSafe(out byte kind);
            reader.ReadValueSafe(out bool owner);
            if (!StorageRules.IsKind(kind)) return;

            List<StorageMember> members = ReadMembers(reader);
            if (members == null || members.Count < 1) return;

            ShowOpened(id, (StorageKind)kind, owner, members);
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
            if (StorageRules.IsStockpile(storage.Kind) && !ReadPiles(reader, storage.Piles)) return false;

            shown.Put(storage);
            details[id] = new Shown { OwnerName = ownerName, Yours = yours, Open = open };
            return true;
        }

        // ---- The local player's open storage ----

        private void ShowOpened(int id, StorageKind kind, bool owner, List<StorageMember> members)
        {
            OpenedId = id;
            OpenedKind = kind;
            OpenedIsOwner = owner;
            OpenedMembers = members.Count;
            openedMembers.Clear();
            openedMembers.AddRange(members);
            openedContents.Clear();
            openedStockpiles.Clear();
            foreach (StorageMember member in members)
            {
                if (member.Piles != null) openedStockpiles.Add(member.Piles);
                else AddAll(openedContents, member.Contents);
            }

            OpenedChanged?.Invoke();
        }

        private static void AddAll(Inventory into, Inventory from)
        {
            foreach (KeyValuePair<int, int> entry in from.Entries)
            {
                into.Add(entry.Key, entry.Value);
            }
        }

        // Where a storage stands, as every machine knows.
        public bool TryPositionOf(int storageId, out BlockPos position)
        {
            position = default;
            if (!running || !shown.TryGet(storageId, out Storage storage)) return false;

            position = storage.Position;
            return true;
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

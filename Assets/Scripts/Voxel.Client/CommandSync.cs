using System;
using System.Collections.Generic;
using System.Text;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using Voxel.Core;

namespace Voxel.Client
{
    // Players' commands (Command): typed in the console (CommandConsole) or sent by the map's
    // "teleport here", run by the server, which answers the player who sent them. Every player may
    // use them. A teleport is applied by the player's own machine, since each player moves its own
    // body (owner-authoritative NetworkTransform): the server only tells it where to go.
    //
    // A spawn takes place where the player aims: the client sends the column under its crosshair
    // with the command.
    public class CommandSync : MonoBehaviour
    {
        private const string RunMessage = "Cmd.Run";
        private const string ReplyMessage = "Cmd.Reply";
        private const string TeleportMessage = "Cmd.Teleport";

        // How far ahead of the player a spawn takes place when the crosshair is on nothing.
        private const float SpawnDistance = 3f;

        [SerializeField] private NetworkManager networkManager;
        [SerializeField] private WorldSync worldSync;
        [SerializeField] private EcosystemSync ecosystemSync;
        [SerializeField] private ClaimSync claimSync;
        [SerializeField] private InventorySync inventorySync;

        private bool serverRunning;
        private bool clientRunning;

        // The server's answers to this machine's commands, for the console.
        public event Action<string> Replied;

        private CustomMessagingManager Messaging => networkManager.CustomMessagingManager;

        // To call right after the server or host has started.
        public void StartServer()
        {
            serverRunning = true;
            Messaging.RegisterNamedMessageHandler(RunMessage, OnRun);
        }

        // To call right after a client's connection has been launched.
        public void StartClient()
        {
            clientRunning = true;
            Messaging.RegisterNamedMessageHandler(ReplyMessage, OnReply);
            Messaging.RegisterNamedMessageHandler(TeleportMessage, OnTeleport);
        }

        public void Stop()
        {
            serverRunning = false;
            clientRunning = false;
        }

        // Sends a command typed by the local player.
        public void Run(string text)
        {
            if (!serverRunning && !clientRunning) return;
            if (text.Length > Command.MaxLength) text = text.Substring(0, Command.MaxLength);

            AimedColumn(out int aimX, out int aimZ);
            if (serverRunning)
            {
                Execute(networkManager.LocalClientId, text, aimX, aimZ);
                return;
            }

            using (var writer = new FastBufferWriter(64, Allocator.Temp, 1024))
            {
                writer.WriteValueSafe(text);
                writer.WriteValueSafe(aimX);
                writer.WriteValueSafe(aimZ);
                Messaging.SendNamedMessage(RunMessage, NetworkManager.ServerClientId, writer, NetworkDelivery.ReliableSequenced);
            }
        }

        // The block column under the crosshair, within reach, or a few blocks ahead of the player.
        private void AimedColumn(out int x, out int z)
        {
            x = z = -1;
            World world = worldSync.World;
            Camera eye = Camera.main;
            if (world == null || eye == null) return;

            Vector3 origin = eye.transform.position;
            Vector3 forward = eye.transform.forward;
            if (VoxelRaycast.Cast(world, origin.ToNumerics(), forward.ToNumerics(), PlayerRules.Reach, out VoxelHit hit))
            {
                x = hit.Block.X;
                z = hit.Block.Z;
                return;
            }

            Vector3 ahead = origin + new Vector3(forward.x, 0f, forward.z).normalized * SpawnDistance;
            x = Mathf.FloorToInt(ahead.x);
            z = Mathf.FloorToInt(ahead.z);
        }

        private void OnRun(ulong senderId, FastBufferReader reader)
        {
            if (!serverRunning) return;

            reader.ReadValueSafe(out string text);
            reader.ReadValueSafe(out int aimX);
            reader.ReadValueSafe(out int aimZ);
            if (text.Length > Command.MaxLength) return;

            Execute(senderId, text, aimX, aimZ);
        }

        // Server side: runs a player's command and answers them.
        private void Execute(ulong clientId, string text, int aimX, int aimZ)
        {
            if (!Command.TryParse(text, out Command command, out string error))
            {
                Reply(clientId, error);
                return;
            }

            switch (command.Kind)
            {
                case CommandKind.Help:
                    Reply(clientId, Command.HelpText);
                    break;
                case CommandKind.Players:
                    Reply(clientId, PlayerList(clientId));
                    break;
                case CommandKind.TeleportTo:
                    TeleportToPoint(clientId, command);
                    break;
                case CommandKind.TeleportToPlayer:
                    TeleportToPlayer(clientId, command.Player);
                    break;
                case CommandKind.Spawn:
                    Spawn(clientId, command, aimX, aimZ);
                    break;
                case CommandKind.Give:
                    Give(clientId, command);
                    break;
            }
        }

        // Items into a player's bag, what fits (the carrying limits hold); the item named in English or
        // in the server's language.
        private void Give(ulong clientId, Command command)
        {
            ulong target = command.Player < 0 ? clientId : (ulong)command.Player;
            if (!networkManager.ConnectedClients.ContainsKey(target))
            {
                Reply(clientId, $"There is no player #{command.Player}. /players lists them.");
                return;
            }

            if (!TryFindItem(command.Name, out int item))
            {
                Reply(clientId, $"There is no item called '{command.Name}'. Write its name as the inventory shows it, for instance /give oak wood 20.");
                return;
            }

            string name = EnglishNameOf(item);
            int given = inventorySync.Give(target, item, command.Count);
            string whose = target == clientId ? "your" : $"player #{target}'s";
            if (given == 0)
            {
                Reply(clientId, $"No room for {name} in {whose} bag (40 blocks, 30 kg).");
                return;
            }

            Reply(clientId, given == command.Count
                ? $"Gave {given} × {name} into {whose} bag."
                : $"Gave {given} × {name} into {whose} bag: no room for more.");
            if (target != clientId) Reply(target, $"Player #{clientId} gave you {given} × {name}.");
        }

        private bool TryFindItem(string normalisedName, out int found)
        {
            foreach (int item in GivableItems())
            {
                if (Command.Normalise(EnglishNameOf(item)) == normalisedName || Command.Normalise(ItemCatalog.NameOf(item, ecosystemSync)) == normalisedName)
                {
                    found = item;
                    return true;
                }
            }

            found = Items.None;
            return false;
        }

        // Every item a player may hold: blocks (not water), what is crafted, each species' seeds, wood
        // and crop, each animal's carcass, the dishes.
        private IEnumerable<int> GivableItems()
        {
            for (int type = 1; type <= Blocks.LastType; type++)
            {
                if (type != Blocks.Water && Items.IsValid(type)) yield return type;
            }

            foreach (int item in new[] { Items.Straw, Items.Arrow, Items.Chest, Items.Stockpile, Items.SmallStockpile, Items.Campfire })
            {
                yield return item;
            }

            for (int tool = Items.Shovel; tool <= Items.Bow; tool++)
            {
                if (Items.IsTool(tool)) yield return tool;
            }

            for (int species = 0; species < ecosystemSync.PlantSpeciesCount; species++)
            {
                yield return Items.Seed(species);
                if (ecosystemSync.IsTree(species)) yield return Items.WoodOf(species);
                if (ecosystemSync.IsCrop(species)) yield return Items.Produce(species);
            }

            for (int animal = 0; animal < ecosystemSync.AnimalSpeciesCount; animal++)
            {
                yield return Items.Carcass(animal);
            }

            for (int dish = 0; dish < Dishes.All.Count; dish++)
            {
                yield return Items.Dish(dish);
            }
        }

        // An item's name in English, which the server answers in.
        private string EnglishNameOf(int item)
        {
            if (Items.IsSeed(item)) return $"{ecosystemSync.SpeciesName(false, Items.SeedSpecies(item))} seeds";
            if (Items.IsWood(item)) return $"{ecosystemSync.SpeciesName(false, Items.WoodSpecies(item))} wood";
            if (Items.IsProduce(item)) return EcosystemSync.DisplayName(ecosystemSync.ProduceName(Items.ProduceSpecies(item)));
            if (Items.IsCarcass(item)) return $"{ecosystemSync.SpeciesName(true, Items.CarcassSpecies(item))} carcass";
            return Items.NameOf(item);
        }

        private string PlayerList(ulong clientId)
        {
            var text = new StringBuilder("Players:");
            foreach (NetworkClient client in networkManager.ConnectedClientsList)
            {
                if (client.PlayerObject == null) continue;

                Vector3 p = client.PlayerObject.transform.position;
                text.Append($"  #{client.ClientId} at {Mathf.FloorToInt(p.x)} {Mathf.FloorToInt(p.y)} {Mathf.FloorToInt(p.z)}");
                if (client.ClientId == clientId) text.Append(" (you)");
                text.Append(';');
            }

            return text.ToString().TrimEnd(';');
        }

        private void TeleportToPoint(ulong clientId, Command command)
        {
            World world = worldSync.World;
            if (world == null) return;

            if (!world.InBounds(command.X, 0, command.Z))
            {
                Reply(clientId, $"That is outside the world: x and z go from 0 to {world.SizeX - 1} and {world.SizeZ - 1}.");
                return;
            }

            // Without a height, on top of the column, water included (the player then swims at the surface).
            int y = command.Y ?? world.GetTopHeight(command.X, command.Z);
            y = Mathf.Clamp(y, 1, world.SizeY);
            Vector3 arrival = FreeSpaceAt(world, new Vector3(command.X + 0.5f, y, command.Z + 0.5f));
            SendTeleport(clientId, arrival);
            Reply(clientId, $"Teleported to {command.X} {Mathf.FloorToInt(arrival.y)} {command.Z}.");
        }

        private void TeleportToPlayer(ulong clientId, int number)
        {
            World world = worldSync.World;
            if (world == null) return;

            if (number < 0 || !networkManager.ConnectedClients.TryGetValue((ulong)number, out NetworkClient target) || target.PlayerObject == null)
            {
                Reply(clientId, $"There is no player #{number}. /players lists them.");
                return;
            }

            if ((ulong)number == clientId)
            {
                Reply(clientId, "That is you.");
                return;
            }

            SendTeleport(clientId, FreeSpaceAt(world, target.PlayerObject.transform.position));
            Reply(clientId, $"Teleported to player #{number}.");
        }

        private static Vector3 FreeSpaceAt(World world, Vector3 position)
        {
            return VoxelCollision.RaiseToFreeSpace(world, position.ToNumerics(), PlayerRules.HalfWidth, PlayerRules.Height).ToUnity();
        }

        private void Spawn(ulong clientId, Command command, int aimX, int aimZ)
        {
            if (!ecosystemSync.TryFindSpecies(command.Name, out bool isAnimal, out int index))
            {
                Reply(clientId, $"There is no animal or plant called '{command.Name}'. {ecosystemSync.SpeciesList()}");
                return;
            }

            // Without a column from the client, at the player's feet.
            if (aimX < 0 && networkManager.ConnectedClients.TryGetValue(clientId, out NetworkClient client) && client.PlayerObject != null)
            {
                Vector3 p = client.PlayerObject.transform.position;
                aimX = Mathf.FloorToInt(p.x);
                aimZ = Mathf.FloorToInt(p.z);
            }

            // Not on someone else's land.
            if (!claimSync.Check(clientId, aimX, aimZ)) return;

            string name = ecosystemSync.SpeciesName(isAnimal, index);
            int placed = ecosystemSync.Spawn(isAnimal, index, command.Count, aimX, aimZ);
            if (placed == 0)
            {
                string why = isAnimal ? "where it can stand (land, water or shore, by species)" : "of its own ground (grass or dirt for most, sand for cacti, the water's edge for reeds, willows and cypresses, water one block deep for lilies)";
                Reply(clientId, $"No room for {name} here: it needs a free column {why}.");
            }
            else
            {
                Reply(clientId, placed == command.Count ? $"Spawned {placed} × {name}." : $"Spawned {placed} × {name} (no room for more nearby).");
            }
        }

        // Server side: the answer goes to the player who sent the command.
        private void Reply(ulong clientId, string text)
        {
            if (networkManager.IsClient && clientId == networkManager.LocalClientId)
            {
                Replied?.Invoke(text);
                return;
            }

            using (var writer = new FastBufferWriter(256, Allocator.Temp, 8192))
            {
                writer.WriteValueSafe(text);
                Messaging.SendNamedMessage(ReplyMessage, clientId, writer, NetworkDelivery.ReliableSequenced);
            }
        }

        private void SendTeleport(ulong clientId, Vector3 position)
        {
            if (networkManager.IsClient && clientId == networkManager.LocalClientId)
            {
                ApplyTeleport(position);
                return;
            }

            using (var writer = new FastBufferWriter(16, Allocator.Temp))
            {
                writer.WriteValueSafe(position);
                Messaging.SendNamedMessage(TeleportMessage, clientId, writer, NetworkDelivery.ReliableSequenced);
            }
        }

        private void OnReply(ulong senderId, FastBufferReader reader)
        {
            if (!clientRunning || senderId != NetworkManager.ServerClientId) return;

            reader.ReadValueSafe(out string text);
            Replied?.Invoke(text);
        }

        private void OnTeleport(ulong senderId, FastBufferReader reader)
        {
            if (!clientRunning || senderId != NetworkManager.ServerClientId) return;

            reader.ReadValueSafe(out Vector3 position);
            ApplyTeleport(position);
        }

        private void ApplyTeleport(Vector3 position)
        {
            NetworkObject player = networkManager.SpawnManager?.GetLocalPlayerObject();
            if (player != null && player.TryGetComponent(out PlayerController controller)) controller.Teleport(position);
        }
    }
}

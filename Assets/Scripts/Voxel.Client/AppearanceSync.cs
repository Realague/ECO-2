using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using Voxel.Core;

namespace Voxel.Client
{
    // The players' appearance. The server keeps it in each player's saved state and shows it on the
    // player's model (PlayerModel), which every machine then displays. A player chooses it once, at
    // the first connection: the server refuses a second choice.
    public class AppearanceSync : MonoBehaviour
    {
        private const string ChooseMessage = "Look.Choose";
        private const string NameMessage = "Look.Name";
        private const string NameRefusedMessage = "Look.NameRefused";

        [SerializeField] private NetworkManager networkManager;

        private PlayerRegistry registry;
        private bool running;

        private CustomMessagingManager Messaging => networkManager.CustomMessagingManager;

        // To call right after the server or host has started.
        internal void StartServer(PlayerRegistry playerRegistry)
        {
            running = true;
            registry = playerRegistry;
            Messaging.RegisterNamedMessageHandler(ChooseMessage, OnChoose);
            Messaging.RegisterNamedMessageHandler(NameMessage, OnName);

            // The host's own player spawned while the network started, before this was ready.
            foreach (NetworkClient client in networkManager.ConnectedClientsList)
            {
                if (client.PlayerObject == null) continue;

                var model = client.PlayerObject.GetComponent<PlayerModel>();
                model.SetAppearanceCode(StoredCode(client.ClientId));
                model.SetPlayerName(StoredName(client.ClientId));
            }
        }

        // To call right after a client's connection has been launched.
        public void StartClient()
        {
            running = true;
            Messaging.RegisterNamedMessageHandler(NameRefusedMessage, OnNameRefused);
        }

        public void Stop()
        {
            registry = null;
            running = false;
        }

        // Server side: the code of the appearance saved for a connected player, or Appearance.NotChosen.
        public ushort StoredCode(ulong clientId)
        {
            if (registry == null || !registry.TryGetState(clientId, out PlayerState state)) return Appearance.NotChosen;

            return state.Appearance?.Encode() ?? Appearance.NotChosen;
        }

        // Server side: the name saved for a connected player, or empty.
        public string StoredName(ulong clientId)
        {
            return registry != null && registry.TryGetState(clientId, out PlayerState state) ? state.Name ?? "" : "";
        }

        // Raised on the player's machine when the server refused the name they chose, with the reason.
        public event System.Action<string> NameRefused;

        // The local player's name, sent to the server (once: it is then kept).
        public void RequestName(string name)
        {
            if (!running) return;

            if (registry != null)
            {
                ChooseName(networkManager.LocalClientId, name);
                return;
            }

            using (var writer = new FastBufferWriter(FastBufferWriter.GetWriteSize(name), Allocator.Temp))
            {
                writer.WriteValueSafe(name);
                Messaging.SendNamedMessage(NameMessage, NetworkManager.ServerClientId, writer, NetworkDelivery.ReliableFragmentedSequenced);
            }
        }

        private void OnName(ulong senderId, FastBufferReader reader)
        {
            reader.ReadValueSafe(out string name);
            ChooseName(senderId, name);
        }

        private void ChooseName(ulong clientId, string text)
        {
            if (registry == null || !registry.TryGetState(clientId, out PlayerState state) || !registry.TryGetPlayerId(clientId, out string playerId)) return;
            if (state.Name.Length > 0) return;

            string reason = null;
            if (!PlayerNames.TryNormalize(text, out string name)) reason = NameRules;
            else if (registry.IsNameTaken(name, playerId)) reason = "Name taken";

            if (reason != null)
            {
                RefuseName(clientId, reason);
                return;
            }

            state.Name = name;
            if (networkManager.ConnectedClients.TryGetValue(clientId, out NetworkClient client) && client.PlayerObject != null)
            {
                client.PlayerObject.GetComponent<PlayerModel>().SetPlayerName(name);
            }
        }

        // What a name may be, shown when one is refused.
        public static readonly string NameRules = $"{PlayerNames.MinLength} to {PlayerNames.MaxLength} letters, digits, spaces, _ or -";

        private void RefuseName(ulong clientId, string reason)
        {
            if (clientId == networkManager.LocalClientId)
            {
                NameRefused?.Invoke(reason);
                return;
            }

            using (var writer = new FastBufferWriter(FastBufferWriter.GetWriteSize(reason), Allocator.Temp))
            {
                writer.WriteValueSafe(reason);
                Messaging.SendNamedMessage(NameRefusedMessage, clientId, writer, NetworkDelivery.ReliableFragmentedSequenced);
            }
        }

        private void OnNameRefused(ulong senderId, FastBufferReader reader)
        {
            reader.ReadValueSafe(out string reason);
            NameRefused?.Invoke(reason);
        }

        // The local player's choice, sent to the server.
        public void RequestAppearance(Appearance appearance)
        {
            if (!running) return;

            if (registry != null)
            {
                Choose(networkManager.LocalClientId, appearance);
                return;
            }

            using (var writer = new FastBufferWriter(sizeof(ushort), Allocator.Temp))
            {
                writer.WriteValueSafe(appearance.Encode());
                Messaging.SendNamedMessage(ChooseMessage, NetworkManager.ServerClientId, writer, NetworkDelivery.ReliableFragmentedSequenced);
            }
        }

        private void OnChoose(ulong senderId, FastBufferReader reader)
        {
            reader.ReadValueSafe(out ushort code);
            if (Appearance.TryDecode(code, out Appearance appearance)) Choose(senderId, appearance);
        }

        private void Choose(ulong clientId, Appearance appearance)
        {
            if (!appearance.IsValid || !registry.TryGetState(clientId, out PlayerState state)) return;
            if (state.Appearance.HasValue) return;

            state.Appearance = appearance;
            if (networkManager.ConnectedClients.TryGetValue(clientId, out NetworkClient client) && client.PlayerObject != null)
            {
                client.PlayerObject.GetComponent<PlayerModel>().SetAppearanceCode(appearance.Encode());
            }
        }
    }
}

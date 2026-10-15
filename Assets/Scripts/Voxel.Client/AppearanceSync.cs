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

            // The host's own player spawned while the network started, before this was ready.
            foreach (NetworkClient client in networkManager.ConnectedClientsList)
            {
                if (client.PlayerObject != null) client.PlayerObject.GetComponent<PlayerModel>().SetAppearanceCode(StoredCode(client.ClientId));
            }
        }

        // To call right after a client's connection has been launched.
        public void StartClient()
        {
            running = true;
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

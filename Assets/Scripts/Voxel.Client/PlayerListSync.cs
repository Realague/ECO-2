using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;

namespace Voxel.Client
{
    // The players online, with their ping, for the list shown while P is held (PlayerListView). The
    // server measures each player's round trip on its own transport (UnityTransport.GetCurrentRtt,
    // in milliseconds) and sends every player the whole list once a second (Players.List: each
    // player's NGO client id, name and ping); the host builds it for itself. The host's own player has
    // no ping: it plays on the server. A player still choosing a name has an empty one.
    public class PlayerListSync : MonoBehaviour
    {
        private const string ListMessage = "Players.List";

        // How often the list is worked out and sent.
        private const float Interval = 1f;

        // The most a ping is sent as: anything slower reads as this.
        private const ulong MaxPing = ushort.MaxValue;

        public struct Entry
        {
            public ulong ClientId;
            public string Name;
            public int Ping;
            public bool IsHost;
        }

        [SerializeField] private NetworkManager networkManager;

        private readonly List<Entry> players = new List<Entry>();
        private bool serverRunning;
        private bool clientRunning;
        private float nextList;

        // The players online, sorted by name (those without one last), as last worked out or received.
        public IReadOnlyList<Entry> Players => players;

        private CustomMessagingManager Messaging => networkManager.CustomMessagingManager;

        // To call right after the server or host has started.
        public void StartServer()
        {
            serverRunning = true;
            nextList = 0f;
        }

        // To call right after a client's connection has been launched.
        public void StartClient()
        {
            clientRunning = true;
            Messaging.RegisterNamedMessageHandler(ListMessage, OnList);
        }

        public void Stop()
        {
            serverRunning = false;
            clientRunning = false;
            players.Clear();
        }

        private void Update()
        {
            if (!serverRunning || !networkManager.IsServer || Time.unscaledTime < nextList) return;

            nextList = Time.unscaledTime + Interval;
            Build();
            Send();
        }

        private void Build()
        {
            players.Clear();
            var transport = networkManager.NetworkConfig.NetworkTransport as UnityTransport;
            foreach (NetworkClient client in networkManager.ConnectedClientsList)
            {
                bool isHost = networkManager.IsHost && client.ClientId == NetworkManager.ServerClientId;
                PlayerModel model = client.PlayerObject != null ? client.PlayerObject.GetComponent<PlayerModel>() : null;
                ulong ping = isHost || transport == null ? 0 : transport.GetCurrentRtt(client.ClientId);
                players.Add(new Entry
                {
                    ClientId = client.ClientId,
                    Name = model != null ? model.PlayerName : "",
                    Ping = (int)Math.Min(ping, MaxPing),
                    IsHost = isHost
                });
            }

            Sort(players);
        }

        private void Send()
        {
            if (networkManager.ConnectedClientsIds.Count == 0) return;

            using (var writer = new FastBufferWriter(256, Allocator.Temp, 64 * 1024))
            {
                writer.WriteValueSafe((ushort)players.Count);
                foreach (Entry entry in players)
                {
                    writer.WriteValueSafe(entry.ClientId);
                    writer.WriteValueSafe(entry.Name ?? "");
                    writer.WriteValueSafe((ushort)entry.Ping);
                    writer.WriteValueSafe(entry.IsHost);
                }

                foreach (ulong clientId in networkManager.ConnectedClientsIds)
                {
                    // The host reads its own list.
                    if (clientId == NetworkManager.ServerClientId) continue;

                    Messaging.SendNamedMessage(ListMessage, clientId, writer, NetworkDelivery.ReliableSequenced);
                }
            }
        }

        private void OnList(ulong senderId, FastBufferReader reader)
        {
            if (!clientRunning || senderId != NetworkManager.ServerClientId) return;

            reader.ReadValueSafe(out ushort count);
            players.Clear();
            for (int i = 0; i < count; i++)
            {
                reader.ReadValueSafe(out ulong clientId);
                reader.ReadValueSafe(out string name);
                reader.ReadValueSafe(out ushort ping);
                reader.ReadValueSafe(out bool isHost);
                players.Add(new Entry { ClientId = clientId, Name = name, Ping = ping, IsHost = isHost });
            }
        }

        private static void Sort(List<Entry> entries)
        {
            entries.Sort((a, b) =>
            {
                bool aNamed = !string.IsNullOrEmpty(a.Name), bNamed = !string.IsNullOrEmpty(b.Name);
                if (aNamed != bNamed) return aNamed ? -1 : 1;
                int byName = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
                return byName != 0 ? byName : a.ClientId.CompareTo(b.ClientId);
            });
        }
    }
}

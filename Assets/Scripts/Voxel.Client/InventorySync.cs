using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using Voxel.Core;

namespace Voxel.Client
{
    // L'inventaire et la spécialité des joueurs. Le serveur les détient (dans l'état sauvegardé de chaque
    // joueur) et décide de tout : fabriquer, choisir une spécialité, donner, planter. Chaque client ne
    // connaît que son propre inventaire, que le serveur lui renvoie à chaque changement.
    public class InventorySync : MonoBehaviour
    {
        private const string StateMessage = "Inv.State";
        private const string CraftMessage = "Inv.Craft";
        private const string SpecialityMessage = "Inv.Speciality";
        private const string GiveMessage = "Inv.Give";
        private const string PlantMessage = "Inv.Plant";

        [SerializeField] private NetworkManager networkManager;
        [SerializeField] private EcosystemSync ecosystemSync;

        // Copie locale de l'inventaire du joueur de cette machine, quand elle n'est pas le serveur.
        private readonly Inventory received = new Inventory();
        private Speciality receivedSpeciality;
        private PlayerRegistry registry;
        private bool running;

        // Inventaire et spécialité du joueur local. Sur l'hôte, ce sont directement ceux du serveur.
        public Inventory Inventory => TryGetLocalState(out PlayerState state) ? state.Inventory : received;

        public Speciality Speciality => TryGetLocalState(out PlayerState state) ? state.Speciality : receivedSpeciality;

        private CustomMessagingManager Messaging => networkManager.CustomMessagingManager;

        // À appeler juste après le démarrage du serveur ou de l'hôte.
        internal void StartServer(PlayerRegistry playerRegistry)
        {
            running = true;
            registry = playerRegistry;
            Messaging.RegisterNamedMessageHandler(CraftMessage, OnCraft);
            Messaging.RegisterNamedMessageHandler(SpecialityMessage, OnSpeciality);
            Messaging.RegisterNamedMessageHandler(GiveMessage, OnGive);
            Messaging.RegisterNamedMessageHandler(PlantMessage, OnPlant);
            networkManager.OnClientConnectedCallback += Publish;
        }

        // À appeler juste après le lancement de la connexion d'un client.
        public void StartClient()
        {
            running = true;
            Messaging.RegisterNamedMessageHandler(StateMessage, OnState);
        }

        public void Stop()
        {
            if (!running) return;

            if (registry != null) networkManager.OnClientConnectedCallback -= Publish;

            registry = null;
            received.Clear();
            receivedSpeciality = Speciality.None;
            running = false;
        }

        // Côté serveur : retire des objets à un joueur. Renvoie faux, sans rien changer, s'il n'en a pas assez.
        public bool TryConsume(ulong clientId, int item, int count = 1)
        {
            if (registry == null || !registry.TryGetState(clientId, out PlayerState state)) return false;
            if (!state.Inventory.TryRemove(item, count)) return false;

            Publish(clientId);
            return true;
        }

        // Côté serveur : ajoute des objets à un joueur.
        public void Give(ulong clientId, int item, int count)
        {
            if (item == Items.None || count <= 0) return;
            if (registry == null || !registry.TryGetState(clientId, out PlayerState state)) return;

            state.Inventory.Add(item, count);
            Publish(clientId);
        }

        public void RequestCraft(int recipeIndex)
        {
            if (!running) return;

            if (registry != null) Craft(networkManager.LocalClientId, recipeIndex);
            else Send(CraftMessage, recipeIndex);
        }

        public void RequestSpeciality(Speciality speciality)
        {
            if (!running) return;

            if (registry != null) Choose(networkManager.LocalClientId, speciality);
            else Send(SpecialityMessage, (int)speciality);
        }

        // Donne des objets au joueur le plus proche, s'il est à portée.
        public void RequestGive(int item, int count)
        {
            if (!running) return;

            if (registry != null) GiveToNearest(networkManager.LocalClientId, item, count);
            else Send(GiveMessage, item, count);
        }

        // Plante une graine dans la colonne de blocs donnée. Réservé aux jardiniers.
        public void RequestPlant(int seed, int x, int z)
        {
            if (!running) return;

            if (registry != null) Plant(networkManager.LocalClientId, seed, x, z);
            else Send(PlantMessage, seed, x, z);
        }

        private bool TryGetLocalState(out PlayerState state)
        {
            state = null;
            return registry != null && networkManager.IsHost && registry.TryGetState(networkManager.LocalClientId, out state);
        }

        private void Craft(ulong clientId, int recipeIndex)
        {
            if (!registry.TryGetState(clientId, out PlayerState state)) return;

            if (Recipes.TryCraft(state.Inventory, state.Speciality, recipeIndex)) Publish(clientId);
        }

        // Une spécialité se choisit une seule fois.
        private void Choose(ulong clientId, Speciality speciality)
        {
            if (!registry.TryGetState(clientId, out PlayerState state)) return;
            if (state.Speciality != Speciality.None || !Specialities.IsChoice(speciality)) return;

            state.Speciality = speciality;
            Publish(clientId);
        }

        private void GiveToNearest(ulong clientId, int item, int count)
        {
            if (!registry.TryGetState(clientId, out PlayerState giver)) return;
            if (!networkManager.ConnectedClients.TryGetValue(clientId, out NetworkClient client) || client.PlayerObject == null) return;

            Vector3 position = client.PlayerObject.transform.position;
            ulong nearestId = 0;
            float nearest = PlayerRules.Reach * PlayerRules.Reach;
            bool found = false;

            foreach (NetworkClient other in networkManager.ConnectedClientsList)
            {
                if (other.ClientId == clientId || other.PlayerObject == null) continue;

                float distance = (other.PlayerObject.transform.position - position).sqrMagnitude;
                if (distance <= nearest)
                {
                    nearest = distance;
                    nearestId = other.ClientId;
                    found = true;
                }
            }

            if (!found || !registry.TryGetState(nearestId, out PlayerState receiver)) return;
            if (!Inventory.Transfer(giver.Inventory, receiver.Inventory, item, count)) return;

            Publish(clientId);
            Publish(nearestId);
        }

        private void Plant(ulong clientId, int seed, int x, int z)
        {
            if (!registry.TryGetState(clientId, out PlayerState state)) return;
            if (state.Speciality != Speciality.Gardener || !Items.IsSeed(seed) || !state.Inventory.Has(seed)) return;

            // La graine n'est dépensée que si elle a réellement germé.
            if (ecosystemSync.TryPlantSeed(clientId, Items.SeedSpecies(seed), x, z)) TryConsume(clientId, seed);
        }

        private void OnCraft(ulong senderId, FastBufferReader reader)
        {
            reader.ReadValueSafe(out int recipeIndex);
            Craft(senderId, recipeIndex);
        }

        private void OnSpeciality(ulong senderId, FastBufferReader reader)
        {
            reader.ReadValueSafe(out int speciality);
            Choose(senderId, (Speciality)speciality);
        }

        private void OnGive(ulong senderId, FastBufferReader reader)
        {
            reader.ReadValueSafe(out int item);
            reader.ReadValueSafe(out int count);
            GiveToNearest(senderId, item, count);
        }

        private void OnPlant(ulong senderId, FastBufferReader reader)
        {
            reader.ReadValueSafe(out int seed);
            reader.ReadValueSafe(out int x);
            reader.ReadValueSafe(out int z);
            Plant(senderId, seed, x, z);
        }

        // Envoie à un joueur son inventaire et sa spécialité. L'hôte lit les siens directement.
        private void Publish(ulong clientId)
        {
            if (clientId == networkManager.LocalClientId) return;
            if (!registry.TryGetState(clientId, out PlayerState state)) return;

            int size = sizeof(byte) + sizeof(int) + state.Inventory.DistinctCount * 2 * sizeof(int);
            using (var writer = new FastBufferWriter(size, Allocator.Temp))
            {
                writer.WriteValueSafe((byte)state.Speciality);
                writer.WriteValueSafe(state.Inventory.DistinctCount);
                foreach (System.Collections.Generic.KeyValuePair<int, int> entry in state.Inventory.Entries)
                {
                    writer.WriteValueSafe(entry.Key);
                    writer.WriteValueSafe(entry.Value);
                }

                Messaging.SendNamedMessage(StateMessage, clientId, writer, NetworkDelivery.ReliableFragmentedSequenced);
            }
        }

        private void OnState(ulong senderId, FastBufferReader reader)
        {
            reader.ReadValueSafe(out byte speciality);
            reader.ReadValueSafe(out int entries);

            receivedSpeciality = (Speciality)speciality;
            received.Clear();
            for (int i = 0; i < entries; i++)
            {
                reader.ReadValueSafe(out int item);
                reader.ReadValueSafe(out int count);
                received.Add(item, count);
            }
        }

        private void Send(string message, params int[] values)
        {
            using (var writer = new FastBufferWriter(values.Length * sizeof(int), Allocator.Temp))
            {
                foreach (int value in values)
                {
                    writer.WriteValueSafe(value);
                }

                Messaging.SendNamedMessage(message, NetworkManager.ServerClientId, writer, NetworkDelivery.ReliableFragmentedSequenced);
            }
        }
    }
}

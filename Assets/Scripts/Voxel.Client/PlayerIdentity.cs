using System;
using System.IO;
using UnityEngine;
using Voxel.Core;

namespace Voxel.Client
{
    // Identifiant stable de ce joueur, créé au premier lancement et relu ensuite. Le serveur s'en sert
    // pour retrouver les données du joueur ; il n'est pas secret et ne prouve pas l'identité.
    internal static class PlayerIdentity
    {
        // L'éditeur a son propre identifiant : on peut ainsi tester à deux sur une même machine,
        // l'éditeur d'un côté et le jeu construit de l'autre.
        private static string FilePath =>
            Path.Combine(Application.persistentDataPath, Application.isEditor ? "player-editor.id" : "player.id");

        public static string GetOrCreate()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    string saved = File.ReadAllText(FilePath).Trim();
                    if (ConnectionRequest.IsValidPlayerId(saved)) return saved;
                }

                string created = Guid.NewGuid().ToString("N");
                File.WriteAllText(FilePath, created);
                return created;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                Debug.LogWarning($"Identifiant de joueur non enregistré, il changera au prochain lancement : {e.Message}");
                return Guid.NewGuid().ToString("N");
            }
        }
    }
}

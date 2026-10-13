using System;
using System.IO;
using UnityEngine;
using Voxel.Core;

namespace Voxel.Client
{
    // Stable id of this player, created at first launch and read back afterwards. The server uses it
    // to find the player's data; it is not secret and does not prove identity.
    internal static class PlayerIdentity
    {
        // The Editor has its own id: two players can thus be tested on one machine,
        // the Editor on one side and the built game on the other.
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
                Debug.LogWarning($"Player id not saved, it will change at the next launch: {e.Message}");
                return Guid.NewGuid().ToString("N");
            }
        }
    }
}

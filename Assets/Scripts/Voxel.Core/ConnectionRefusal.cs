namespace Voxel.Core
{
    // Motifs pour lesquels le serveur refuse une connexion. Le client n'affiche que ceux-là : tout
    // autre texte de déconnexion vient de la couche réseau et n'est pas destiné au joueur.
    public static class ConnectionRefusal
    {
        public const string InvalidRequest = "Demande de connexion invalide.";
        public const string WrongPassword = "Mot de passe incorrect.";
        public const string AlreadyConnected = "Ce joueur est déjà connecté.";

        public static bool IsKnown(string reason) =>
            reason == InvalidRequest || reason == WrongPassword || reason == AlreadyConnected;
    }
}

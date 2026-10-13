using System;

namespace Eco.Sim
{
    // Décide combien de pas de simulation exécuter. La simulation avance par pas fixes de temps simulé
    // et ne lit jamais l'heure réelle : accélérer le temps revient à exécuter plus de pas par seconde,
    // et le résultat ne dépend que du nombre de pas.
    public sealed class SimClock
    {
        public const int MinutesPerStep = 10;
        public const int StepsPerDay = 24 * 60 / MinutesPerStep;

        // À vitesse 1, une minute de jeu dure une seconde réelle.
        public const float RealSecondsPerStep = 10f;
        public const float MaxMultiplier = 10000f;

        private float multiplier = 1f;
        private double pendingSteps;

        public float Multiplier
        {
            get => multiplier;
            set => multiplier = Math.Min(Math.Max(value, 0f), MaxMultiplier);
        }

        // Pas demandés explicitement (« avancer de N jours ») : ils sont toujours exécutés, aussi vite
        // que la machine le permet.
        public int QueuedSteps { get; private set; }

        // Vrai quand la machine ne tient pas le multiplicateur demandé.
        public bool IsBehind { get; private set; }

        public void Advance(float realSeconds)
        {
            pendingSteps += realSeconds * multiplier / RealSecondsPerStep;
        }

        public void QueueDays(int days)
        {
            if (days > 0) QueuedSteps += days * StepsPerDay;
        }

        // Renvoie vrai s'il y a un pas à exécuter maintenant, et le décompte.
        public bool TryTakeStep()
        {
            if (QueuedSteps > 0)
            {
                QueuedSteps--;
                return true;
            }

            if (pendingSteps >= 1.0)
            {
                pendingSteps -= 1.0;
                return true;
            }

            IsBehind = false;
            return false;
        }

        // À appeler quand le temps de calcul accordé à cette image est épuisé : le retard dû au
        // multiplicateur est abandonné plutôt qu'accumulé, sinon le serveur ne le rattraperait jamais.
        public void DropBacklog()
        {
            if (pendingSteps < 1.0) return;

            pendingSteps -= Math.Floor(pendingSteps);
            IsBehind = true;
        }
    }
}

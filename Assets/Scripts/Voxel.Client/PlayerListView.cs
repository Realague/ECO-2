using System.Collections.Generic;
using UnityEngine.UIElements;

namespace Voxel.Client
{
    // The list of players online in the HUD (player-list in Hud.uxml), shown by HudBanner while P is
    // held: a row per player (PlayerListSync), the local one highlighted, with their ping as five
    // bars, as many lit as the connection is good, and in milliseconds ("Host" for the host's own
    // player, which plays on the server). Rows are made again only when what they show changes.
    internal sealed class PlayerListView
    {
        // A ping under each of these lights that many bars, five under the first.
        private static readonly int[] BarLimits = { 150, 300, 600, 1000 };
        private const int BarCount = 5;

        private readonly VisualElement panel;
        private readonly VisualElement rows;
        private readonly Label count;
        private readonly List<PlayerListSync.Entry> shown = new List<PlayerListSync.Entry>();
        private ulong shownLocalId = ulong.MaxValue;
        private string shownLanguage;

        public PlayerListView(VisualElement panel)
        {
            this.panel = panel;
            rows = panel.Q("player-rows");
            count = panel.Q<Label>("player-count");
        }

        public void Show(bool visible, IReadOnlyList<PlayerListSync.Entry> players, ulong localClientId)
        {
            panel.EnableInClassList("hidden", !visible);
            if (!visible || IsShown(players, localClientId)) return;

            shown.Clear();
            for (int i = 0; i < players.Count; i++) shown.Add(players[i]);
            shownLocalId = localClientId;
            shownLanguage = Loc.Language;

            count.text = Loc.T("{0} online", players.Count);
            rows.Clear();
            foreach (PlayerListSync.Entry player in shown)
            {
                rows.Add(Row(player, player.ClientId == localClientId));
            }
        }

        private bool IsShown(IReadOnlyList<PlayerListSync.Entry> players, ulong localClientId)
        {
            if (players.Count != shown.Count || localClientId != shownLocalId || Loc.Language != shownLanguage) return false;

            for (int i = 0; i < players.Count; i++)
            {
                PlayerListSync.Entry a = players[i], b = shown[i];
                if (a.ClientId != b.ClientId || a.Name != b.Name || a.Ping != b.Ping || a.IsHost != b.IsHost) return false;
            }

            return true;
        }

        private static VisualElement Row(PlayerListSync.Entry player, bool isYou)
        {
            var row = new VisualElement { pickingMode = PickingMode.Ignore };
            row.AddToClassList("player-row");
            row.EnableInClassList("player-row--you", isYou);

            bool named = !string.IsNullOrEmpty(player.Name);
            var name = new Label(named ? player.Name : Loc.T("(choosing a name)")) { pickingMode = PickingMode.Ignore };
            name.AddToClassList("player-name");
            name.EnableInClassList("player-name--unnamed", !named);
            row.Add(name);

            var ping = new Label(player.IsHost ? Loc.T("Host") : Loc.T("{0} ms", player.Ping)) { pickingMode = PickingMode.Ignore };
            ping.AddToClassList("player-ping");
            row.Add(ping);

            int lit = Bars(player);
            var bars = new VisualElement { pickingMode = PickingMode.Ignore };
            bars.AddToClassList("ping-bars");
            bars.AddToClassList(lit >= 4 ? "ping-bars--good" : lit >= 2 ? "ping-bars--fair" : "ping-bars--poor");
            for (int b = 0; b < BarCount; b++)
            {
                var bar = new VisualElement { pickingMode = PickingMode.Ignore };
                bar.AddToClassList("ping-bar");
                bar.EnableInClassList("ping-bar--lit", b < lit);
                bar.style.height = Length.Percent(20f * (b + 1));
                bars.Add(bar);
            }

            row.Add(bars);
            return row;
        }

        // How many of the five bars a player's ping lights: all for the host's own player.
        private static int Bars(PlayerListSync.Entry player)
        {
            if (player.IsHost) return BarCount;

            int lit = BarCount;
            foreach (int limit in BarLimits)
            {
                if (player.Ping < limit) return lit;
                lit--;
            }

            return lit;
        }
    }
}

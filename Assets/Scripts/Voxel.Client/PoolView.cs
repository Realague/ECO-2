using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using Voxel.Core;

namespace Voxel.Client
{
    // The storage side of the storage window (Storage.uxml: store-grid, member-list, view-toggle): what
    // a pool of chests or of stockpiles holds (or the chests linked to a campfire), either merged, one
    // grid of everything added up, or each storage on its own card (its name, how full it is, its items
    // or its piles), as the player chooses with the toggle (kept for the session; shown when there is
    // more than one); or a single inventory (the wood by a campfire). Presses are passed on with what
    // was pressed: an item, or a pile numbered across the pool, the storage it is in (0 for the whole
    // pool) and whether it is a stockpile's item from every pile of it.
    internal sealed class PoolView
    {
        // Merged (false) or each storage on its own (true), for the session.
        private static bool split;

        private readonly StorageSync storageSync;
        private readonly EcosystemSync ecosystemSync;
        private readonly ScrollView mergedView;
        private readonly ScrollView memberList;
        private readonly VisualElement toggle;
        private readonly Button mergedButton;
        private readonly Button splitButton;
        private readonly ItemGrid merged;
        private readonly int minimumRows;
        private readonly List<Card> cards = new List<Card>();
        private readonly Inventory totals = new Inventory();
        private string layoutKey;
        private int hoveredPile = -1;

        // What a press on the merged grid means: an item of the pool, or of every pile of it.
        private bool mergedByItem;

        private sealed class Card
        {
            public int Id;
            public VisualElement Root;
            public Label Fill;
            public ItemGrid Grid;
            public int FirstPile;
            public readonly List<VisualElement> Piles = new List<VisualElement>();
            public readonly List<VisualElement> PileIcons = new List<VisualElement>();
            public readonly List<Label> PileCounts = new List<Label>();
        }

        public event Action<int, int, bool, VisualElement, PointerDownEvent> Pressed;
        public event Action<PointerMoveEvent> Moved;
        public event Action<PointerUpEvent> Released;

        public PoolView(VisualElement root, StorageSync storageSync, EcosystemSync ecosystemSync, int minimumRows)
        {
            this.storageSync = storageSync;
            this.ecosystemSync = ecosystemSync;
            mergedView = root.Q<ScrollView>("store-grid");
            memberList = root.Q<ScrollView>("member-list");
            toggle = root.Q("view-toggle");
            mergedButton = root.Q<Button>("view-merged");
            splitButton = root.Q<Button>("view-split");
            memberList.verticalScrollerVisibility = ScrollerVisibility.AlwaysVisible;
            this.minimumRows = minimumRows;
            merged = new ItemGrid(mergedView, ecosystemSync, minimumRows);
            merged.Pressed += (item, slot, e) => Pressed?.Invoke(item, 0, mergedByItem, slot, e);
            merged.Moved += e => Moved?.Invoke(e);
            merged.Released += e => Released?.Invoke(e);
            mergedButton.focusable = false;
            splitButton.focusable = false;
            mergedButton.clicked += () => split = false;
            splitButton.clicked += () => split = true;
        }

        // The item under the pointer, if any.
        public int HoveredItem
        {
            get
            {
                if (mergedView.style.display != DisplayStyle.None) return merged.HoveredItem;
                if (hoveredPile >= 0) return storageSync.OpenedPileItem(hoveredPile);

                foreach (Card card in cards)
                {
                    if (card.Grid != null && card.Grid.HoveredItem != Items.None) return card.Grid.HoveredItem;
                }

                return Items.None;
            }
        }

        public void Invalidate()
        {
            merged.Invalidate();
            layoutKey = null;
        }

        // The storage whose card is under a point (where something is dropped), else 0: the whole pool.
        public int MemberAt(Vector2 position)
        {
            if (memberList.style.display == DisplayStyle.None) return 0;

            foreach (Card card in cards)
            {
                if (card.Root.worldBound.Contains(position)) return card.Id;
            }

            return 0;
        }

        // Chests: a pool's, or those linked to a campfire (openedId 0: none of them is the one open).
        public void ShowChests(IReadOnlyList<StorageMember> members, Inventory total, int openedId)
        {
            bool cardsShown = split && members.Count > 1;
            ShowToggle(members.Count > 1);
            Show(cardsShown);
            if (!cardsShown)
            {
                mergedByItem = false;
                merged.MinimumRows = minimumRows;
                merged.Show(total.Entries, _ => false);
                return;
            }

            if (KeyOf("chests", members) != layoutKey) BuildCards(members, openedId, "chests");

            for (int i = 0; i < cards.Count; i++)
            {
                StorageMember member = members[i];
                float weight = StorageRules.ChestWeight(member.Contents, ecosystemSync.ItemWeights);
                cards[i].Grid.Show(member.Contents.Entries, _ => false);
                cards[i].Fill.text = $"{Loc.Number(weight, "0.0")} / {StorageRules.ChestMaxWeight:0} kg";
                SetShare(cards[i].Fill, weight / StorageRules.ChestMaxWeight);
            }
        }

        // A pool of stockpiles: merged (each kind of block or wood added up, taken from every pile of it)
        // or each stockpile's piles, as they stand (always so for a stockpile alone).
        public void ShowStockpiles(IReadOnlyList<StorageMember> members, int openedId)
        {
            bool cardsShown = split || members.Count == 1;
            ShowToggle(members.Count > 1);
            Show(cardsShown);
            if (!cardsShown)
            {
                totals.Clear();
                foreach (StorageMember member in members)
                {
                    for (int pile = 0; pile < member.Piles.Count; pile++)
                    {
                        if (member.Piles.CountAt(pile) > 0) totals.Add(member.Piles.ItemAt(pile), member.Piles.CountAt(pile));
                    }
                }

                mergedByItem = true;
                merged.MinimumRows = minimumRows;
                merged.Show(totals.Entries, _ => false);
                return;
            }

            if (KeyOf("stockpiles", members) != layoutKey) BuildCards(members, openedId, "stockpiles");

            for (int i = 0; i < cards.Count; i++)
            {
                Card card = cards[i];
                StockpilePiles piles = members[i].Piles;
                for (int pile = 0; pile < card.Piles.Count; pile++)
                {
                    int item = piles.ItemAt(pile);
                    int count = piles.CountAt(pile);
                    card.PileIcons[pile].style.backgroundImage = count > 0 ? new StyleBackground(ItemCatalog.IconOf(item, ecosystemSync)) : new StyleBackground();
                    card.PileCounts[pile].text = count > 0 ? $"{count} / {piles.PileSize}" : "";
                    card.Piles[pile].EnableInClassList("item-slot--empty", count == 0);
                }

                card.Fill.text = $"{piles.Total} / {piles.Capacity}";
                SetShare(card.Fill, (float)piles.Total / Mathf.Max(1, piles.Capacity));
            }
        }

        // One inventory, in a grid (the wood by a campfire).
        public void ShowSingle(Inventory contents)
        {
            ShowToggle(false);
            Show(false);
            mergedByItem = false;
            merged.MinimumRows = 1;
            merged.Show(contents.Entries, _ => false);
        }

        private void ShowToggle(bool shown)
        {
            toggle.style.display = shown ? DisplayStyle.Flex : DisplayStyle.None;
            mergedButton.EnableInClassList("view-button--selected", !split);
            splitButton.EnableInClassList("view-button--selected", split);
        }

        private void Show(bool cardsShown)
        {
            mergedView.style.display = cardsShown ? DisplayStyle.None : DisplayStyle.Flex;
            memberList.style.display = cardsShown ? DisplayStyle.Flex : DisplayStyle.None;
            if (!cardsShown) hoveredPile = -1;
        }

        private static string KeyOf(string kind, IReadOnlyList<StorageMember> members)
        {
            var key = new System.Text.StringBuilder(kind);
            foreach (StorageMember member in members)
            {
                key.Append(',').Append(member.Id).Append(':').Append(member.Piles?.Count ?? 0);
            }

            return key.ToString();
        }

        // A card per storage, in the pool's order; a stockpile's piles numbered across the pool.
        private void BuildCards(IReadOnlyList<StorageMember> members, int openedId, string kind)
        {
            layoutKey = KeyOf(kind, members);
            memberList.Clear();
            cards.Clear();
            hoveredPile = -1;
            int firstPile = 0;
            for (int i = 0; i < members.Count; i++)
            {
                StorageMember member = members[i];
                var card = new Card { Id = member.Id, Root = new VisualElement(), FirstPile = firstPile };
                card.Root.AddToClassList("pool-card");
                card.Root.EnableInClassList("pool-card--open", member.Id == openedId);

                var head = new VisualElement { pickingMode = PickingMode.Ignore };
                head.AddToClassList("pool-card-head");
                var icon = new VisualElement { pickingMode = PickingMode.Ignore };
                icon.AddToClassList("pool-card-icon");
                icon.style.backgroundImage = new StyleBackground(ItemCatalog.IconOf(StorageRules.ItemOf(member.Kind), ecosystemSync));
                var title = new Label(TitleOf(member, i, openedId)) { pickingMode = PickingMode.Ignore };
                title.AddToClassList("pool-card-title");
                card.Fill = new Label { pickingMode = PickingMode.Ignore };
                card.Fill.AddToClassList("carry-count-text");
                card.Fill.AddToClassList("pool-card-fill");
                head.Add(icon);
                head.Add(title);
                head.Add(card.Fill);
                card.Root.Add(head);
                if (storageSync.TryPositionOf(member.Id, out BlockPos position)) card.Root.tooltip = Loc.T("At {0}, {1}, {2}", position.X, position.Y, position.Z);

                if (member.Piles == null)
                {
                    var grid = new VisualElement();
                    grid.AddToClassList("pool-card-grid");
                    card.Root.Add(grid);
                    card.Grid = new ItemGrid(grid, ecosystemSync, 1);
                    int id = member.Id;
                    card.Grid.Pressed += (item, slot, e) => Pressed?.Invoke(item, id, false, slot, e);
                    card.Grid.Moved += e => Moved?.Invoke(e);
                    card.Grid.Released += e => Released?.Invoke(e);
                }
                else
                {
                    card.Root.Add(PilesOf(card, member.Piles.Count));
                    firstPile += member.Piles.Count;
                }

                memberList.Add(card.Root);
                cards.Add(card);
            }
        }

        private static string TitleOf(StorageMember member, int index, int openedId)
        {
            bool chest = member.Kind == StorageKind.Chest;
            if (member.Id == openedId) return Loc.T(chest ? "This chest" : "This stockpile");

            return Loc.T(chest ? "Chest {0}" : "Stockpile {0}", index + 1);
        }

        // A stockpile's piles, a row per z and a pile per x, as they stand on the ground.
        private VisualElement PilesOf(Card card, int count)
        {
            var block = new VisualElement { pickingMode = PickingMode.Ignore };
            block.AddToClassList("pile-block");
            int side = Mathf.RoundToInt(Mathf.Sqrt(count));
            for (int i = 0; i < count; i++)
            {
                if (i % side == 0)
                {
                    var row = new VisualElement { pickingMode = PickingMode.Ignore };
                    row.AddToClassList("pile-row");
                    block.Add(row);
                }

                block[block.childCount - 1].Add(AddPile(card, card.FirstPile + i));
            }

            return block;
        }

        private VisualElement AddPile(Card card, int index)
        {
            var slot = new VisualElement();
            slot.AddToClassList("item-slot");
            slot.AddToClassList("pile-slot");
            var icon = new VisualElement { pickingMode = PickingMode.Ignore };
            icon.AddToClassList("item-icon");
            var count = new Label { pickingMode = PickingMode.Ignore };
            count.AddToClassList("item-count");
            count.AddToClassList("pile-count");
            slot.Add(icon);
            slot.Add(count);
            slot.RegisterCallback<PointerEnterEvent>(_ => hoveredPile = index);
            slot.RegisterCallback<PointerLeaveEvent>(_ =>
            {
                if (hoveredPile == index) hoveredPile = -1;
            });
            slot.RegisterCallback<PointerDownEvent>(e =>
            {
                if (storageSync.OpenedPileCountAt(index) > 0) Pressed?.Invoke(index, 0, false, slot, e);
            });
            slot.RegisterCallback<PointerMoveEvent>(e => Moved?.Invoke(e));
            slot.RegisterCallback<PointerUpEvent>(e => Released?.Invoke(e));

            card.Piles.Add(slot);
            card.PileIcons.Add(icon);
            card.PileCounts.Add(count);
            return slot;
        }

        private static void SetShare(Label label, float share)
        {
            bool full = share >= 0.9999f;
            label.EnableInClassList("carry-count-text--high", share >= CarryHud.HighShare && !full);
            label.EnableInClassList("carry-count-text--full", full);
        }
    }
}

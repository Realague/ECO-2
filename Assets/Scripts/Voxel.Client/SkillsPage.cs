using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.UIElements;
using Voxel.Core;

namespace Voxel.Client
{
    // The inventory's Skills tab (Inventory.uxml: skills-page, skill-sheet), run by InventoryScreen,
    // laid out after the user's mockup. On the left the skills (those unlocked; every one, greyed with
    // what unlocks it, when the server shows them all), a search by name or tag, and the points to
    // spend. In the middle the chosen skill: its icon, name and description over its tier in progress,
    // then a band per tier ("TIER 1 · its name", its experience bar in quarters), each holding its
    // upgrades as parchment cards (icon, name, rank and a pip per step with its value; a one-step
    // upgrade shows its value large, gold once bought) or, for an upgrade with options, a framed group
    // with a card per option; gold lines join each level of an upgrade to the next one, which replaces
    // it. A tier shows once the one before it is open, or always when the server says so. On the right,
    // a dark sheet: the chosen upgrade's bonus now, its level, next level, cost and requirements, its
    // options, its technical details when the server shows them, and Upgrade.
    internal sealed class SkillsPage
    {
        private static readonly Color LinkColour = new Color(0.85f, 0.68f, 0.31f, 0.85f);

        private readonly SkillSync skillSync;

        private readonly VisualElement page;
        private readonly ScrollView skillList;
        private readonly TextField search;
        private readonly VisualElement searchBox;
        private readonly VisualElement searchHint;
        private readonly Label summaryPoints;
        private readonly Label summaryXp;
        private readonly VisualElement headerIcon;
        private readonly Label title;
        private readonly Label description;
        private readonly Label points;
        private readonly Label headerTier;
        private readonly VisualElement headerBar;
        private readonly Label headerXp;
        private readonly ScrollView tiers;
        private readonly Label empty;
        private readonly VisualElement sheet;
        private readonly VisualElement sheetIcon;
        private readonly Label sheetName;
        private readonly Label sheetKind;
        private readonly Label sheetDescription;
        private readonly VisualElement sheetEffects;
        private readonly Label sheetTechnical;
        private readonly VisualElement sheetChoices;
        private readonly Label sheetReason;
        private readonly Button buy;
        private readonly Label buyText;

        // The cards of the skill drawn, by upgrade id, and the lines from a level to the next.
        private readonly Dictionary<string, VisualElement> cards = new Dictionary<string, VisualElement>();
        private readonly List<(VisualElement from, VisualElement to)> links = new List<(VisualElement, VisualElement)>();
        private readonly List<VisualElement> bands = new List<VisualElement>();
        private readonly Dictionary<VisualElement, VisualElement> bandOf = new Dictionary<VisualElement, VisualElement>();
        private VisualElement linkLayer;

        // The space under a tier's cards, where the links turn (Inventory.uss, .skill-tier-cards' bottom margin).
        private const float BandGap = 22f;

        private string selectedSkill;
        private string selectedUpgrade;
        private int selectedChoice = -1;
        private string shownKey;
        private string drawnSkill;

        public SkillsPage(VisualElement root, SkillSync skillSync)
        {
            this.skillSync = skillSync;
            page = root.Q("skills-page");
            skillList = root.Q<ScrollView>("skill-list");
            search = root.Q<TextField>("skill-search");
            searchBox = root.Q("skill-search-box");
            searchHint = root.Q("skill-search-hint");
            summaryPoints = root.Q<Label>("skill-summary-points");
            summaryXp = root.Q<Label>("skill-summary-xp");
            headerIcon = root.Q("skill-header-icon");
            title = root.Q<Label>("skill-title");
            description = root.Q<Label>("skill-description");
            points = root.Q<Label>("skill-page-points");
            headerTier = root.Q<Label>("skill-header-tier");
            headerBar = root.Q("skill-header-bar-fill");
            headerXp = root.Q<Label>("skill-header-xp");
            tiers = root.Q<ScrollView>("skill-tiers");
            empty = root.Q<Label>("skill-empty");
            sheet = root.Q("skill-sheet");
            sheetIcon = root.Q("skill-sheet-icon");
            sheetName = root.Q<Label>("skill-sheet-name");
            sheetKind = root.Q<Label>("skill-sheet-kind");
            sheetDescription = root.Q<Label>("skill-sheet-description");
            sheetEffects = root.Q("skill-sheet-effects");
            sheetTechnical = root.Q<Label>("skill-sheet-technical");
            sheetChoices = root.Q("skill-sheet-choices");
            sheetReason = root.Q<Label>("skill-sheet-reason");
            buy = root.Q<Button>("skill-sheet-buy");
            buyText = root.Q<Label>("skill-sheet-buy-text");
            buy.focusable = false;
            buy.clicked += () =>
            {
                if (selectedSkill != null && selectedUpgrade != null) skillSync.RequestBuy(selectedSkill, selectedUpgrade, selectedChoice);
            };

            tiers.verticalScrollerVisibility = ScrollerVisibility.AlwaysVisible;

            // The lines follow the cards wherever the layout puts them.
            tiers.contentContainer.RegisterCallback<GeometryChangedEvent>(_ => linkLayer?.MarkDirtyRepaint());
            search.RegisterValueChangedCallback(e =>
            {
                shownKey = null;
                searchHint.style.display = string.IsNullOrEmpty(e.newValue) ? DisplayStyle.Flex : DisplayStyle.None;
            });
            search.RegisterCallback<FocusInEvent>(_ => searchBox.AddToClassList("item-search-box--focused"));
            search.RegisterCallback<FocusOutEvent>(_ => searchBox.RemoveFromClassList("item-search-box--focused"));
            Show(false);
        }

        public bool IsTyping => search.focusController?.focusedElement == search;

        public void Blur() => search.Blur();

        public void Show(bool shown)
        {
            page.style.display = shown ? DisplayStyle.Flex : DisplayStyle.None;
            sheet.style.display = shown ? DisplayStyle.Flex : DisplayStyle.None;
            shownKey = null;
        }

        public void Retranslate() => shownKey = null;

        // The skill points the local player has to spend, all skills together (the tab bar shows them).
        public int PointsToSpend()
        {
            int total = 0;
            SkillState state = skillSync.LocalState;
            foreach (SkillDefinition skill in skillSync.Catalog.Skills)
            {
                if (state.TryGet(skill.Id, out SkillProgress progress) && progress.Unlocked) total += Mathf.Max(0, SkillRules.PointsLeft(skill, progress));
            }

            return total;
        }

        // Every frame while the tab is shown: drawn again when the skills, the options, the choice, the
        // search or the language changed.
        public void Refresh()
        {
            SkillState state = skillSync.LocalState;
            string key = $"{state.Version}|{skillSync.Options.ToByte()}|{selectedSkill}|{selectedUpgrade}|{selectedChoice}|{search.value}|{Loc.Language}";
            if (key == shownKey) return;

            shownKey = key;
            List<SkillDefinition> shown = ShownSkills(state);
            if (selectedSkill == null || !shown.Exists(skill => skill.Id == selectedSkill))
            {
                selectedSkill = shown.Count > 0 ? shown[0].Id : null;
                selectedUpgrade = null;
                selectedChoice = -1;
            }

            DrawList(shown, state);
            DrawSkill(state);
            DrawSheet(state);
        }

        // The skills listed: unlocked ones (all when the server shows them all), matching the search.
        private List<SkillDefinition> ShownSkills(SkillState state)
        {
            var shown = new List<SkillDefinition>();
            string wanted = Command.Normalise(search.value ?? "");
            foreach (SkillDefinition skill in skillSync.Catalog.Skills)
            {
                bool unlocked = state.TryGet(skill.Id, out SkillProgress progress) && progress.Unlocked;
                if (!unlocked && !skillSync.Options.ShowAllSkills) continue;
                if (wanted.Length > 0 && !Matches(skill, wanted)) continue;

                shown.Add(skill);
            }

            return shown;
        }

        private static bool Matches(SkillDefinition skill, string wanted)
        {
            if (Command.Normalise(Loc.Name(skill.Name)).Contains(wanted) || Command.Normalise(skill.Name).Contains(wanted)) return true;

            foreach (UpgradeDefinition upgrade in skill.Upgrades)
            {
                if (upgrade.Hidden) continue;
                if (Command.Normalise(Loc.T(upgrade.Name)).Contains(wanted)) return true;
                foreach (string tag in upgrade.Tags)
                {
                    if (Command.Normalise(Loc.T(tag)).Contains(wanted) || Command.Normalise(tag).Contains(wanted)) return true;
                }
            }

            return false;
        }

        private void DrawList(List<SkillDefinition> shown, SkillState state)
        {
            skillList.Clear();
            foreach (SkillDefinition skill in shown)
            {
                state.TryGet(skill.Id, out SkillProgress progress);
                bool unlocked = progress != null && progress.Unlocked;
                var button = new Button { focusable = false };
                button.AddToClassList("category");
                button.AddToClassList("skill-row");
                button.EnableInClassList("category--selected", skill.Id == selectedSkill);
                button.EnableInClassList("skill-row--locked", !unlocked);

                var icon = new VisualElement();
                icon.AddToClassList("skill-row-icon");
                SetIcon(icon, SkillIcon(skill.Id));
                button.Add(icon);

                var text = new VisualElement();
                text.AddToClassList("skill-row-text");
                var name = new Label(Loc.Name(skill.Name));
                name.AddToClassList("category-name");
                text.Add(name);
                var status = new Label(unlocked ? Loc.T("Tier {0}", SkillRules.OpenTiers(skill, progress)) : Loc.T("Locked"));
                status.AddToClassList("skill-row-status");
                text.Add(status);
                button.Add(text);

                int left = unlocked ? SkillRules.PointsLeft(skill, progress) : 0;
                if (left > 0)
                {
                    var badge = new Label(left.ToString());
                    badge.AddToClassList("skill-row-points");
                    button.Add(badge);
                }

                string id = skill.Id;
                button.clicked += () =>
                {
                    selectedSkill = id;
                    selectedUpgrade = null;
                    selectedChoice = -1;
                };
                skillList.Add(button);
            }

            summaryPoints.text = PointsToSpend().ToString();
        }

        private void DrawSkill(SkillState state)
        {
            // Drawn again for the same skill (a purchase, more experience), it stays scrolled where it was.
            if (selectedSkill == drawnSkill)
            {
                Vector2 kept = tiers.scrollOffset;
                tiers.schedule.Execute(() => tiers.scrollOffset = kept);
            }

            drawnSkill = selectedSkill;
            tiers.Clear();
            cards.Clear();
            links.Clear();
            bands.Clear();
            bandOf.Clear();
            linkLayer = null;
            if (selectedSkill == null || !skillSync.Catalog.TryGet(selectedSkill, out SkillDefinition skill))
            {
                SetIcon(headerIcon, null);
                title.text = Loc.T("Skills").ToUpper(Loc.Culture);
                description.text = "";
                points.text = "0";
                headerTier.text = "";
                headerXp.text = "";
                headerBar.style.width = Length.Percent(0);
                summaryXp.text = "";
                empty.text = Loc.T("No skill yet: skills unlock as you work (fell a tree with an axe…).");
                empty.style.display = DisplayStyle.Flex;
                return;
            }

            empty.style.display = DisplayStyle.None;
            state.TryGet(skill.Id, out SkillProgress progress);
            bool unlocked = progress != null && progress.Unlocked;
            int open = SkillRules.OpenTiers(skill, progress);
            DrawHeader(skill, progress, unlocked, open);

            if (!unlocked)
            {
                var condition = new Label(UnlockText(skill));
                condition.AddToClassList("skill-condition");
                tiers.Add(condition);
            }

            // A tier shows once the one before it is open; all of them when the server says so.
            int shownTiers = skillSync.Options.ShowAllTiers ? skill.Tiers.Length : Mathf.Max(1, open);
            int lastTier = Mathf.Min(shownTiers, skill.Tiers.Length);
            var tree = new VisualElement();
            tree.AddToClassList("skill-tree");
            for (int t = 1; t <= lastTier; t++) tree.Add(Band(skill, progress, t, open, t == lastTier));
            tiers.Add(tree);

            // Each level joined to the one that replaces it, when both are shown.
            foreach (UpgradeDefinition upgrade in skill.Upgrades)
            {
                if (string.IsNullOrEmpty(upgrade.Replaces)) continue;
                if (cards.TryGetValue(upgrade.Replaces, out VisualElement from) && cards.TryGetValue(upgrade.Id, out VisualElement to)) links.Add((from, to));
            }

            linkLayer = new VisualElement { pickingMode = PickingMode.Ignore };
            linkLayer.AddToClassList("skill-links");
            linkLayer.generateVisualContent += DrawLinks;
            tiers.Add(linkLayer);
        }

        // The skill's icon, name and description, its open tier and the experience in it.
        private void DrawHeader(SkillDefinition skill, SkillProgress progress, bool unlocked, int open)
        {
            SetIcon(headerIcon, SkillIcon(skill.Id));
            title.text = Loc.Name(skill.Name).ToUpper(Loc.Culture);
            description.text = string.IsNullOrEmpty(skill.Description) ? "" : Loc.T(skill.Description);
            summaryXp.text = Loc.T("{0} kcal", Loc.Number(progress != null ? progress.Xp : 0f, "0"));
            if (!unlocked)
            {
                points.text = Loc.T("Locked");
                headerTier.text = "";
                headerXp.text = "";
                headerBar.style.width = Length.Percent(0);
                return;
            }

            points.text = Loc.T("{0} points", SkillRules.PointsLeft(skill, progress));
            TierDefinition tier = skill.Tiers[open - 1];
            headerTier.text = string.IsNullOrEmpty(tier.Name) ? Loc.T("Tier {0}", open) : Loc.T("Tier {0} · {1}", open, Loc.T(tier.Name));
            headerBar.style.width = Length.Percent(SkillRules.TierProgress(skill, progress, open) * 100f);
            float xp = Mathf.Clamp(progress.Xp - skill.TierStart(open), 0f, tier.XpQuota);
            headerXp.text = Loc.T("{0} / {1} kcal", Loc.Number(xp, "0"), Loc.Number(tier.XpQuota, "0"));
        }

        // A tier: a row of its upgrades beside the rail, where its number sits on a disc and a bar
        // down to the next tier's disc fills with the experience in it (ticked at each quarter, each
        // giving a share of the tier's points). Its name, description and experience are the rail's
        // tooltip.
        private VisualElement Band(SkillDefinition skill, SkillProgress progress, int tier, int open, bool last)
        {
            TierDefinition definition = skill.Tiers[tier - 1];
            float share = SkillRules.TierProgress(skill, progress, tier);
            var row = new VisualElement();
            row.AddToClassList("skill-tier");
            row.EnableInClassList("skill-tier--closed", tier > open);
            row.EnableInClassList("skill-tier--done", tier <= open && share >= 1f);
            row.EnableInClassList("skill-tier--current", tier == open && share < 1f);

            var rail = new VisualElement();
            rail.AddToClassList("skill-rail");
            float xp = progress != null ? Mathf.Clamp(progress.Xp - skill.TierStart(tier), 0f, definition.XpQuota) : 0f;
            string tip = string.IsNullOrEmpty(definition.Name) ? Loc.T("Tier {0}", tier) : Loc.T("Tier {0} · {1}", tier, Loc.T(definition.Name));
            tip += "\n" + Loc.T("{0} / {1} kcal", Loc.Number(xp, "0"), Loc.Number(definition.XpQuota, "0"));
            if (!string.IsNullOrEmpty(definition.Description)) tip += "\n" + Loc.T(definition.Description);
            rail.tooltip = tip;

            var bar = new VisualElement();
            bar.AddToClassList("skill-rail-bar");
            bar.EnableInClassList("skill-rail-bar--last", last);
            var fill = new VisualElement();
            fill.AddToClassList("skill-rail-fill");
            fill.style.height = Length.Percent(share * 100f);
            bar.Add(fill);
            for (int q = 1; q < SkillDefinition.QuartersPerTier; q++)
            {
                var tick = new VisualElement();
                tick.AddToClassList("skill-rail-tick");
                tick.style.top = Length.Percent(100f * q / SkillDefinition.QuartersPerTier);
                bar.Add(tick);
            }

            rail.Add(bar);
            var number = new Label(tier.ToString(Loc.Culture));
            number.AddToClassList("skill-rail-number");
            rail.Add(number);
            row.Add(rail);

            var cardsRow = new VisualElement();
            cardsRow.AddToClassList("skill-tier-cards");
            foreach (UpgradeDefinition upgrade in definition.Upgrades)
            {
                if (upgrade.Hidden) continue;

                VisualElement card = upgrade.HasChoices ? Group(skill, progress, upgrade, open) : Card(skill, progress, upgrade, open);
                bandOf[card] = cardsRow;
                cardsRow.Add(card);
            }

            row.Add(cardsRow);
            bands.Add(cardsRow);
            return row;
        }

        // An upgrade in steps (or a single one): icon, name, rank and a pip per step with its value; a
        // one-step upgrade with a value shows it large instead, gold once bought.
        private VisualElement Card(SkillDefinition skill, SkillProgress progress, UpgradeDefinition upgrade, int open)
        {
            UpgradeProgress bought = progress != null ? progress.Of(upgrade.Id) : default;
            bool complete = SkillRules.IsComplete(upgrade, bought);
            SkillRefusal refusal = SkillRules.CanBuy(skill, progress, upgrade.Id);
            int count = upgrade.Steps.Length;
            int done = Mathf.Min(bought.Steps, count);
            string single = count == 1 ? ValueOf(upgrade, 0, true) : null;

            var card = NewCard(UpgradeIcon(skill.Id, upgrade.Id), Loc.T(upgrade.Name), out VisualElement body, out VisualElement foot);
            card.EnableInClassList("skill-card--selected", upgrade.Id == selectedUpgrade);
            card.EnableInClassList("skill-card--available", refusal == SkillRefusal.None);
            card.EnableInClassList("skill-card--locked", !complete && refusal != SkillRefusal.None && refusal != SkillRefusal.NotEnoughPoints);
            card.EnableInClassList("skill-card--gold", single != null && complete);

            var rank = new Label(Loc.T("Rank {0}/{1}", done, count));
            rank.AddToClassList("skill-card-rank");
            body.Add(rank);
            if (single != null)
            {
                var value = new Label(single);
                value.AddToClassList("skill-card-value");
                foot.Add(value);
            }
            else
            {
                var pips = new VisualElement();
                pips.AddToClassList("skill-pips");
                for (int s = 0; s < count; s++)
                {
                    bool closed = upgrade.TierOf(s) > open;
                    pips.Add(Pip(s < done, closed && s >= done, ValueOf(upgrade, s, false)));
                }

                foot.Add(pips);
            }

            card.tooltip = Loc.T("{0} pt", upgrade.Cost);
            string id = upgrade.Id;
            card.clicked += () =>
            {
                selectedUpgrade = id;
                selectedChoice = -1;
            };
            cards[upgrade.Id] = card;
            return card;
        }

        // An upgrade with options: a framed group, its name and how many may be chosen, a card per option.
        private VisualElement Group(SkillDefinition skill, SkillProgress progress, UpgradeDefinition upgrade, int open)
        {
            UpgradeProgress bought = progress != null ? progress.Of(upgrade.Id) : default;
            var group = new VisualElement();
            group.AddToClassList("skill-group");
            group.EnableInClassList("skill-group--selected", upgrade.Id == selectedUpgrade);

            var head = new VisualElement();
            head.AddToClassList("skill-group-head");
            var name = new Label(Loc.T(upgrade.Name).ToUpper(Loc.Culture));
            name.AddToClassList("skill-group-title");
            head.Add(name);
            var limit = new Label(Loc.T("{0} choices at most · {1} pt each", upgrade.ChoiceLimit, upgrade.Cost));
            limit.AddToClassList("skill-group-limit");
            head.Add(limit);
            if (!string.IsNullOrEmpty(upgrade.Description)) head.tooltip = Loc.T(upgrade.Description);
            group.Add(head);

            var row = new VisualElement();
            row.AddToClassList("skill-group-row");
            SkillAsset.Upgrade asset = skillSync.AssetOf(skill.Id)?.FindUpgrade(upgrade.Id);
            for (int c = 0; c < upgrade.Choices.Length; c++)
            {
                UpgradeChoice choice = upgrade.Choices[c];
                bool taken = bought.HasChoice(c);
                SkillRefusal refusal = SkillRules.CanBuy(skill, progress, upgrade.Id, c);
                Texture2D icon = asset != null && c < asset.choices.Length && asset.choices[c].icon != null ? asset.choices[c].icon : UpgradeIcon(skill.Id, upgrade.Id);

                var card = NewCard(icon, Loc.T(choice.Name), out VisualElement body, out VisualElement foot);
                card.AddToClassList("skill-card--choice");
                card.EnableInClassList("skill-card--selected", upgrade.Id == selectedUpgrade && selectedChoice == c);
                card.EnableInClassList("skill-card--available", refusal == SkillRefusal.None);
                card.EnableInClassList("skill-card--locked", !taken && refusal != SkillRefusal.None && refusal != SkillRefusal.NotEnoughPoints);
                card.EnableInClassList("skill-card--gold", taken);

                var pips = new VisualElement();
                pips.AddToClassList("skill-pips");
                pips.Add(Pip(taken, !taken && upgrade.Tier > open, null));
                foot.Add(pips);
                var state = new Label(taken ? Loc.T("Chosen") : Loc.T("{0} pt", upgrade.Cost));
                state.AddToClassList("skill-card-rank");
                body.Add(state);

                if (!string.IsNullOrEmpty(choice.Description)) card.tooltip = Loc.T(choice.Description);
                string id = upgrade.Id;
                int index = c;
                card.clicked += () =>
                {
                    selectedUpgrade = id;
                    selectedChoice = index;
                };
                row.Add(card);
            }

            group.Add(row);
            cards[upgrade.Id] = group;
            return group;
        }

        // A card: the icon in its frame beside the name (body, where the rank goes), then the foot
        // across the card (the pips, or the value).
        private static Button NewCard(Texture2D icon, string name, out VisualElement body, out VisualElement foot)
        {
            var card = new Button { focusable = false };
            card.AddToClassList("skill-card");

            var top = new VisualElement();
            top.AddToClassList("skill-card-top");
            var frame = new VisualElement();
            frame.AddToClassList("skill-card-icon-frame");
            var image = new VisualElement();
            image.AddToClassList("skill-card-icon");
            SetIcon(image, icon);
            frame.Add(image);
            top.Add(frame);

            body = new VisualElement();
            body.AddToClassList("skill-card-body");
            var label = new Label(name);
            label.AddToClassList("skill-card-name");
            body.Add(label);
            top.Add(body);
            card.Add(top);

            foot = new VisualElement();
            foot.AddToClassList("skill-card-foot");
            card.Add(foot);
            return card;
        }

        // A step: bought (filled), open (ringed) or waiting for its tier (grey), its value under it.
        private static VisualElement Pip(bool bought, bool closed, string value)
        {
            var column = new VisualElement();
            column.AddToClassList("skill-pip-column");
            var pip = new VisualElement();
            pip.AddToClassList("skill-pip");
            pip.EnableInClassList("skill-pip--bought", bought);
            pip.EnableInClassList("skill-pip--closed", closed);
            column.Add(pip);
            if (!string.IsNullOrEmpty(value))
            {
                var label = new Label(value);
                label.AddToClassList("skill-pip-label");
                column.Add(label);
            }

            return column;
        }

        // The gold lines from the bottom of a level's card to the top of the next level's.
        private void DrawLinks(MeshGenerationContext context)
        {
            if (linkLayer == null || links.Count == 0) return;

            Painter2D painter = context.painter2D;
            painter.strokeColor = LinkColour;
            painter.fillColor = LinkColour;
            painter.lineWidth = 2f;
            foreach ((VisualElement from, VisualElement to) in links)
            {
                Rect a = linkLayer.WorldToLocal(from.worldBound);
                Rect b = linkLayer.WorldToLocal(to.worldBound);
                if (a.width <= 0f || b.width <= 0f || !bandOf.TryGetValue(from, out VisualElement fromBand)) continue;

                // Down out of its tier's cards, across in the gap under them, then down to the next level,
                // broken where it would cross the cards of a tier between them (one the chain skips).
                Rect source = linkLayer.WorldToLocal(fromBand.worldBound);
                float turn = source.yMax + BandGap * 0.5f;
                var start = new Vector2(a.center.x, a.yMax);
                var end = new Vector2(b.center.x, b.yMin);
                painter.BeginPath();
                painter.MoveTo(start);
                painter.LineTo(new Vector2(start.x, turn));
                painter.LineTo(new Vector2(end.x, turn));
                foreach (VisualElement band in bands)
                {
                    Rect between = linkLayer.WorldToLocal(band.worldBound);
                    if (between.yMin <= turn || between.yMax >= end.y) continue;

                    painter.LineTo(new Vector2(end.x, between.yMin));
                    painter.MoveTo(new Vector2(end.x, between.yMax));
                }

                painter.LineTo(new Vector2(end.x, end.y - 5f));
                painter.Stroke();

                // A small arrowhead on the card it leads to.
                painter.BeginPath();
                painter.MoveTo(new Vector2(end.x - 5f, end.y - 7f));
                painter.LineTo(new Vector2(end.x + 5f, end.y - 7f));
                painter.LineTo(new Vector2(end.x, end.y - 1f));
                painter.ClosePath();
                painter.Fill();
            }
        }

        private void DrawSheet(SkillState state)
        {
            SkillDefinition skill = null;
            UpgradeDefinition upgrade = null;
            if (selectedSkill != null && skillSync.Catalog.TryGet(selectedSkill, out skill) && selectedUpgrade != null) skill.TryGetUpgrade(selectedUpgrade, out upgrade);

            sheetEffects.Clear();
            sheetChoices.Clear();
            if (skill == null)
            {
                sheet.style.visibility = Visibility.Hidden;
                return;
            }

            sheet.style.visibility = Visibility.Visible;
            state.TryGet(skill.Id, out SkillProgress progress);
            bool unlocked = progress != null && progress.Unlocked;
            int open = SkillRules.OpenTiers(skill, progress);
            if (upgrade == null)
            {
                // No upgrade chosen: the skill itself.
                SetIcon(sheetIcon, SkillIcon(skill.Id));
                sheetName.text = Loc.Name(skill.Name);
                sheetKind.text = unlocked ? Loc.T("Tier {0} · {1} points to spend", open, SkillRules.PointsLeft(skill, progress)) : Loc.T("Locked");
                sheetDescription.text = string.IsNullOrEmpty(skill.Description) ? "" : Loc.T(skill.Description);
                if (unlocked)
                {
                    AddInfoRow(sheetEffects, Loc.T("Total experience"), Loc.T("{0} kcal", Loc.Number(progress.Xp, "0")));
                    AddLine(sheetEffects, Loc.T("Choose an upgrade to see what it does."));
                }
                else
                {
                    AddLine(sheetEffects, UnlockText(skill));
                }

                sheetTechnical.style.display = DisplayStyle.None;
                sheetReason.text = "";
                buy.style.display = DisplayStyle.None;
                return;
            }

            UpgradeProgress bought = progress != null ? progress.Of(upgrade.Id) : default;
            bool complete = SkillRules.IsComplete(upgrade, bought);
            if (upgrade.HasChoices && (selectedChoice < 0 || selectedChoice >= upgrade.Choices.Length)) selectedChoice = FirstFreeChoice(upgrade, bought);

            SkillAsset.Upgrade asset = skillSync.AssetOf(skill.Id)?.FindUpgrade(upgrade.Id);
            Texture2D icon = UpgradeIcon(skill.Id, upgrade.Id);
            if (upgrade.HasChoices && selectedChoice >= 0 && asset != null && selectedChoice < asset.choices.Length && asset.choices[selectedChoice].icon != null) icon = asset.choices[selectedChoice].icon;
            SetIcon(sheetIcon, icon);
            sheetName.text = upgrade.HasChoices && selectedChoice >= 0 ? Loc.T(upgrade.Choices[selectedChoice].Name) : Loc.T(upgrade.Name);
            sheetKind.text = upgrade.HasChoices ? Loc.T("Tier {0} · {1}", upgrade.Tier, Loc.T(upgrade.Name)) : KindText(upgrade);
            sheetDescription.text = upgrade.HasChoices && selectedChoice >= 0 && !string.IsNullOrEmpty(upgrade.Choices[selectedChoice].Description)
                ? Loc.T(upgrade.Choices[selectedChoice].Description)
                : string.IsNullOrEmpty(upgrade.Description) ? "" : Loc.T(upgrade.Description);

            int count = upgrade.HasChoices ? upgrade.ChoiceLimit : upgrade.Steps.Length;
            int done = upgrade.HasChoices ? bought.ChoiceCount : Mathf.Min(bought.Steps, upgrade.Steps.Length);
            if (upgrade.HasChoices)
            {
                // The options, the one chosen to buy framed; what each does is its card's tooltip.
                for (int c = 0; c < upgrade.Choices.Length; c++) sheetChoices.Add(ChoiceRow(upgrade, bought, c));
            }

            // The bonus now, then the level, the next one and the cost.
            var box = new VisualElement();
            box.AddToClassList("skill-info-box");
            var boxTitle = new Label(Loc.T("Current bonus"));
            boxTitle.AddToClassList("skill-info-title");
            box.Add(boxTitle);
            var boxValue = new Label(CurrentBonus(upgrade, bought));
            boxValue.AddToClassList("skill-info-big");
            box.Add(boxValue);
            sheetEffects.Add(box);

            AddInfoRow(sheetEffects, Loc.T("Level"), $"{done} / {count}");
            if (!upgrade.HasChoices) AddInfoRow(sheetEffects, Loc.T("Next level"), done < count ? ValueOf(upgrade, done, true) ?? Loc.T("Active") : Loc.T("Max"));
            AddInfoRow(sheetEffects, Loc.T("Skill point cost"), upgrade.Cost.ToString());

            // What it takes, ticked when met.
            if (!complete)
            {
                var requirements = new VisualElement();
                requirements.AddToClassList("skill-info-box");
                var heading = new Label(Loc.T("Requirements"));
                heading.AddToClassList("skill-info-title");
                requirements.Add(heading);

                int tierNeeded = upgrade.HasChoices ? upgrade.Tier : upgrade.TierOf(done);
                AddRequirement(requirements, Loc.T("Tier {0} reached", tierNeeded), unlocked && tierNeeded <= open);
                foreach (string required in upgrade.Requires)
                {
                    bool met = skill.TryGetUpgrade(required, out UpgradeDefinition other) && progress != null && SkillRules.IsComplete(other, progress.Of(required));
                    AddRequirement(requirements, Loc.T("{0} complete", other != null ? Loc.T(other.Name) : required), met);
                }

                if (!string.IsNullOrEmpty(upgrade.Replaces) && System.Array.IndexOf(upgrade.Requires, upgrade.Replaces) < 0 && skill.TryGetUpgrade(upgrade.Replaces, out UpgradeDefinition replaced))
                {
                    AddRequirement(requirements, Loc.T("{0}: at least one step", Loc.T(replaced.Name)), progress != null && progress.Of(upgrade.Replaces).Any);
                }

                AddRequirement(requirements, Loc.T("{0} skill points", upgrade.Cost), unlocked && SkillRules.PointsLeft(skill, progress) >= upgrade.Cost);
                sheetEffects.Add(requirements);
            }

            bool details = skillSync.Options.ShowDetails;
            sheetTechnical.style.display = details ? DisplayStyle.Flex : DisplayStyle.None;
            if (details) sheetTechnical.text = TechnicalText(upgrade);

            SkillRefusal refusal = SkillRules.CanBuy(skill, progress, upgrade.Id, selectedChoice);
            buy.style.display = complete ? DisplayStyle.None : DisplayStyle.Flex;
            buy.SetEnabled(refusal == SkillRefusal.None);
            buyText.text = Loc.T("Upgrade ({0} pt)", upgrade.Cost);
            sheetReason.text = complete ? Loc.T("Complete") : refusal == SkillRefusal.None ? "" : Loc.T(SkillSync.TextOf(refusal));
            sheetReason.EnableInClassList("skill-sheet-reason--done", complete);
        }

        // What an upgrade gives now: its last step's value, the options chosen, or that it is active.
        private static string CurrentBonus(UpgradeDefinition upgrade, UpgradeProgress bought)
        {
            if (upgrade.HasChoices)
            {
                var names = new List<string>();
                for (int c = 0; c < upgrade.Choices.Length && c < 32; c++)
                {
                    if (bought.HasChoice(c)) names.Add(Loc.T(upgrade.Choices[c].Name));
                }

                return names.Count > 0 ? string.Join(", ", names) : "—";
            }

            int done = Mathf.Min(bought.Steps, upgrade.Steps.Length);
            if (done == 0) return "—";
            return ValueOf(upgrade, done - 1, true) ?? Loc.T("Active");
        }

        private VisualElement ChoiceRow(UpgradeDefinition upgrade, UpgradeProgress bought, int index)
        {
            UpgradeChoice choice = upgrade.Choices[index];
            var row = new Button { focusable = false };
            row.AddToClassList("choice");
            row.AddToClassList("skill-choice");
            bool taken = bought.HasChoice(index);
            row.EnableInClassList("choice--selected", index == selectedChoice);
            var text = new Label(taken ? Loc.T("{0} (chosen)", Loc.T(choice.Name)) : Loc.T(choice.Name));
            text.AddToClassList("choice-text");
            row.Add(text);
            if (!string.IsNullOrEmpty(choice.Description)) row.tooltip = Loc.T(choice.Description);
            int chosen = index;
            row.clicked += () => selectedChoice = chosen;
            return row;
        }

        private static int FirstFreeChoice(UpgradeDefinition upgrade, UpgradeProgress bought)
        {
            for (int c = 0; c < upgrade.Choices.Length && c < 32; c++)
            {
                if (!bought.HasChoice(c)) return c;
            }

            return upgrade.Choices.Length > 0 ? 0 : -1;
        }

        private static string KindText(UpgradeDefinition upgrade)
        {
            if (upgrade.Steps.Length <= 1) return Loc.T("Tier {0} · unique", upgrade.Tier);
            return Loc.T("Tier {0} · {1} steps", upgrade.Tier, upgrade.Steps.Length);
        }

        // A step's value, short: "15 %" under a pip, "−50 %" or "+50 %" (signed) on a card or the sheet.
        // Null for a step whose help has no value (an interaction).
        private static string ValueOf(UpgradeDefinition upgrade, int step, bool signed)
        {
            if (step < 0 || step >= upgrade.Steps.Length) return null;

            foreach (SkillBonus bonus in upgrade.Steps[step].Bonuses)
            {
                string value = Loc.Number((float)bonus.ValueAt(step + 1, upgrade.Steps.Length, upgrade.TierOf(step)), "0.##");
                switch (bonus.Kind)
                {
                    case SkillBonusKind.YieldPercent: return signed ? $"+{value} %" : $"{value}%";
                    case SkillBonusKind.TimePercent:
                    case SkillBonusKind.CaloriePercent:
                    case SkillBonusKind.WeightPercent: return signed ? $"−{value} %" : $"{value}%";
                    case SkillBonusKind.FinishChancePercent: return signed ? $"{value} %" : $"{value}%";
                    case SkillBonusKind.YieldFlat: return $"+{value}";
                    case SkillBonusKind.TimeSeconds: return $"−{value} s";
                }
            }

            return null;
        }

        // What a help does, in words: "Takes 10 % fewer hits", "+5 % logs".
        internal static string BonusText(SkillBonus bonus, int step, int steps, int tier)
        {
            string value = Loc.Number((float)bonus.ValueAt(step, steps, tier), "0.##");
            switch (bonus.Kind)
            {
                case SkillBonusKind.YieldPercent: return Loc.T("+{0} % yield", value);
                case SkillBonusKind.YieldFlat: return Loc.T("+{0} yield", value);
                case SkillBonusKind.TimePercent: return Loc.T("{0} % faster", value);
                case SkillBonusKind.TimeSeconds: return Loc.T("{0} s faster", value);
                case SkillBonusKind.CaloriePercent: return Loc.T("{0} % fewer calories", value);
                case SkillBonusKind.WeightPercent: return Loc.T("{0} % lighter", value);
                case SkillBonusKind.Recipe: return Loc.T("A new recipe");
                case SkillBonusKind.FinishChancePercent: return Loc.T("{0} % chance a hit finishes it at once", value);
                default: return null;
            }
        }

        private static string TechnicalText(UpgradeDefinition upgrade)
        {
            var text = new StringBuilder();
            if (!string.IsNullOrEmpty(upgrade.TechnicalDescription)) text.AppendLine(Loc.T(upgrade.TechnicalDescription));
            text.Append(upgrade.Id).Append(" · ").Append(Loc.T("cost {0}", upgrade.Cost));
            for (int s = 0; s < upgrade.Steps.Length; s++)
            {
                text.AppendLine();
                text.Append(Loc.T("Step {0}, tier {1}:", s + 1, upgrade.TierOf(s)));
                foreach (SkillBonus bonus in upgrade.Steps[s].Bonuses)
                {
                    text.Append(' ').Append(bonus.Kind).Append('(').Append(bonus.Context).Append(") = ")
                        .Append(bonus.ValueAt(s + 1, upgrade.Steps.Length, upgrade.TierOf(s)).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
                }
            }

            foreach (UpgradeChoice choice in upgrade.Choices)
            {
                text.AppendLine();
                text.Append(choice.Id).Append(':');
                foreach (SkillBonus bonus in choice.Bonuses) text.Append(' ').Append(bonus.Kind).Append('(').Append(bonus.Context).Append(") = ").Append(bonus.Value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
            }

            return text.ToString();
        }

        // What unlocks a skill: "Unlocked by: felling a tree with an axe".
        private static string UnlockText(SkillDefinition skill)
        {
            var parts = new List<string>();
            foreach (SkillUnlock unlock in skill.Unlocks)
            {
                string action = Loc.T(ActionName(unlock.Context));
                parts.Add(unlock.Tool != Items.None ? Loc.T("{0} with {1}", action, Loc.Lower(Items.NameOf(unlock.Tool))) : action);
            }

            foreach (SkillPrerequisite prerequisite in skill.Prerequisites)
            {
                parts.Add(Loc.T("tier {0} of another skill", prerequisite.Tier));
            }

            return parts.Count == 0 ? Loc.T("Locked") : Loc.T("Unlocked by: {0}", string.Join(", ", parts));
        }

        // The English name of an action context, for the unlocking condition.
        private static string ActionName(string context)
        {
            string head = context?.Split(SkillContext.Separator)[0] ?? "";
            switch (head)
            {
                case SkillContext.Fell: return "felling a tree";
                case SkillContext.Dig: return "digging";
                case SkillContext.Mine: return "mining";
                case SkillContext.Cut: return "cutting plants";
                case SkillContext.Craft: return "crafting";
                case SkillContext.Place: return "placing blocks";
                case SkillContext.Till: return "tilling";
                default: return context ?? "";
            }
        }

        private static void AddInfoRow(VisualElement into, string label, string value)
        {
            var row = new VisualElement();
            row.AddToClassList("skill-info-row");
            var name = new Label(label);
            name.AddToClassList("skill-info-label");
            row.Add(name);
            var shown = new Label(value);
            shown.AddToClassList("skill-info-value");
            row.Add(shown);
            into.Add(row);
        }

        private static void AddRequirement(VisualElement into, string text, bool met)
        {
            var row = new VisualElement();
            row.AddToClassList("skill-requirement");
            row.EnableInClassList("skill-requirement--missing", !met);
            var icon = new VisualElement();
            icon.AddToClassList("icon");
            icon.AddToClassList(met ? "icon--check" : "icon--lock");
            icon.AddToClassList("skill-requirement-icon");
            row.Add(icon);
            var label = new Label(text);
            label.AddToClassList("skill-requirement-text");
            row.Add(label);
            into.Add(row);
        }

        private static void AddLine(VisualElement into, string text)
        {
            var label = new Label(text);
            label.AddToClassList("skill-effect");
            into.Add(label);
        }

        private Texture2D SkillIcon(string skillId)
        {
            SkillAsset asset = skillSync.AssetOf(skillId);
            return asset != null ? asset.icon : null;
        }

        private Texture2D UpgradeIcon(string skillId, string upgradeId)
        {
            SkillAsset.Upgrade upgrade = skillSync.AssetOf(skillId)?.FindUpgrade(upgradeId);
            return upgrade != null && upgrade.icon != null ? upgrade.icon : SkillIcon(skillId);
        }

        private static void SetIcon(VisualElement element, Texture2D texture)
        {
            element.style.backgroundImage = texture != null ? new StyleBackground(texture) : new StyleBackground(StyleKeyword.None);
        }
    }
}

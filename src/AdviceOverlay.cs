using Godot;
using BuildAdvisor.Integration;
using BuildAdvisor.Data;

namespace BuildAdvisor.UI;

public static class AdviceOverlay
{
    private const string ToggleName = "BuildAdvisorToggle";
    private const string PageName = "BuildAdvisorPage";
    // Panel geometry in the game's 1920x1080 layout: left side, clear of the top bar.
    private const float PanelX = 74, PanelY = 72, PanelWidth = 640, PanelHeight = 984;
    private const float ContentWidth = PanelWidth - 44, TextWidth = ContentWidth - 20;
    private const float BodyY = 158, FooterY = 884, BodyHeight = FooterY - 12 - BodyY;
    private const string TextName = "BuildAdvisorText";
    private const string AdviceBodyName = "BuildAdvisorAdviceBody";
    private const string SearchName = "BuildAdvisorCardSearch";
    private const string RunName = "BuildAdvisorRunReport";
    private const string RunTextName = "BuildAdvisorRunReportText";
    private const string TagsName = "BuildAdvisorTagReference";
    private const string HostName = "BuildAdvisorGlobalLayer";
    private const string LiveName = "BuildAdvisorLiveContext";
    private const string ArchetypeChooserName = "BuildAdvisorArchetypeChooser";
    private const string LanguageChooserName = "BuildAdvisorLanguageChooser";
    private const string RefreshName = "BuildAdvisorRefresh";
    private const string AutoPickName = "BuildAdvisorAutoPick";
    private const string OfflineName = "BuildAdvisorOffline";
    private const string CloseName = "BuildAdvisorClose";
    private const string ShopTierPrefix = "BuildAdvisorShopTier";
    private const string SelectionTierPrefix = "BuildAdvisorSelectionTier";
    private static Action? currentRefresh;
    private static Action? searchRefresh;
    private static Action? tagRefresh;
    private static bool searchLoading;
    private static string? searchCharacter;
    private static DateTimeOffset? contextMissingSince;
    private static string currentRawText = "BUILD ADVISOR\nОжидание данных текущего забега…";
    private static readonly CardCatalogClient SearchCatalog = new();
    private static readonly SpireCodexMetricsClient SearchMetrics = new();

    internal sealed record CardSearchEntry(CardDefinition Card, OfferScore Score);

    // Called by the mod initializer.  The same global CanvasLayer is retained
    // while the player moves between combat, rewards, events, map and shops.
    internal static void Bootstrap()
    {
        var root = (Engine.GetMainLoop() as SceneTree)?.Root;
        if (root is null) return;
        if (root.GetNodeOrNull<CanvasLayer>(HostName) is not null) return;
        Render(root, "BUILD ADVISOR\nОжидание данных текущего забега…", null, RunContextReader.TryRead());
    }

    internal static void Ensure(Node screen)
    {
        var root = (Engine.GetMainLoop() as SceneTree)?.Root;
        if (root?.GetNodeOrNull<CanvasLayer>(HostName) is not null) return;
        Render(screen, "BUILD ADVISOR\nОжидание данных текущего забега…", null, RunContextReader.TryRead());
    }

    internal static void SetRefreshAction(Action? action) => currentRefresh = action;

    internal static void Render(Node screen, string text, Action<bool>? feedback = null, RunContext? context = null)
    {
        var host = GlobalHost(screen);
        currentRawText = text;
        context ??= RunContextReader.TryRead();
        var label = Find<Label>(host, TextName);
        if (label is not null)
        {
            label.Text = L(text);
            label.CustomMinimumSize = new Vector2(TextWidth, HeightFor(label.Text));
            SyncArchetypeChooser(host, context);
            UpdateLive(host);
            return;
        }

        // 6.24 (variant B): a parchment panel on the left and a wax seal "BD" at the left edge; the
        // old emblem sat in the game's top bar over its own buttons.
        var toggle = AdvisorTheme.Seal(ToggleName);

        var page = new Panel { Name = PageName, Position = new Vector2(PanelX, PanelY), Size = new Vector2(PanelWidth, PanelHeight), ZIndex = 500, Visible = false, Theme = AdvisorTheme.PanelTheme() };
        page.AddThemeStyleboxOverride("panel", AdvisorTheme.PanelBox());
        var monogram = new Label { Text = "BD", Position = new Vector2(22, 12), Size = new Vector2(60, 40) };
        if (AdvisorTheme.Monogram is { } monogramFont) monogram.AddThemeFontOverride("font", monogramFont);
        monogram.AddThemeFontSizeOverride("font_size", 26); monogram.AddThemeColorOverride("font_color", AdvisorTheme.Wax);
        var title = new Label { Text = "Build Advisor", Position = new Vector2(84, 12), Size = new Vector2(420, 40) };
        if (AdvisorTheme.Heading is { } headingFont) title.AddThemeFontOverride("font", headingFont);
        title.AddThemeFontSizeOverride("font_size", 28);
        var live = new Label { Name = LiveName, Position = new Vector2(22, 56), Size = new Vector2(ContentWidth, 44), AutowrapMode = TextServer.AutowrapMode.WordSmart };
        live.AddThemeFontSizeOverride("font_size", 15); live.AddThemeColorOverride("font_color", AdvisorTheme.InkSoft);
        var close = new Button { Name = CloseName, Text = "×", Position = new Vector2(PanelWidth - 58, 12), Size = new Vector2(40, 40), Flat = true };
        close.AddThemeFontSizeOverride("font_size", 28);
        var adviceTab = Tab("BuildAdvisorAdviceTab", "Совет", new Vector2(22, 104));
        var rule = new ColorRect { Color = AdvisorTheme.PaperLine, Position = new Vector2(22, 146), Size = new Vector2(ContentWidth, 1), MouseFilter = Control.MouseFilterEnum.Ignore };

        var body = new Control { Name = AdviceBodyName, Position = new Vector2(22, BodyY), Size = new Vector2(ContentWidth, BodyHeight) };
        var scroll = new ScrollContainer { Position = Vector2.Zero, Size = new Vector2(ContentWidth, BodyHeight), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, VerticalScrollMode = ScrollContainer.ScrollMode.Auto };
        label = new Label { Name = TextName, Text = L(text), AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(TextWidth, HeightFor(L(text))), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        label.AddThemeFontSizeOverride("font_size", 17); scroll.AddChild(label); body.AddChild(scroll);
        // The tab exists immediately, even while the catalogue is still being fetched.
        var searchTab = Tab("BuildAdvisorSearchTab", "Искать карты", new Vector2(116, 104));
        var runTab = Tab("BuildAdvisorRunTab", "Анализ забега", new Vector2(268, 104));
        var tagsTab = Tab("BuildAdvisorTagsTab", "Метки", new Vector2(430, 104));
        var search = new Control { Name = SearchName, Position = new Vector2(22, BodyY), Size = new Vector2(ContentWidth, BodyHeight), Visible = false };
        var loading = new Label { Text = L("Каталог карт загружается…"), Position = new Vector2(0, 0), Size = new Vector2(ContentWidth, 42) };
        loading.AddThemeFontSizeOverride("font_size", 20); search.AddChild(loading);
        var run = new Control { Name = RunName, Position = new Vector2(22, BodyY), Size = new Vector2(ContentWidth, BodyHeight), Visible = false };
        var runScroll = new ScrollContainer { Position = Vector2.Zero, Size = new Vector2(ContentWidth, BodyHeight), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, VerticalScrollMode = ScrollContainer.ScrollMode.Auto };
        var runText = new Label { Name = RunTextName, AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(TextWidth, BodyHeight) };
        runText.AddThemeFontSizeOverride("font_size", 17); runScroll.AddChild(runText); run.AddChild(runScroll);
        var tagsPanel = CreateTagReference();
        var tabs = new[] { adviceTab, searchTab, runTab, tagsTab };
        void Activate(Button active) { foreach (var tab in tabs) MarkTab(tab, ReferenceEquals(tab, active)); }
        Activate(adviceTab);
        searchTab.Pressed += () =>
        {
            Activate(searchTab);
            search.Visible = true;
            body.Visible = false;
            run.Visible = false;
            tagsPanel.Visible = false;
            var liveContext = RunContextReader.TryRead();
            if (searchRefresh is not null && liveContext is not null && string.Equals(searchCharacter, liveContext.Character, StringComparison.OrdinalIgnoreCase))
                searchRefresh();
            else
                _ = LoadSearchFromTabAsync(host);
        };
        adviceTab.Pressed += () => { Activate(adviceTab); search.Visible = false; run.Visible = false; tagsPanel.Visible = false; body.Visible = true; };
        runTab.Pressed += () => { Activate(runTab); search.Visible = false; body.Visible = false; tagsPanel.Visible = false; run.Visible = true; UpdateRunReport(host); };
        tagsTab.Pressed += () => { Activate(tagsTab); search.Visible = false; body.Visible = false; run.Visible = false; tagsPanel.Visible = true; };

        var chooser = new OptionButton
        {
            Name = ArchetypeChooserName, CustomMinimumSize = new Vector2(250, 38),
            TooltipText = L("Направление колоды: вручную или автоматически"), Visible = false
        };
        chooser.ItemSelected += index =>
        {
            var liveContext = RunContextReader.TryRead();
            if (liveContext is null) return;
            var options = ArchetypePreferences.Options(liveContext.Character);
            if (index < 0 || index >= options.Count) return;
            ArchetypePreferences.Set(liveContext.Character, options[(int)index]);
            currentRefresh?.Invoke();
        };
        var refreshLive = new Button { Name = RefreshName, Text = L("Обновить"), CustomMinimumSize = new Vector2(120, 38) };
        refreshLive.Pressed += () => RefreshAdvice(host);
        var autoPick = new CheckButton
        {
            Name = AutoPickName, Text = L("Автовыбор"),
            ButtonPressed = AutoPickSettings.Enabled,
            CustomMinimumSize = new Vector2(150, 38)
        };
        autoPick.Toggled += enabled => AutoPickSettings.Enabled = enabled;
        // L1: offline mode. Cached and local data only; the choice persists between sessions.
        var offline = new CheckButton
        {
            Name = OfflineName, Text = L("Офлайн"),
            ButtonPressed = NetworkSettings.Offline,
            CustomMinimumSize = new Vector2(120, 38)
        };
        offline.Toggled += enabled => { NetworkSettings.Offline = enabled; RefreshAdvice(host); };
        var language = new OptionButton
        {
            Name = LanguageChooserName, CustomMinimumSize = new Vector2(120, 38)
        };
        language.AddItem("Русский"); language.AddItem("English");
        language.Select(ModLocalization.Current == AdvisorLanguage.English ? 1 : 0);
        language.ItemSelected += index =>
        {
            ModLocalization.Set(index == 1 ? AdvisorLanguage.English : AdvisorLanguage.Russian);
            Relocalize(host);
        };
        // Settings live in a wrapping footer under the content.
        var footerRule = new ColorRect { Color = AdvisorTheme.PaperLine, Position = new Vector2(22, FooterY - 8), Size = new Vector2(ContentWidth, 1), MouseFilter = Control.MouseFilterEnum.Ignore };
        var footer = new HFlowContainer { Position = new Vector2(22, FooterY), Size = new Vector2(ContentWidth, PanelHeight - FooterY - 12) };
        footer.AddThemeConstantOverride("h_separation", 10); footer.AddThemeConstantOverride("v_separation", 8);
        footer.AddChild(chooser); footer.AddChild(refreshLive); footer.AddChild(autoPick); footer.AddChild(offline); footer.AddChild(language);
        if (feedback is not null)
        {
            var useful = new Button { Text = L("Полезно"), CustomMinimumSize = new Vector2(100, 38) };
            var bad = new Button { Text = L("Сомнительно"), CustomMinimumSize = new Vector2(130, 38) };
            useful.Pressed += () => feedback(true); bad.Pressed += () => feedback(false); footer.AddChild(useful); footer.AddChild(bad);
        }
        page.AddChild(monogram); page.AddChild(title); page.AddChild(live); page.AddChild(close); page.AddChild(adviceTab); page.AddChild(searchTab); page.AddChild(runTab); page.AddChild(tagsTab); page.AddChild(rule);
        page.AddChild(body); page.AddChild(search); page.AddChild(run); page.AddChild(tagsPanel); page.AddChild(footerRule); page.AddChild(footer);
        host.AddChild(toggle); host.AddChild(page);
        toggle.Pressed += () => page.Visible = !page.Visible;
        close.Pressed += () => page.Hide();
        StartLiveUpdates(host);
        SyncArchetypeChooser(host, context);
        UpdateLive(host);
    }

    public static void Remove(Node screen)
    {
        // Global UI survives changes between game screens.
    }

    // Badges belong to the reward row, never to the card visual itself (the game reuses those
    // visuals in combat). This restores S-D markers without leaking them into the player's hand.
    internal static void ClearBadges(Node screen)
    {
        if (screen is not Viewport) AttachedBadges.StripDescendants(screen, "BuildAdvisorTier");
        var host = GlobalHost(screen);
        foreach (var child in host.GetChildren().Where(x => x.Name.ToString().StartsWith("BuildAdvisorTier", StringComparison.Ordinal)).ToArray())
        {
            host.RemoveChild(child);
            child.QueueFree();
        }
    }

    internal static void ClearShopBadges(Node screen)
    {
        if (screen is not Viewport) AttachedBadges.StripDescendants(screen, ShopTierPrefix);
        foreach (var child in Descendants(GlobalHost(screen)).Where(x => x.Name.ToString().StartsWith(ShopTierPrefix, StringComparison.Ordinal)).ToArray())
        {
            child.GetParent()?.RemoveChild(child);
            child.QueueFree();
        }
    }

    internal static void ClearSelectionBadges(Node screen)
    {
        if (screen is not Viewport) AttachedBadges.StripDescendants(screen, GridBadgeName);
        foreach (var child in Descendants(GlobalHost(screen)).Where(x => x.Name.ToString().StartsWith(SelectionTierPrefix, StringComparison.Ordinal)).ToArray())
        {
            child.GetParent()?.RemoveChild(child);
            child.QueueFree();
        }
    }

    // Event choices use several different screens and may be scrollable. Anchor each marker to
    // the real holder instead of assuming the fixed three-card reward layout.
    internal static void RenderSelectionBadges(Node screen, IEnumerable<(Control Anchor, OfferScore Score)> values, string actionLabel = "")
    {
        ClearSelectionBadges(screen);
        var host = GlobalHost(screen);
        var entries = values.Where(x => GodotObject.IsInstanceValid(x.Anchor)).ToArray();
        var best = entries.Select(x => x.Score.Final).DefaultIfEmpty(0m).Max();
        for (var index = 0; index < entries.Length; index++)
            AddSelectionBadge(host, screen, entries[index].Anchor, entries[index].Score, best, entries.Length > 1, actionLabel, SelectionTierPrefix + index);
    }

    // Card grids (deck upgrade/remove/transform, opened from the campfire or an event) keep holders
    // only for the visible rows and hand them to other cards while scrolling. A badge therefore
    // follows the card a holder shows now: the set of holders is re-read ten times a second, a
    // badge is rebuilt when its holder shows another card, and new rows get their badges.
    internal static void RenderTrackedSelectionBadges(Node screen, Func<IEnumerable<(Control Anchor, string Id)>> holders, IReadOnlyList<OfferScore> scores, string actionLabel = "")
    {
        ClearSelectionBadges(screen);
        var host = GlobalHost(screen);
        var byId = scores.GroupBy(x => Data.SpireCodexMetricsClient.NormalizeId(x.Id), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
        var sync = new SelectionBadgeSync
        {
            Name = SelectionTierPrefix + "Sync", Screen = screen, Host = host, Holders = holders, Scores = byId,
            Best = scores.Select(x => x.Final).DefaultIfEmpty(0m).Max(), Multiple = scores.Count > 1, ActionLabel = actionLabel
        };
        host.AddChild(sync);
        sync.Sync();
        FrameTicker.Add(sync.Tick);
    }

    private sealed class SelectionBadgeSync : Node
    {
        public Node? Screen { get; init; }
        public Node? Host { get; init; }
        public Func<IEnumerable<(Control Anchor, string Id)>>? Holders { get; init; }
        public IReadOnlyDictionary<string, OfferScore> Scores { get; init; } = new Dictionary<string, OfferScore>();
        public decimal Best { get; init; }
        public bool Multiple { get; init; }
        public string ActionLabel { get; init; } = "";
        private readonly Dictionary<Control, (Label Badge, string Id)> badges = new();
        private ulong next;
        private int counter;

        public bool Tick()
        {
            if (!GodotObject.IsInstanceValid(this) || IsQueuedForDeletion()) return false;
            if (Time.GetTicksMsec() < next) return true;
            next = Time.GetTicksMsec() + 100;
            Sync();
            return GodotObject.IsInstanceValid(this) && !IsQueuedForDeletion();
        }

        public void Sync()
        {
            if (Screen is null || Host is null || Holders is null || !GodotObject.IsInstanceValid(Screen) || !Screen.IsInsideTree())
            {
                foreach (var entry in badges.Values) if (GodotObject.IsInstanceValid(entry.Badge)) entry.Badge.QueueFree();
                badges.Clear();
                QueueFree();
                return;
            }
            var current = Holders().Where(x => GodotObject.IsInstanceValid(x.Anchor))
                .Select(x => (x.Anchor, Id: Data.SpireCodexMetricsClient.NormalizeId(x.Id))).ToArray();
            var shown = badges.Where(x => GodotObject.IsInstanceValid(x.Value.Badge)).ToDictionary(x => x.Key, x => x.Value.Id);
            var (create, remove) = BadgeTracking.Changes(shown, current, Scores.ContainsKey);
            foreach (var anchor in remove)
            {
                if (badges.TryGetValue(anchor, out var old) && GodotObject.IsInstanceValid(old.Badge)) old.Badge.QueueFree();
                badges.Remove(anchor);
            }
            foreach (var (anchor, id) in create)
                badges[anchor] = (AddSelectionBadge(Host, Screen, anchor, Scores[id], Best, Multiple, ActionLabel, SelectionTierPrefix + "Tracked" + counter++), id);
        }
    }

    // Deck grids (campfire upgrade, remove, transform). A badge positioned from global
    // coordinates drifted for rows outside the scroll window (6.22.2: badges of the lower rows
    // stacked at the top of the screen). Here the badge is a child of the card holder, so it
    // scrolls and clips with the card; the holder → card pairing comes from the grid itself and
    // a badge is retexted when its holder starts showing another card.
    internal static void RenderGridBadges(Node screen, Func<IEnumerable<(Control Holder, Control? Card, string Id)>> holders, IReadOnlyList<OfferScore> scores, string actionLabel)
    {
        ClearSelectionBadges(screen);
        var byId = scores.GroupBy(x => Data.SpireCodexMetricsClient.NormalizeId(x.Id), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
        var sync = new GridBadgeSync
        {
            Name = SelectionTierPrefix + "GridSync", Screen = screen, Holders = holders, Scores = byId,
            Best = scores.Select(x => x.Final).DefaultIfEmpty(0m).Max(), Multiple = scores.Count > 1, ActionLabel = actionLabel
        };
        GlobalHost(screen).AddChild(sync);
        sync.TreeExiting += sync.FreeAll;
        sync.Sync();
        FrameTicker.Add(sync.Tick);
        GD.Print($"Build Advisor grid badges: {scores.Count} scored cards, {holders().Count()} holders shown");
    }

    internal const string GridBadgeName = "BuildAdvisorGridBadge";

    private sealed class GridBadgeSync : Node
    {
        public Node? Screen { get; init; }
        public Func<IEnumerable<(Control Holder, Control? Card, string Id)>>? Holders { get; init; }
        public IReadOnlyDictionary<string, OfferScore> Scores { get; init; } = new Dictionary<string, OfferScore>();
        public decimal Best { get; init; }
        public bool Multiple { get; init; }
        public string ActionLabel { get; init; } = "";
        private readonly Dictionary<Control, (Label Badge, string Id)> badges = new();
        private ulong next;

        public bool Tick()
        {
            if (!GodotObject.IsInstanceValid(this) || IsQueuedForDeletion()) return false;
            if (Time.GetTicksMsec() < next) return true;
            next = Time.GetTicksMsec() + 100;
            Sync();
            return GodotObject.IsInstanceValid(this) && !IsQueuedForDeletion();
        }

        internal void FreeAll()
        {
            foreach (var entry in badges.Values)
                if (GodotObject.IsInstanceValid(entry.Badge)) { entry.Badge.GetParent()?.RemoveChild(entry.Badge); entry.Badge.QueueFree(); }
            badges.Clear();
        }

        public void Sync()
        {
            if (Screen is null || Holders is null || !GodotObject.IsInstanceValid(Screen) || !Screen.IsInsideTree())
            {
                FreeAll();
                QueueFree();
                return;
            }
            var current = new HashSet<Control>();
            foreach (var (holder, card, rawId) in Holders())
            {
                if (!GodotObject.IsInstanceValid(holder)) continue;
                current.Add(holder);
                var id = Data.SpireCodexMetricsClient.NormalizeId(rawId);
                badges.TryGetValue(holder, out var entry);
                if (!Scores.TryGetValue(id, out var score))
                {
                    if (entry.Badge is not null && GodotObject.IsInstanceValid(entry.Badge)) entry.Badge.Visible = false;
                    continue;
                }
                var badge = entry.Badge is not null && GodotObject.IsInstanceValid(entry.Badge) ? entry.Badge : Create(holder, Screen);
                if (entry.Id != id || !ReferenceEquals(badge, entry.Badge)) Describe(badge, score);
                badges[holder] = (badge, id);
                badge.Visible = true;
                Place(badge, holder, card);
            }
            foreach (var gone in badges.Keys.Where(x => !current.Contains(x)).ToArray())
            {
                if (GodotObject.IsInstanceValid(badges[gone].Badge)) badges[gone].Badge.Visible = false;
            }
        }

        private static Label Create(Control holder, Node screen)
        {
            // The holder is pooled: the badge leaves by itself once the holder serves another screen.
            var badge = new AttachedBadge
            {
                OwnerScreen = screen,
                Name = GridBadgeName, Size = new Vector2(132, 34), ZIndex = 20, ZAsRelative = true,
                MouseFilter = Control.MouseFilterEnum.Pass,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            };
            badge.AddThemeFontSizeOverride("font_size", 15);
            holder.AddChild(badge);
            badge.Activate();
            return badge;
        }

        private void Describe(Label badge, OfferScore score)
        {
            var tier = Tier(score.Final);
            var marker = score.Final == Best && Multiple ? "★ " : "";
            badge.Text = string.IsNullOrWhiteSpace(ActionLabel) ? $"{marker}{tier}  {score.Final:F0}" : $"{marker}{L(ActionLabel)} {score.Final:F0}";
            var details = L($"{score.Name}: {score.Final:F0}/100\n{ScoreExplanation.Formula(score)}\n{score.Reason}");
            var best = score.Final == Best && Multiple;
            if (badge is AttachedBadge attached)
            {
                attached.ExpandedText = details;
                attached.StylePopup = popup => AdvisorTheme.Popup(popup, best ? AdvisorTheme.Wax : null);
            }
            else badge.TooltipText = details;
            AdvisorTheme.Ribbon(badge, best);
        }

        // The card node is centred on the holder; the badge sits on the card's bottom rim.
        private static void Place(Label badge, Control holder, Control? card)
        {
            var size = card is not null && GodotObject.IsInstanceValid(card) && card.Size.Y > 0 ? card.Size * card.Scale : new Vector2(300, 422);
            var centre = card is not null && GodotObject.IsInstanceValid(card) ? card.Position : Vector2.Zero;
            badge.Position = new Vector2(centre.X - badge.Size.X / 2f, centre.Y + size.Y / 2f - badge.Size.Y * .75f);
        }
    }

    private static Label AddSelectionBadge(Node host, Node screen, Control anchor, OfferScore score, decimal best, bool multiple, string actionLabel, string name)
    {
        var tier = Tier(score.Final);
        var marker = score.Final == best && multiple ? "★ " : "";
        // S–D describes intrinsic card quality. For remove/transform/upgrade, the score is
        // action priority and must not make a weak removal target look like an S-tier card.
        var compactText = string.IsNullOrWhiteSpace(actionLabel)
            ? $"{marker}{tier}  {score.Final:F0}"
            : $"{marker}{actionLabel} {score.Final:F0}";
        var badge = new SelectionBadgeFollower
        {
            Target = anchor,
            SelectionScreen = screen,
            Name = name,
            Text = compactText,
            Size = new Vector2(140, 42), ZIndex = 260,
            MouseFilter = Control.MouseFilterEnum.Stop,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            TooltipText = L(string.IsNullOrWhiteSpace(actionLabel) ? $"{score.Name}\n{ScoreExplanation.Breakdown(score)}\n{score.TagText}\n{score.Impact}" : $"{score.Name}: {score.Final:F0}/100\n{ScoreExplanation.Formula(score)}\n{score.TagText}\n{score.Impact}")
        };
        AdvisorTheme.Ribbon(badge, score.Final == best && multiple);
        host.AddChild(badge);
        badge.RefreshPosition();
        FrameTicker.Add(badge.Tick);
        MakeBadgeExpandable(badge, compactText,
            L($"{marker}{(string.IsNullOrWhiteSpace(actionLabel) ? tier : actionLabel)}  {score.Final:F0}\n{(string.IsNullOrWhiteSpace(actionLabel) ? ScoreExplanation.Breakdown(score) : ScoreExplanation.Formula(score))}\n{FullTags(score.TagText)}"),
            badge.Position, badge.Size);
        return badge;
    }

    // Reward badges are children of the reward card holders (6.22.6): the reward screen is an
    // overlay, and a badge on the mod's own layer could end up under it (a reward with no badges
    // at all). A holder child is drawn with the card, above it, and leaves with it.
    internal static void RenderBadges(Node screen, IEnumerable<OfferScore> scores, IEnumerable<string>? offeredIds = null,
        Func<string, (Control Holder, object Model)?>? holderFor = null)
    {
        var row = ReflectionIds.Member(screen, "_cardRow") as Control;
        if (row is null) return;
        ClearBadges(screen);
        var host = GlobalHost(screen);
        var ranked = scores.ToArray();
        var byId = ranked.GroupBy(x => SpireCodexMetricsClient.NormalizeId(x.Id), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
        var values = offeredIds is null
            ? ranked
            : offeredIds.Select(id => byId.GetValueOrDefault(SpireCodexMetricsClient.NormalizeId(id))).Where(x => x is not null).Cast<OfferScore>().ToArray();
        var best = ranked.FirstOrDefault()?.Final ?? 0m;
        for (var index = 0; index < values.Length; index++)
        {
            var score = values[index];
            var tier = Tier(score.Final);
            var isBest = score.Final == best && values.Length > 1;
            // 6.24 ribbon under the card: tier and score, then the win-rate scale.
            var compactText = $"{(isBest ? "★ " : "")}{tier} · {score.Final:F0}\n{TakeSkipModel.Format(TakeSkipModel.WinDelta(score))} п.п.";
            var anchor = holderFor?.Invoke(score.Id);
            if (anchor is { } attached && GodotObject.IsInstanceValid(attached.Holder))
            {
                var holder = attached.Holder;
                var cardBadge = new AttachedBadge
                {
                    OwnerScreen = screen, ExpectedModel = attached.Model,
                    CurrentModel = () => GodotObject.IsInstanceValid(holder) ? CardModelOf(holder) : null,
                    Place = badge => PlaceBelowCard(badge, holder),
                    Name = $"BuildAdvisorTier{index}", Text = compactText, Size = new Vector2(150, 56),
                    ZIndex = 20, ZAsRelative = true, MouseFilter = Control.MouseFilterEnum.Pass,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                    ExpandedText = L($"{(isBest ? "★ " : "")}{tier}  {score.Final:F0}\n{ScoreExplanation.Breakdown(score)}\n{FullTags(score.TagText)}\n{score.Impact}"),
                    StylePopup = popup => AdvisorTheme.Popup(popup, isBest ? AdvisorTheme.Wax : null)
                };
                AdvisorTheme.Ribbon(cardBadge, isBest, 16);
                holder.AddChild(cardBadge);
                PlaceBelowCard(cardBadge, holder);
                cardBadge.Activate();
                continue;
            }
            // Fallback when the screen does not expose its holders: the stable centred layout.
            var width = Math.Max(1280, screen.GetViewport().GetVisibleRect().Size.X);
            var spacing = values.Length <= 3 ? 350f : 285f;
            var position = new Vector2(width / 2f + (index - (values.Length - 1) / 2f) * spacing - 56, 394);
            var badge = new Label
            {
                Name = $"BuildAdvisorTier{index}", Text = compactText,
                Position = position - new Vector2(18, 0), Size = new Vector2(148, 58), ZIndex = 200,
                MouseFilter = Control.MouseFilterEnum.Stop,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                TooltipText = L($"{score.Name}\n{ScoreExplanation.Breakdown(score)}\n{score.TagText}\n{score.Impact}")
            };
            AdvisorTheme.Ribbon(badge, isBest);
            host.AddChild(badge);
            MakeBadgeExpandable(badge, compactText, L($"{(isBest ? "★ " : "")}{tier}  {score.Final:F0}\n{ScoreExplanation.Breakdown(score)}\n{FullTags(score.TagText)}"), badge.Position, badge.Size);
        }
    }

    private static object? CardModelOf(Control holder)
    {
        var card = ReflectionIds.Member(holder, "CardNode");
        return (card is null ? null : ReflectionIds.Member(card, "Model")) ?? ReflectionIds.Member(holder, "CardModel");
    }

    // 6.24: the reward ribbon hangs under the card (variant B), clear of the card title.
    private static void PlaceBelowCard(Label badge, Control holder)
    {
        var card = ReflectionIds.Member(holder, "CardNode") as Control;
        var valid = card is not null && GodotObject.IsInstanceValid(card) && card.Size.Y > 0;
        var bottom = AttachedBadges.CardBottomCentre(valid ? card!.Position : Vector2.Zero, valid ? card!.Size : new Vector2(300, 422), valid ? card!.Scale : Vector2.One);
        badge.Position = AttachedBadges.Below(bottom, badge.Size, 10f);
    }

    private static void StylePopup(Label popup, Color color) => AdvisorTheme.Popup(popup, color);

    private static void StyleBadge(Label badge, string tier, bool best, int fontSize)
    {
        _ = tier;
        AdvisorTheme.Ribbon(badge, best, fontSize);
    }

    private const string OfflineTooltip = "Офлайн-режим: мод не обращается к сети и использует сохранённую статистику и локальную модель. Pick Coach и загрузка картинок отключаются.";

    // 6.18: every gain decision also shows the take/skip scale in win-rate percentage points.
    private static string WinLine(OfferScore score) =>
        $"{TakeSkipModel.Format(TakeSkipModel.WinDelta(score))} п.п. к победе · уверенность {TakeSkipModel.Evidence(score)}";

    // Shop badges are children of the sale slots (6.22.6). Global positions with fixed row
    // heights put the badges of a shop with another layout (the fake merchant, a bought-out row,
    // relics and potions) in a line at the top of the screen, away from their items. A slot
    // child moves with the slot's opening animation and sits above the slot's clickable area.
    internal static void RenderShopBadges(Node screen, IEnumerable<(Control Slot, OfferScore Score)> values, Func<OfferScore, string>? note = null)
    {
        ClearShopBadges(screen);
        var entries = values.Where(x => GodotObject.IsInstanceValid(x.Slot)).ToArray();
        var best = entries.Select(x => x.Score.Final).DefaultIfEmpty(0m).Max();
        for (var index = 0; index < entries.Length; index++)
        {
            var (slot, score) = entries[index];
            var tier = Tier(score.Final);
            var isBest = score.Final == best && entries.Length > 1;
            var badge = new AttachedBadge
            {
                OwnerScreen = screen,
                Place = label => PlaceAboveSlot(label, slot),
                Name = ShopTierPrefix + index,
                Text = $"{(isBest ? "★ " : "")}{tier}  {score.Final:F0}",
                Size = new Vector2(120, 34), ZIndex = 30, ZAsRelative = true,
                MouseFilter = Control.MouseFilterEnum.Pass,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                // L3: price and the gold left after buying, next to the win-rate scale.
                ExpandedText = L($"{score.Name}\n{ScoreExplanation.Breakdown(score)}{(note?.Invoke(score) is { Length: > 0 } priceLine ? "\n" + priceLine : "")}\n{FullTags(score.TagText)}\n{score.Impact}"),
                StylePopup = popup => AdvisorTheme.Popup(popup, isBest ? AdvisorTheme.Wax : null)
            };
            StyleBadge(badge, tier, isBest, 14);
            slot.AddChild(badge);
            PlaceAboveSlot(badge, slot);
            badge.Activate();
        }
    }

    private static void PlaceAboveSlot(Label badge, Control slot)
    {
        if (!GodotObject.IsInstanceValid(slot)) return;
        var area = ReflectionIds.Member(slot, "Hitbox") as Control;
        if (area is null || !GodotObject.IsInstanceValid(area) || area.Size.X <= 1 || area.Size.Y <= 1) area = slot;
        // The clickable area's top centre, expressed in the slot's own coordinates.
        var toSlot = slot.GetGlobalTransformWithCanvas().AffineInverse() * area.GetGlobalTransformWithCanvas();
        badge.Position = AttachedBadges.Above(toSlot * new Vector2(area.Size.X / 2f, 0f), badge.Size, 6f);
    }

    private const string MapBadgePrefix = "BuildAdvisorMapBadge";
    private const string OptionBadgePrefix = "BuildAdvisorOptionBadge";

    // H1: small labels under map points: expected damage and HP after the node along the best
    // path through it; the recommended path gets a gold frame and a star. The map stays in the
    // scene tree under combat and event rooms, so labels are shown only while isShown() is true.
    internal static void RenderMapBadges(Node map, Func<bool> isShown, IEnumerable<(Control Point, string Text, string Severity, bool Best, string Tooltip)> values)
    {
        _ = isShown;
        ClearMapBadges(map);
        // Labels are children of the map points: they scroll with the map and are drawn in the
        // map's own layer, so rooms opened over the map cover them (6.22.2 followers in the
        // global layer stayed put while the map scrolled).
        var index = 0;
        foreach (var (point, rawText, severity, best, tooltip) in values)
        {
            if (!GodotObject.IsInstanceValid(point)) continue;
            var text = (best ? "★ " : "") + (string.IsNullOrWhiteSpace(rawText) ? "путь" : rawText);
            if (string.IsNullOrWhiteSpace(rawText) && !best) continue;
            var color = severity switch { "high" => new Color("e0484f"), "medium" => new Color("e4b13c"), "rest" => new Color("5fc27e"), _ => new Color("8fd0ff") };
            var size = new Vector2(best ? 118 : 104, 26);
            var badge = new Label
            {
                Name = MapBadgePrefix + index++, Text = L(text), Size = size, ZIndex = 30, ZAsRelative = true,
                MouseFilter = Control.MouseFilterEnum.Pass,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                // NMapPoint scales on hover; labels keep their size relative to the point.
                Position = new Vector2(point.Size.X / 2f - size.X / 2f, point.Size.Y + 2f)
            };
            badge.AddThemeFontSizeOverride("font_size", 13);
            badge.AddThemeColorOverride("font_color", color);
            var box = BadgeBox(best ? new Color("f2c94c") : new Color(color, .7f));
            box.BorderWidthLeft = box.BorderWidthRight = box.BorderWidthTop = box.BorderWidthBottom = best ? 3 : 1;
            box.CornerRadiusTopLeft = box.CornerRadiusTopRight = box.CornerRadiusBottomLeft = box.CornerRadiusBottomRight = 8;
            badge.AddThemeStyleboxOverride("normal", box);
            var details = L(tooltip);
            AttachedBadges.EnablePopup(badge, () => details, popup => StylePopup(popup, color));
            point.AddChild(badge);
        }
    }

    internal static void ClearMapBadges(Node map)
    {
        ClearTags(map, MapBadgePrefix);
        foreach (var child in Descendants(map).Where(x => x.Name.ToString().StartsWith(MapBadgePrefix, StringComparison.Ordinal)).ToArray())
        {
            child.GetParent()?.RemoveChild(child);
            child.QueueFree();
        }
    }

    // Event options: the community verdict sits inside the right end of each option button.
    internal static void RenderOptionBadges(Node screen, IEnumerable<(Control Option, string Text, string Severity, bool Best, string Tooltip)> values)
    {
        ClearTags(screen, OptionBadgePrefix);
        RenderTags(screen, OptionBadgePrefix, TagPlacement.RightInside, () => true,
            values.Where(x => !string.IsNullOrWhiteSpace(x.Text)).Select(x => (x.Option, (x.Best ? "★ " : "") + x.Text, x.Severity, x.Best, x.Tooltip)), 150f, 30f, 15);
    }

    private enum TagPlacement { Below, RightInside }

    private static void RenderTags(Node screen, string prefix, TagPlacement placement, Func<bool> isShown,
        IEnumerable<(Control Target, string Text, string Severity, bool Best, string Tooltip)> values, float width, float height, int fontSize)
    {
        var host = GlobalHost(screen);
        var index = 0;
        foreach (var (target, text, severity, best, tooltip) in values)
        {
            if (!GodotObject.IsInstanceValid(target)) continue;
            var color = severity switch { "high" => new Color("e0484f"), "medium" => new Color("e4b13c"), "rest" or "good" => new Color("5fc27e"), _ => new Color("8fd0ff") };
            var badge = new TagBadgeFollower
            {
                Target = target, IsShown = isShown, Placement = placement, Name = prefix + index++,
                Text = L(text), Size = new Vector2(best ? width + 14 : width, height), ZIndex = 240,
                MouseFilter = Control.MouseFilterEnum.Pass,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            };
            var details = L(tooltip);
            AttachedBadges.EnablePopup(badge, () => details, popup => StylePopup(popup, color));
            badge.AddThemeFontSizeOverride("font_size", fontSize);
            badge.AddThemeColorOverride("font_color", color);
            var box = BadgeBox(best ? new Color("f2c94c") : new Color(color, .7f));
            box.BorderWidthLeft = box.BorderWidthRight = box.BorderWidthTop = box.BorderWidthBottom = best ? 3 : 1;
            box.CornerRadiusTopLeft = box.CornerRadiusTopRight = box.CornerRadiusBottomLeft = box.CornerRadiusBottomRight = 8;
            badge.AddThemeStyleboxOverride("normal", box);
            host.AddChild(badge);
            badge.RefreshPosition();
            FrameTicker.Add(badge.Tick);
        }
    }

    private static void ClearTags(Node screen, string prefix)
    {
        foreach (var child in GlobalHost(screen).GetChildren().Where(x => x.Name.ToString().StartsWith(prefix, StringComparison.Ordinal)).ToArray())
        {
            child.GetParent()?.RemoveChild(child);
            child.QueueFree();
        }
    }

    private sealed class TagBadgeFollower : Label
    {
        public Control? Target { get; init; }
        public Func<bool> IsShown { get; init; } = () => true;
        public TagPlacement Placement { get; init; }

        public bool Tick()
        {
            if (!GodotObject.IsInstanceValid(this) || IsQueuedForDeletion()) return false;
            RefreshPosition();
            return !IsQueuedForDeletion();
        }

        public void RefreshPosition()
        {
            if (Target is null || !GodotObject.IsInstanceValid(Target) || !Target.IsInsideTree())
            {
                QueueFree();
                return;
            }
            bool shown;
            try { shown = IsShown(); } catch { shown = false; }
            var transform = Target.GetGlobalTransformWithCanvas();
            var anchor = Placement == TagPlacement.Below
                ? transform * new Vector2(Target.Size.X / 2f, Target.Size.Y)
                : transform * new Vector2(Target.Size.X, Target.Size.Y / 2f);
            var viewport = GetViewportRect().Size;
            Visible = shown && Target.IsVisibleInTree() && anchor.Y > 0f && anchor.Y < viewport.Y - Size.Y;
            if (!Visible) return;
            Position = Placement == TagPlacement.Below
                ? new Vector2(anchor.X - Size.X / 2f, anchor.Y + 2f)
                : new Vector2(anchor.X - Size.X - 18f, anchor.Y - Size.Y / 2f);
        }
    }

    private sealed class SelectionBadgeFollower : Label
    {
        public Control? Target { get; init; }
        public Node? SelectionScreen { get; init; }
        public bool PauseFollowing { get; set; }

        public bool Tick()
        {
            if (!GodotObject.IsInstanceValid(this) || IsQueuedForDeletion()) return false;
            if (!PauseFollowing) RefreshPosition();
            return !IsQueuedForDeletion();
        }

        public void RefreshPosition()
        {
            if (Target is null || !GodotObject.IsInstanceValid(Target) || !Target.IsInsideTree())
            {
                QueueFree();
                return;
            }
            Visible = Target.IsVisibleInTree() && !PreviewIsOpen(SelectionScreen);
            if (!Visible) return;
            var topCenter = Target.GetGlobalTransformWithCanvas() * new Vector2(Target.Size.X / 2f, 0f);
            var viewport = GetViewportRect().Size;
            // A holder scrolled out of view must not leave its badge pinned to the screen edge.
            if (topCenter.Y < 0f || topCenter.Y > viewport.Y - 24f) { Visible = false; return; }
            Position = new Vector2(
                Math.Clamp(topCenter.X - Size.X / 2f, 8f, Math.Max(8f, viewport.X - Size.X - 8f)),
                Math.Clamp(topCenter.Y - Size.Y - 10f, 8f, Math.Max(8f, viewport.Y - Size.Y - 8f)));
        }

        private static bool PreviewIsOpen(Node? screen)
        {
            if (screen is null || !GodotObject.IsInstanceValid(screen)) return false;
            foreach (var name in new[] { "_previewContainer", "_upgradeSinglePreviewContainer", "_upgradeMultiPreviewContainer", "_bundlePreviewContainer" })
                if (ReflectionIds.Member(screen, name) is Control control && control.Visible && control.IsVisibleInTree()) return true;
            return false;
        }
    }

    private static void MakeBadgeExpandable(Label badge, string compactText, string expandedText, Vector2 compactPosition, Vector2 compactSize)
    {
        // M4: the breakdown has one line per factor group; size the hover card to its text
        // (about 22 px per wrapped line at font 15) instead of a fixed 178 px box.
        var wrappedLines = expandedText.Split('\n').Sum(line => Math.Max(1, (int)Math.Ceiling(line.Length / 68d)));
        var expandedSize = new Vector2(660, Math.Clamp(wrappedLines * 22 + 20, 178, 460));
        badge.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        badge.MouseEntered += () =>
        {
            if (!GodotObject.IsInstanceValid(badge)) return;
            if (badge is SelectionBadgeFollower selectionFollower) selectionFollower.PauseFollowing = true;
            var viewportSize = badge.GetViewportRect().Size;
            var wanted = badge.Position - new Vector2((expandedSize.X - compactSize.X) / 2f, expandedSize.Y - compactSize.Y + 8f);
            var expandedPosition = new Vector2(
                Math.Clamp(wanted.X, 8f, Math.Max(8f, viewportSize.X - expandedSize.X - 8f)),
                Math.Clamp(wanted.Y, 8f, Math.Max(8f, viewportSize.Y - expandedSize.Y - 8f)));
            badge.Text = expandedText;
            badge.Position = expandedPosition;
            badge.Size = expandedSize;
            badge.ZIndex = 1000;
        };
        badge.MouseExited += () =>
        {
            if (!GodotObject.IsInstanceValid(badge)) return;
            badge.Text = compactText;
            badge.Size = compactSize;
            badge.ZIndex = 200;
            if (badge is SelectionBadgeFollower selectionFollower)
            {
                selectionFollower.PauseFollowing = false;
                selectionFollower.RefreshPosition();
            }
            else badge.Position = compactPosition;
        };
    }

    private static string FullTags(string text) => string.IsNullOrWhiteSpace(text) ? "Контекст не определён" : text;

    private static string ShortTags(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "контекст";
        var tags = text.Split('·', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Take(2).ToArray();
        var joined = string.Join(" · ", tags);
        return joined.Length <= 25 ? joined : joined[..22] + "…";
    }

    internal static void EnableCardSearch(Node screen, RunContext context, Func<IReadOnlyList<CardSearchEntry>> source)
    {
        var page = GlobalHost(screen).GetNodeOrNull<Panel>(PageName);
        if (page is null) return;
        var adviceBody = page.GetNodeOrNull<Control>(AdviceBodyName);
        var search = page.GetNodeOrNull<Control>(SearchName);
        if (search is null) return;
        foreach (var child in search.GetChildren().ToArray()) { search.RemoveChild(child); child.QueueFree(); }
        var heading = new Label { Text = L("Карты для поиска"), Position = new Vector2(0, 0), Size = new Vector2(ContentWidth, 34) };
        if (AdvisorTheme.Heading is { } headingFont) heading.AddThemeFontOverride("font", headingFont);
        heading.AddThemeFontSizeOverride("font_size", 22);
        var note = new Label { Text = L("Список учитывает вашу колоду и выбранный архетип. Измените архетип сверху и нажмите «Обновить»."), Position = new Vector2(0, 36), Size = new Vector2(ContentWidth, 46), AutowrapMode = TextServer.AutowrapMode.WordSmart };
        note.AddThemeFontSizeOverride("font_size", 15); note.AddThemeColorOverride("font_color", AdvisorTheme.InkSoft);
        var half = (ContentWidth - 12) / 2f;
        var type = Filter("Все типы", "Атака", "Навык", "Талант", new Vector2(0, 88)); type.Size = new Vector2(half, 38);
        var rarity = Filter("Любая редкость", "Обычная", "Необычная", "Редкая", new Vector2(half + 12, 88)); rarity.Size = new Vector2(half, 38);
        var tier = Filter("Любая метка", "S", "A", "B", "C", "D", new Vector2(0, 134)); tier.Size = new Vector2(half, 38);
        var sorting = Filter("Сортировка: приоритет", "Сортировка: польза колоде", "Сортировка: надёжность", "Сортировка: редкость", new Vector2(half + 12, 134)); sorting.Size = new Vector2(half, 38);
        var refresh = new Button { Text = L("Обновить"), Position = new Vector2(0, 180), Size = new Vector2(150, 38) };
        var resultsScroll = new ScrollContainer { Position = new Vector2(0, 228), Size = new Vector2(ContentWidth, BodyHeight - 228), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, VerticalScrollMode = ScrollContainer.ScrollMode.Auto };
        var results = new VBoxContainer { CustomMinimumSize = new Vector2(TextWidth, BodyHeight - 228) };
        results.AddThemeConstantOverride("separation", 10); resultsScroll.AddChild(results);

        void Update()
        {
            var liveContext = RunContextReader.TryRead() ?? context;
            var filtered = source().Where(x => MatchesType(x.Card, type.Selected) && MatchesRarity(x.Card, rarity.Selected) && MatchesTier(x.Score.Final, tier.Selected));
            var entries = SortSearch(filtered, sorting.Selected).Take(30).ToArray();
            foreach (var child in results.GetChildren().ToArray()) { results.RemoveChild(child); child.QueueFree(); }
            var summary = new Label { Text = L($"Архетип: {ArchetypePreferences.Get(liveContext.Character)} · Найдено: {entries.Length}"), CustomMinimumSize = new Vector2(TextWidth, 30) };
            summary.AddThemeFontSizeOverride("font_size", 17); results.AddChild(summary);
            if (entries.Length == 0)
            {
                results.AddChild(new Label { Text = L("Нет карт с такими фильтрами."), CustomMinimumSize = new Vector2(TextWidth, 44) });
                return;
            }
            foreach (var entry in entries)
            {
                var row = new PanelContainer { CustomMinimumSize = new Vector2(TextWidth, 150) };
                var content = new HBoxContainer(); content.AddThemeConstantOverride("separation", 12);
                var preview = new TextureRect { CustomMinimumSize = new Vector2(92, 132), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered };
                var details = new Label { Text = L($"{Tier(entry.Score.Final)}  {entry.Score.Final:F0}/100   {entry.Card.Name}\n{TypeName(entry.Card.Type)} · {RarityName(entry.Card.Rarity)} · стоимость {entry.Card.Cost}\n{ScoreExplanation.Formula(entry.Score)}\nМетки: {entry.Score.TagText}\n{entry.Score.Impact}"), AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(TextWidth - 124, 140), VerticalAlignment = VerticalAlignment.Center };
                details.AddThemeFontSizeOverride("font_size", 14);
                content.AddChild(preview); content.AddChild(details); row.AddChild(content); results.AddChild(row);
                _ = CardImageCache.LoadIntoAsync(preview, entry.Card.Id, entry.Card.ImageUrl);
            }
            results.CustomMinimumSize = new Vector2(TextWidth, Math.Max(BodyHeight - 228, 50 + entries.Length * 160));
        }
        refresh.Pressed += Update; type.ItemSelected += _ => Update(); rarity.ItemSelected += _ => Update(); tier.ItemSelected += _ => Update(); sorting.ItemSelected += _ => Update();
        search.AddChild(heading); search.AddChild(note); search.AddChild(type); search.AddChild(rarity); search.AddChild(tier); search.AddChild(sorting); search.AddChild(refresh); search.AddChild(resultsScroll);
        searchCharacter = context.Character;
        searchRefresh = Update;
        Update();
    }

    private static async Task LoadSearchFromTabAsync(Node host)
    {
        var context = RunContextReader.TryRead();
        if (context is null) { ShowSearchStatus(host, "Текущий забег ещё не прочитан. Закройте окно, откройте карту и повторите попытку."); return; }
        var cached = SearchCatalog.TryGetCached();
        if (cached.Count > 0)
        {
            var metrics = new Dictionary<string, EntityMetric>(StringComparer.OrdinalIgnoreCase);
            EnableCardSearch(host, context, () => CardRewardPatch.BuildSearchEntries(RunContextReader.TryRead() ?? context, cached, metrics));
            _ = EnrichSearchMetricsAsync(context, metrics);
            return;
        }
        if (searchLoading) return;
        searchLoading = true;
        ShowSearchStatus(host, "Каталог карт загружается…");
        try
        {
            // The catalogue is enough for useful archetype/need scoring and is cached on disk.
            // Remote metrics enrich the same dictionary later and never block the filters.
            var catalog = await SearchCatalog.GetAsync();
            var liveContext = RunContextReader.TryRead() ?? context;
            var metrics = new Dictionary<string, EntityMetric>(StringComparer.OrdinalIgnoreCase);
            EnableCardSearch(host, liveContext, () => CardRewardPatch.BuildSearchEntries(RunContextReader.TryRead() ?? liveContext, catalog, metrics));
            _ = EnrichSearchMetricsAsync(liveContext, metrics);
        }
        catch (Exception error)
        {
            ShowSearchStatus(host, "Не удалось открыть каталог: " + error.GetType().Name + ". Нажмите «Искать карты», чтобы повторить.");
            GD.PrintErr("Build Advisor search: " + error);
        }
        finally { searchLoading = false; }
    }

    private static void EnsureCachedSearch(Node host, RunContext context)
    {
        if (searchRefresh is not null && string.Equals(searchCharacter, context.Character, StringComparison.OrdinalIgnoreCase)) return;
        var cached = SearchCatalog.TryGetCached();
        if (cached.Count == 0) return;
        var metrics = new Dictionary<string, EntityMetric>(StringComparer.OrdinalIgnoreCase);
        EnableCardSearch(host, context, () => CardRewardPatch.BuildSearchEntries(RunContextReader.TryRead() ?? context, cached, metrics));
        _ = EnrichSearchMetricsAsync(context, metrics);
        GD.Print($"Build Advisor search: restored {cached.Count} cached keys for {context.Character}");
    }

    private static async Task EnrichSearchMetricsAsync(RunContext context, Dictionary<string, EntityMetric> target)
    {
        try
        {
            var loaded = await SearchMetrics.GetMetricsAsync("cards", context.Bracket, context.Character);
            foreach (var pair in loaded) target[pair.Key] = pair.Value;
            if (string.Equals(searchCharacter, context.Character, StringComparison.OrdinalIgnoreCase)) searchRefresh?.Invoke();
        }
        catch (Exception error) { GD.PrintErr("Build Advisor search metrics: " + error.GetType().Name); }
    }

    private static void ShowSearchStatus(Node host, string text)
    {
        var search = host.GetNodeOrNull<Panel>(PageName)?.GetNodeOrNull<Control>(SearchName);
        if (search is null) return;
        foreach (var child in search.GetChildren().ToArray()) { search.RemoveChild(child); child.QueueFree(); }
        var status = new Label { Text = L(text), Position = Vector2.Zero, Size = new Vector2(ContentWidth, 80), AutowrapMode = TextServer.AutowrapMode.WordSmart };
        status.AddThemeFontSizeOverride("font_size", 19);
        search.AddChild(status);
        searchRefresh = null;
        searchCharacter = null;
    }

    private static Button Tab(string name, string text, Vector2 position)
    {
        var tab = new Button { Name = name, Text = L(text), Position = position, Size = new Vector2(text == "Анализ забега" ? 150 : text == "Искать карты" ? 140 : 86, 38), FocusMode = Control.FocusModeEnum.None };
        if (AdvisorTheme.Heading is { } font) tab.AddThemeFontOverride("font", font);
        tab.AddThemeFontSizeOverride("font_size", 20);
        MarkTab(tab, false);
        return tab;
    }

    private static void MarkTab(Button tab, bool active)
    {
        foreach (var state in new[] { "normal", "hover", "pressed", "focus" }) tab.AddThemeStyleboxOverride(state, AdvisorTheme.TabBox(active));
        tab.AddThemeColorOverride("font_color", active ? AdvisorTheme.Wax : AdvisorTheme.InkSoft);
        tab.AddThemeColorOverride("font_hover_color", AdvisorTheme.Wax);
    }
    private static OptionButton Filter(string first, string second, string third, string fourth, Vector2 position)
    {
        var option = new OptionButton { Position = position, Size = new Vector2(240, 40) };
        option.AddItem(L(first)); option.AddItem(L(second)); option.AddItem(L(third)); option.AddItem(L(fourth)); return option;
    }
    private static OptionButton Filter(string first, string second, string third, string fourth, string fifth, Vector2 position)
    {
        var option = Filter(first, second, third, fourth, position); option.AddItem(L(fifth)); return option;
    }
    private static OptionButton Filter(string first, string second, string third, string fourth, string fifth, string sixth, Vector2 position)
    {
        var option = Filter(first, second, third, fourth, fifth, position); option.AddItem(L(sixth)); return option;
    }
    private static StyleBoxFlat Box(Color color, int radius) => new() { BgColor = color, CornerRadiusTopLeft = radius, CornerRadiusTopRight = radius, CornerRadiusBottomLeft = radius, CornerRadiusBottomRight = radius };
    private static Control CreateTagReference()
    {
        var panel = new Control { Name = TagsName, Position = new Vector2(22, BodyY), Size = new Vector2(ContentWidth, BodyHeight), Visible = false };
        var heading = new Label { Text = L($"Справочник меток Build Advisor · {CardSemanticTags.Reference.Count} признаков"), Position = Vector2.Zero, Size = new Vector2(ContentWidth, 34), AutowrapMode = TextServer.AutowrapMode.WordSmart };
        if (AdvisorTheme.Heading is { } headingFont) heading.AddThemeFontOverride("font", headingFont);
        heading.AddThemeFontSizeOverride("font_size", 21);
        var query = new LineEdit { PlaceholderText = L("Поиск по названию или описанию…"), Position = new Vector2(0, 44), Size = new Vector2(ContentWidth, 40) };
        var category = new OptionButton { Position = new Vector2(0, 92), Size = new Vector2(ContentWidth, 40) };
        var categories = CardSemanticTags.Reference.Select(x => x.Category).Distinct().OrderBy(x => x).ToArray();
        var scroll = new ScrollContainer { Position = new Vector2(0, 142), Size = new Vector2(ContentWidth, BodyHeight - 142), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, VerticalScrollMode = ScrollContainer.ScrollMode.Auto };
        var content = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(TextWidth, BodyHeight - 142) };
        content.AddThemeFontSizeOverride("font_size", 16); scroll.AddChild(content);
        void Rebuild()
        {
            var needle = query.Text.Trim();
            var selected = category.Selected <= 0 ? null : categories[category.Selected - 1];
            var rows = CardSemanticTags.Reference.Where(x => (selected is null || x.Category == selected) &&
                (needle.Length == 0 || x.Label.Contains(needle, StringComparison.OrdinalIgnoreCase) || L(x.Label).Contains(needle, StringComparison.OrdinalIgnoreCase) || x.Description.Contains(needle, StringComparison.OrdinalIgnoreCase) || L(x.Description).Contains(needle, StringComparison.OrdinalIgnoreCase) || x.Key.Contains(needle, StringComparison.OrdinalIgnoreCase)))
                .GroupBy(x => x.Category).Select(group => $"{L(group.Key).ToUpperInvariant()}\n" + string.Join("\n", group.Select(x => $"  {L(x.Label)} — {L(x.Description)}")));
            content.Text = string.Join("\n\n", rows);
            content.CustomMinimumSize = new Vector2(TextWidth, HeightFor(content.Text));
        }
        void LocalizeTags()
        {
            var selected = category.Selected;
            heading.Text = L($"Справочник меток Build Advisor · {CardSemanticTags.Reference.Count} признаков");
            query.PlaceholderText = L("Поиск по названию или описанию…");
            category.Clear();
            category.AddItem(L("Все категории"));
            foreach (var name in categories) category.AddItem(L(name));
            category.Select(Math.Clamp((int)selected, 0, categories.Length));
            Rebuild();
        }
        query.TextChanged += _ => Rebuild(); category.ItemSelected += _ => Rebuild();
        panel.AddChild(heading); panel.AddChild(query); panel.AddChild(category); panel.AddChild(scroll);
        tagRefresh = LocalizeTags;
        LocalizeTags();
        return panel;
    }
    private static StyleBoxFlat BadgeBox(Color accent) => new()
    {
        BgColor = new Color(0.018f, 0.035f, 0.06f, 0.96f), BorderColor = accent,
        BorderWidthLeft = 3, BorderWidthTop = 3, BorderWidthRight = 3, BorderWidthBottom = 3,
        CornerRadiusTopLeft = 12, CornerRadiusTopRight = 12, CornerRadiusBottomLeft = 12, CornerRadiusBottomRight = 12,
        ShadowColor = new Color(accent, .28f), ShadowSize = 5
    };
    private static Node GlobalHost(Node fallback)
    {
        var root = (Engine.GetMainLoop() as SceneTree)?.Root;
        if (root is null) return fallback;
        var existing = root.GetNodeOrNull<CanvasLayer>(HostName);
        if (existing is not null) return existing;
        var layer = new CanvasLayer { Name = HostName, Layer = 120 };
        root.AddChild(layer);
        return layer;
    }
    private static void UpdateLive(Node host)
    {
        var live = Find<Label>(host, LiveName);
        if (live is null) return;
        var context = RunContextReader.TryRead();
        if (context is not null)
        {
            contextMissingSince = null;
            ArchetypePreferences.ObserveRun(context.Character, context.Floor, context.Cards.Count, context.RunSeed);
            SyncArchetypeChooser(host, context);
            live.Text = L($"Act {context.Act} · этаж {context.Floor} · HP {context.Hp}/{context.MaxHp} · золото {context.Gold} · {context.Cards.Count} карт · {context.Relics.Count} реликвий");
            EnsureCachedSearch(host, context);
            UpdateRunReport(host);
            return;
        }
        contextMissingSince ??= DateTimeOffset.UtcNow;
        live.Text = L("Ожидание данных забега…");
        // Brief scene transitions can temporarily hide run state.  Only clear stale
        // shop/reward advice after the state has really been absent for a few ticks.
        if (DateTimeOffset.UtcNow - contextMissingSince < TimeSpan.FromSeconds(3)) return;
        ArchetypePreferences.EndRun();
        SyncArchetypeChooser(host, null);
        var label = Find<Label>(host, TextName);
        if (label is not null && !currentRawText.Contains("Нет активного забега", StringComparison.Ordinal))
        {
            currentRawText = "BUILD ADVISOR\nНет активного забега.";
            label.Text = L(currentRawText);
            label.CustomMinimumSize = new Vector2(TextWidth, HeightFor(label.Text));
            currentRefresh = null;
            ClearBadges(host);
        }
    }

    private static void SyncArchetypeChooser(Node host, RunContext? context)
    {
        var chooser = Find<OptionButton>(host, ArchetypeChooserName);
        if (chooser is null) return;
        if (context is null) { chooser.Hide(); return; }
        var options = ArchetypePreferences.Options(context.Character);
        var expected = options.Select(option => L("Архетип: " + option)).ToArray();
        var stale = chooser.ItemCount != expected.Length || Enumerable.Range(0, chooser.ItemCount).Any(index => chooser.GetItemText(index) != expected[index]);
        if (stale)
        {
            chooser.Clear();
            foreach (var option in expected) chooser.AddItem(option);
        }
        chooser.Select(Math.Max(0, options.ToList().IndexOf(ArchetypePreferences.Get(context.Character))));
        chooser.Show();
    }
    private static void UpdateRunReport(Node host)
    {
        var label = Find<Label>(host, RunTextName);
        var context = RunContextReader.TryRead();
        if (label is null) return;
        if (context is null) { label.Text = L("Нет активного забега."); return; }
        var hp = context.MaxHp > 0 ? (decimal)context.Hp / context.MaxHp : 0m;
        var survival = Math.Clamp(25m + hp * 45m + Math.Min(6, context.SkillCards) * 5m, 0m, 100m);
        var tempo = Math.Clamp(25m + Math.Min(8, context.AttackCards) * 9m - Math.Max(0m, context.AverageCost - 1.5m) * 12m, 0m, 100m);
        var scaling = Math.Clamp(25m + Math.Min(3, context.PowerCards) * 22m + (context.Act == 1 ? 15m : 0m), 0m, 100m);
        var consistency = Math.Clamp(70m - Math.Max(0, context.Cards.Count - 15) * 3m + Math.Min(8, context.SkillCards) * 3m, 0m, 100m);
        var economy = Math.Clamp(context.Gold / 3m + (context.Floor < 35 ? 15m : 0m), 0m, 100m);
        var overall = survival * .30m + tempo * .22m + scaling * .22m + consistency * .16m + economy * .10m;
        var history = context.History.Count == 0 ? "История изменений пока пуста." : string.Join("\n", context.History.TakeLast(8));
        label.Text = L($"АНАЛИЗ ЗАБЕГА · {overall:F0}/100\n\n" +
                     $"Выживаемость  {Meter(survival)} {survival:F0}\nТемп           {Meter(tempo)} {tempo:F0}\nМасштабирование {Meter(scaling)} {scaling:F0}\nСтабильность   {Meter(consistency)} {consistency:F0}\nЭкономика      {Meter(economy)} {economy:F0}\n\n" +
                     LocalDeckAdvisor.Snapshot(context) + "\n\n" + RunHistoryInsights.Summary(context.Character, context.Floor) + "\n" + PersonalRunDiary.Summary(context.Character) +
                     "\n\n" + ArchetypeAdvisor.Format(ArchetypeAdvisor.Current(context, null).Direction, ArchetypeAdvisor.Current(context, null).Candidates) +
                     "\n\nПоследние изменения:\n" + history +
                     "\n\nОценки — диагностические ориентиры, а не гарантия исхода боя. Низкий показатель показывает, что именно стоит исправлять следующей наградой или покупкой.");
        label.CustomMinimumSize = new Vector2(TextWidth, HeightFor(label.Text));
    }

    private static string Meter(decimal value)
    {
        var filled = Math.Clamp((int)Math.Round(value / 10m), 0, 10);
        return "[" + new string('■', filled) + new string('·', 10 - filled) + "]";
    }

    private static IOrderedEnumerable<CardSearchEntry> SortSearch(IEnumerable<CardSearchEntry> entries, long mode) => mode switch
    {
        1 => entries.OrderByDescending(x => x.Score.Context).ThenByDescending(x => x.Score.Final),
        2 => entries.OrderByDescending(x => x.Score.Confidence).ThenByDescending(x => x.Score.Final),
        3 => entries.OrderByDescending(x => RarityRank(x.Card.Rarity)).ThenByDescending(x => x.Score.Final),
        _ => entries.OrderByDescending(x => x.Score.Final)
    };

    private static int RarityRank(string rarity) => CardTaxonomy.RarityName(rarity) switch { "Редкая" => 3, "Необычная" => 2, "Обычная" => 1, _ => 0 };
    private static void RefreshAdvice(Node host)
    {
        UpdateLive(host);
        if (currentRefresh is not null) { currentRefresh(); return; }
        var label = Find<Label>(host, TextName);
        var context = RunContextReader.TryRead();
        if (label is null || context is null) return;
        currentRawText = "BUILD ADVISOR · обновлено вручную\n\n" + LocalDeckAdvisor.Snapshot(context) + "\n\nТекущие рекомендации автоматически заменятся при появлении новой награды, события или магазина.";
        label.Text = L(currentRawText);
        label.CustomMinimumSize = new Vector2(TextWidth, HeightFor(label.Text));
    }
    private static void Relocalize(Node host)
    {
        var label = Find<Label>(host, TextName);
        if (label is not null)
        {
            label.Text = L(currentRawText);
            label.CustomMinimumSize = new Vector2(TextWidth, HeightFor(label.Text));
        }
        var advice = Find<Button>(host, "BuildAdvisorAdviceTab"); if (advice is not null) advice.Text = L("Совет");
        var search = Find<Button>(host, "BuildAdvisorSearchTab"); if (search is not null) search.Text = L("Искать карты");
        var run = Find<Button>(host, "BuildAdvisorRunTab"); if (run is not null) run.Text = L("Анализ забега");
        var tags = Find<Button>(host, "BuildAdvisorTagsTab"); if (tags is not null) tags.Text = L("Метки");
        var refresh = Find<Button>(host, RefreshName);
        if (refresh is not null) { refresh.Text = L("Обновить"); refresh.TooltipText = L("Прочитать текущее состояние забега заново"); }
        var autoPick = Find<CheckButton>(host, AutoPickName);
        if (autoPick is not null)
        {
            autoPick.Text = L("Автовыбор");
            autoPick.TooltipText = L("Автоматически берёт карту только при высокой уверенности, явном преимуществе и когда пропуск хуже. Не выбирает спорные варианты.");
        }
        var offline = Find<CheckButton>(host, OfflineName);
        if (offline is not null) { offline.Text = L("Офлайн"); offline.TooltipText = L(OfflineTooltip); }
        var close = Find<Button>(host, CloseName); if (close is not null) close.TooltipText = L("Закрыть");
        searchRefresh = null;
        searchCharacter = null;
        tagRefresh?.Invoke();
        var context = RunContextReader.TryRead();
        SyncArchetypeChooser(host, context);
        UpdateLive(host);
        var searchPanel = Find<Control>(host, SearchName);
        if (searchPanel?.Visible == true) _ = LoadSearchFromTabAsync(host);
        currentRefresh?.Invoke();
    }
    // L2: F8 shows and hides the panel. The game itself binds F1-F7, F9, F10 (NInputManager)
    // and F11 (developer console), and F12 is Steam's screenshot key.
    internal const Key PanelHotkey = Key.F8;

    internal static void TogglePanel(Node host)
    {
        var page = host.GetNodeOrNull<Panel>(PageName);
        if (page is not null) page.Visible = !page.Visible;
    }

    // The hotkey and the once-a-second run line (6.22.7: a ticker; the LiveUpdater._Process
    // override was never called by the engine, so F8 never worked).
    private static void StartLiveUpdates(Node host)
    {
        var hotkeyDown = false;
        ulong nextUpdate = 0;
        FrameTicker.Add(() =>
        {
            if (!GodotObject.IsInstanceValid(host) || !host.IsInsideTree()) return false;
            // Polled with edge detection: one press toggles once, holding the key does not
            // flicker, and no input event is consumed from the game.
            var down = Input.IsKeyPressed(PanelHotkey) || Input.IsPhysicalKeyPressed(PanelHotkey);
            if (down && !hotkeyDown) TogglePanel(host);
            hotkeyDown = down;
            if (Time.GetTicksMsec() < nextUpdate) return true;
            nextUpdate = Time.GetTicksMsec() + 1000;
            UpdateLive(host);
            return true;
        });
    }

    private static bool MatchesType(CardDefinition card, long filter) => filter switch { 1 => card.Type.Contains("attack", StringComparison.OrdinalIgnoreCase), 2 => card.Type.Contains("skill", StringComparison.OrdinalIgnoreCase), 3 => card.Type.Contains("power", StringComparison.OrdinalIgnoreCase), _ => true };
    private static bool MatchesRarity(CardDefinition card, long filter) => filter switch { 1 => CardTaxonomy.RarityName(card.Rarity) == "Обычная", 2 => CardTaxonomy.RarityName(card.Rarity) == "Необычная", 3 => CardTaxonomy.RarityName(card.Rarity) == "Редкая", _ => true };
    private static bool MatchesTier(decimal score, long filter) => filter == 0 || Tier(score) == new[] { "", "S", "A", "B", "C", "D" }[Math.Clamp((int)filter, 0, 5)];
    private static string TypeName(string type) => type.Contains("attack", StringComparison.OrdinalIgnoreCase) ? "Атака" : type.Contains("power", StringComparison.OrdinalIgnoreCase) ? "Талант" : "Навык";
    private static string RarityName(string rarity) => CardTaxonomy.RarityName(rarity);
    private static string Tier(decimal score) => score >= 90 ? "S" : score >= 78 ? "A" : score >= 65 ? "B" : score >= 50 ? "C" : "D";
    private static Color TierColor(string tier) => tier switch { "S" => new Color("b43cff"), "A" => new Color("dd3344"), "B" => new Color("e49b28"), "C" => new Color("3187d8"), _ => new Color("59616c") };
    private static IEnumerable<Node> Descendants(Node root)
    {
        foreach (var child in root.GetChildren())
        {
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
    private static T? Find<T>(Node root, string name) where T : Node => root.FindChild(name, true, false) as T;
    private static string L(string text) => ModLocalization.Text(text);
    // Wrapped height at font 17 in the narrow panel: ~60 characters per line, 25 px per line.
    private static float HeightFor(string text) =>
        Math.Max(BodyHeight - 10, 40 + text.Split('\n').Sum(line => Math.Max(1, (int)Math.Ceiling(line.Length / 60d))) * 25);
}

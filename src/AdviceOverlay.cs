using Godot;
using BuildAdvisor.Integration;
using BuildAdvisor.Data;

namespace BuildAdvisor.UI;

public static class AdviceOverlay
{
    private const string ToggleName = "BuildAdvisorToggle";
    private const string PageName = "BuildAdvisorPage";
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
            label.CustomMinimumSize = new Vector2(1500, HeightFor(label.Text));
            SyncArchetypeChooser(host, context);
            UpdateLive(host);
            return;
        }

        // Keep the control and the underlying bitmap at the same size. A TextureButton
        // otherwise draws the source texture at its native 2K resolution.
        var toggle = new TextureButton { Name = ToggleName, Position = new Vector2(1510, 4), Size = new Vector2(68, 68), CustomMinimumSize = new Vector2(68, 68), IgnoreTextureSize = true, ZIndex = 500, TooltipText = "Build Advisor · F8" };
        var emblem = LoadEmblem();
        if (emblem is not null) toggle.TextureNormal = emblem;

        var page = new Panel { Name = PageName, Position = new Vector2(110, 55), Size = new Vector2(1700, 930), ZIndex = 500, Visible = false };
        page.AddThemeStyleboxOverride("panel", Box(new Color(0.015f, 0.035f, 0.075f, 0.985f), 14));
        var title = new Label { Text = "BUILD ADVISOR", Position = new Vector2(28, 18), Size = new Vector2(420, 42) };
        title.AddThemeFontSizeOverride("font_size", 28); title.AddThemeColorOverride("font_color", new Color("f5ca63"));
        var live = new Label { Name = LiveName, Position = new Vector2(520, 22), Size = new Vector2(980, 34) };
        live.AddThemeFontSizeOverride("font_size", 18); live.AddThemeColorOverride("font_color", new Color("a6c4d9"));
        var adviceTab = Tab("BuildAdvisorAdviceTab", "Совет", new Vector2(28, 70));
        var close = new Button { Name = CloseName, Text = "×", Position = new Vector2(1628, 16), Size = new Vector2(46, 42), TooltipText = L("Закрыть") };
        close.AddThemeFontSizeOverride("font_size", 28);

        var body = new Control { Name = AdviceBodyName, Position = new Vector2(28, 118), Size = new Vector2(1644, 784) };
        var scroll = new ScrollContainer { Position = Vector2.Zero, Size = new Vector2(1644, 784), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, VerticalScrollMode = ScrollContainer.ScrollMode.Auto };
        label = new Label { Name = TextName, Text = L(text), AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(1580, HeightFor(L(text))), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        label.AddThemeFontSizeOverride("font_size", 20); scroll.AddChild(label); body.AddChild(scroll);
        // The tab exists immediately, even while the catalogue is still being fetched.
        var searchTab = Tab("BuildAdvisorSearchTab", "Искать карты", new Vector2(160, 70));
        var runTab = Tab("BuildAdvisorRunTab", "Анализ забега", new Vector2(292, 70));
        var tagsTab = Tab("BuildAdvisorTagsTab", "Метки", new Vector2(442, 70));
        var search = new Control { Name = SearchName, Position = new Vector2(28, 118), Size = new Vector2(1644, 784), Visible = false };
        var loading = new Label { Text = L("Каталог карт загружается…"), Position = new Vector2(0, 0), Size = new Vector2(800, 42) };
        loading.AddThemeFontSizeOverride("font_size", 22); search.AddChild(loading);
        var run = new Control { Name = RunName, Position = new Vector2(28, 118), Size = new Vector2(1644, 784), Visible = false };
        var runScroll = new ScrollContainer { Position = Vector2.Zero, Size = new Vector2(1644, 784), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, VerticalScrollMode = ScrollContainer.ScrollMode.Auto };
        var runText = new Label { Name = RunTextName, AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(1580, 760) };
        runText.AddThemeFontSizeOverride("font_size", 20); runScroll.AddChild(runText); run.AddChild(runScroll);
        var tagsPanel = CreateTagReference();
        searchTab.Pressed += () =>
        {
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
        adviceTab.Pressed += () => { search.Visible = false; run.Visible = false; tagsPanel.Visible = false; body.Visible = true; };
        runTab.Pressed += () => { search.Visible = false; body.Visible = false; tagsPanel.Visible = false; run.Visible = true; UpdateRunReport(host); };
        tagsTab.Pressed += () => { search.Visible = false; body.Visible = false; run.Visible = false; tagsPanel.Visible = true; };

        var chooser = new OptionButton
        {
            Name = ArchetypeChooserName, Position = new Vector2(580, 72), Size = new Vector2(300, 38),
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
        var refreshLive = new Button { Name = RefreshName, Text = L("Обновить"), Position = new Vector2(892, 72), Size = new Vector2(150, 38), TooltipText = L("Прочитать текущее состояние забега заново") };
        refreshLive.Pressed += () => RefreshAdvice(host);
        var autoPick = new CheckButton
        {
            Name = AutoPickName, Text = L("Автовыбор"),
            ButtonPressed = AutoPickSettings.Enabled,
            Position = new Vector2(1060, 72),
            Size = new Vector2(190, 38),
            TooltipText = L("Автоматически берёт карту только при высокой уверенности, явном преимуществе и когда пропуск хуже. Не выбирает спорные варианты.")
        };
        autoPick.Toggled += enabled => AutoPickSettings.Enabled = enabled;
        // L1: offline mode. Cached and local data only; the choice persists between sessions.
        var offline = new CheckButton
        {
            Name = OfflineName, Text = L("Офлайн"),
            ButtonPressed = NetworkSettings.Offline,
            Position = new Vector2(1256, 72),
            Size = new Vector2(144, 38),
            TooltipText = L(OfflineTooltip)
        };
        offline.Toggled += enabled => { NetworkSettings.Offline = enabled; RefreshAdvice(host); };
        var language = new OptionButton
        {
            Name = LanguageChooserName, Position = new Vector2(1406, 72), Size = new Vector2(100, 38),
            TooltipText = "Language / Язык"
        };
        language.AddItem("Русский"); language.AddItem("English");
        language.Select(ModLocalization.Current == AdvisorLanguage.English ? 1 : 0);
        language.ItemSelected += index =>
        {
            ModLocalization.Set(index == 1 ? AdvisorLanguage.English : AdvisorLanguage.Russian);
            Relocalize(host);
        };
        if (feedback is not null)
        {
            var useful = new Button { Text = "👍", Position = new Vector2(1516, 72), Size = new Vector2(54, 38), TooltipText = L("Совет полезен") };
            var bad = new Button { Text = "⚑", Position = new Vector2(1578, 72), Size = new Vector2(54, 38), TooltipText = L("Совет сомнителен") };
            useful.Pressed += () => feedback(true); bad.Pressed += () => feedback(false); page.AddChild(useful); page.AddChild(bad);
        }
        page.AddChild(title); page.AddChild(live); page.AddChild(adviceTab); page.AddChild(searchTab); page.AddChild(runTab); page.AddChild(tagsTab); page.AddChild(chooser); page.AddChild(refreshLive); page.AddChild(autoPick); page.AddChild(offline); page.AddChild(language); page.AddChild(close); page.AddChild(body); page.AddChild(search); page.AddChild(run); page.AddChild(tagsPanel);
        host.AddChild(toggle); host.AddChild(page);
        toggle.Pressed += () => page.Visible = !page.Visible;
        close.Pressed += () => page.Hide();
        host.AddChild(new LiveUpdater { Name = "BuildAdvisorLiveUpdater" });
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
        var host = GlobalHost(screen);
        foreach (var child in host.GetChildren().Where(x => x.Name.ToString().StartsWith("BuildAdvisorTier", StringComparison.Ordinal)).ToArray())
        {
            host.RemoveChild(child);
            child.QueueFree();
        }
    }

    internal static void ClearShopBadges(Node screen)
    {
        foreach (var child in Descendants(GlobalHost(screen)).Where(x => x.Name.ToString().StartsWith(ShopTierPrefix, StringComparison.Ordinal)).ToArray())
        {
            child.GetParent()?.RemoveChild(child);
            child.QueueFree();
        }
    }

    internal static void ClearSelectionBadges(Node screen)
    {
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
        private double elapsed;
        private int counter;

        public override void _Process(double delta)
        {
            elapsed += delta;
            if (elapsed < .1) return;
            elapsed = 0;
            Sync();
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

    private static Label AddSelectionBadge(Node host, Node screen, Control anchor, OfferScore score, decimal best, bool multiple, string actionLabel, string name)
    {
        var tier = Tier(score.Final);
        var marker = score.Final == best && multiple ? "★" : "◆";
        // S–D describes intrinsic card quality. For remove/transform/upgrade, the score is
        // action priority and must not make a weak removal target look like an S-tier card.
        var compactText = string.IsNullOrWhiteSpace(actionLabel)
            ? $"{marker} {tier}  {score.Final:F0}"
            : $"{marker} {actionLabel} {score.Final:F0}";
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
        badge.AddThemeFontSizeOverride("font_size", 15);
        badge.AddThemeColorOverride("font_color", TierColor(tier));
        badge.AddThemeStyleboxOverride("normal", BadgeBox(TierColor(tier)));
        host.AddChild(badge);
        badge.RefreshPosition();
        MakeBadgeExpandable(badge, compactText,
            L($"{marker} {(string.IsNullOrWhiteSpace(actionLabel) ? tier : actionLabel)}  {score.Final:F0}\n{(string.IsNullOrWhiteSpace(actionLabel) ? ScoreExplanation.Breakdown(score) : ScoreExplanation.Formula(score))}\n{FullTags(score.TagText)}"),
            badge.Position, badge.Size);
        return badge;
    }

    internal static void RenderBadges(Node screen, IEnumerable<OfferScore> scores, IEnumerable<string>? offeredIds = null)
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
            // Reward cards use a stable centred layout. Matching internal model nodes is
            // unreliable because their position can be (0,0) even when the visual is elsewhere.
            var width = Math.Max(1280, screen.GetViewport().GetVisibleRect().Size.X);
            var spacing = values.Length <= 3 ? 350f : 285f;
            var position = new Vector2(width / 2f + (index - (values.Length - 1) / 2f) * spacing - 56, 394);
            var tier = Tier(score.Final);
            var isBest = score.Final == best && values.Length > 1;
            var shortTags = ShortTags(score.TagText);
            var compactText = $"{(isBest ? "★" : "◆")} {tier}  {score.Final:F0}\n{shortTags}";
            var badge = new Label
            {
                Name = $"BuildAdvisorTier{index}", Text = compactText,
                Position = position - new Vector2(18, 0), Size = new Vector2(148, 58), ZIndex = 200,
                MouseFilter = Control.MouseFilterEnum.Stop,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                TooltipText = L($"{score.Name}\n{ScoreExplanation.Breakdown(score)}\n{score.TagText}\n{score.Impact}")
            };
            badge.AddThemeFontSizeOverride("font_size", 15);
            badge.AddThemeColorOverride("font_color", TierColor(tier));
            badge.AddThemeStyleboxOverride("normal", BadgeBox(TierColor(tier)));
            host.AddChild(badge);
            MakeBadgeExpandable(badge, compactText, L($"{(isBest ? "★" : "◆")} {tier}  {score.Final:F0}\n{ScoreExplanation.Breakdown(score)}\n{FullTags(score.TagText)}"), badge.Position, badge.Size);
        }
    }

    private const string OfflineTooltip = "Офлайн-режим: мод не обращается к сети и использует сохранённую статистику и локальную модель. Pick Coach и загрузка картинок отключаются.";

    // 6.18: every gain decision also shows the take/skip scale in win-rate percentage points.
    private static string WinLine(OfferScore score) =>
        $"{TakeSkipModel.Format(TakeSkipModel.WinDelta(score))} п.п. к победе · уверенность {TakeSkipModel.Evidence(score)}";

    internal static void RenderShopBadges(Node screen, IEnumerable<(Control Slot, OfferScore Score)> values, Func<OfferScore, string>? note = null)
    {
        ClearShopBadges(screen);
        var host = GlobalHost(screen);
        var entries = values.Where(x => GodotObject.IsInstanceValid(x.Slot)).ToArray();
        var best = entries.Select(x => x.Score.Final).DefaultIfEmpty(0m).Max();
        for (var index = 0; index < entries.Length; index++)
        {
            var (slot, score) = entries[index];
            if (slot.Size.X <= 1 || slot.Size.Y <= 1) continue;
            var tier = Tier(score.Final);
            var compactText = $"{(score.Final == best && entries.Length > 1 ? "★" : "◆")} {tier}  {score.Final:F0}";
            // L3: price and the gold left after buying, next to the win-rate scale.
            // The shop animates its slots after Open(). Keep following the slot on the global
            // unscaled layer instead of taking one early snapshot of an in-flight position.
            var badge = new ShopBadgeFollower
            {
                Slot = slot,
                ShopRow = index < 5 ? 0 : 1,
                Name = ShopTierPrefix + index,
                Text = compactText,
                Size = new Vector2(140, 42), ZIndex = 250,
                MouseFilter = Control.MouseFilterEnum.Stop,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                TooltipText = L($"{score.Name}\n{ScoreExplanation.Breakdown(score)}{(note?.Invoke(score) is { Length: > 0 } priceLine ? "\n" + priceLine : "")}\n{score.TagText}\n{score.Impact}")
            };
            badge.AddThemeFontSizeOverride("font_size", 15);
            badge.AddThemeColorOverride("font_color", TierColor(tier));
            badge.AddThemeStyleboxOverride("normal", BadgeBox(TierColor(tier)));
            host.AddChild(badge);
            badge.RefreshPosition();
            MakeBadgeExpandable(badge, compactText, L($"{(score.Final == best && entries.Length > 1 ? "★" : "◆")} {tier}  {score.Final:F0}\n{ScoreExplanation.Breakdown(score)}{(note?.Invoke(score) is { Length: > 0 } priceNote ? "\n" + priceNote : "")}\n{FullTags(score.TagText)}"), badge.Position, badge.Size);
        }
    }

    internal static Vector2 ShopBadgePosition(Vector2 slotOrigin, Vector2 viewportSize, float width = 140f, int row = 0)
    {
        // Merchant slot containers report misleading Y coordinates (the first row often reports
        // zero although its cards are centred lower on screen). X remains reliable, so follow it
        // while anchoring the two known card rows to the reference 1920x1080 shop layout.
        var scaleY = viewportSize.Y / 1080f;
        var rowY = (row <= 0 ? 180f : 570f) * scaleY;
        return new Vector2(
            Math.Clamp(slotOrigin.X - width / 2f, 8f, Math.Max(8f, viewportSize.X - width - 8f)),
            Math.Clamp(rowY, 8f, Math.Max(8f, viewportSize.Y - 50f)));
    }

    private const string MapBadgePrefix = "BuildAdvisorMapBadge";
    private const string OptionBadgePrefix = "BuildAdvisorOptionBadge";

    // H1: small labels under map points: expected damage and HP after the node along the best
    // path through it; the recommended path gets a gold frame and a star. The map stays in the
    // scene tree under combat and event rooms, so labels are shown only while isShown() is true.
    internal static void RenderMapBadges(Node map, Func<bool> isShown, IEnumerable<(Control Point, string Text, string Severity, bool Best, string Tooltip)> values)
    {
        ClearMapBadges(map);
        RenderTags(map, MapBadgePrefix, TagPlacement.Below, isShown, values.Select(x => (x.Point, (x.Best ? "★ " : "") + (string.IsNullOrWhiteSpace(x.Text) ? "путь" : x.Text), x.Severity, x.Best, x.Tooltip))
            .Where(x => !x.Item2.Equals("путь", StringComparison.Ordinal)), 104f, 26f, 13);
    }

    internal static void ClearMapBadges(Node map) => ClearTags(map, MapBadgePrefix);

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
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                TooltipText = L(tooltip)
            };
            badge.AddThemeFontSizeOverride("font_size", fontSize);
            badge.AddThemeColorOverride("font_color", color);
            var box = BadgeBox(best ? new Color("f2c94c") : new Color(color, .7f));
            box.BorderWidthLeft = box.BorderWidthRight = box.BorderWidthTop = box.BorderWidthBottom = best ? 3 : 1;
            box.CornerRadiusTopLeft = box.CornerRadiusTopRight = box.CornerRadiusBottomLeft = box.CornerRadiusBottomRight = 8;
            badge.AddThemeStyleboxOverride("normal", box);
            host.AddChild(badge);
            badge.RefreshPosition();
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

        public override void _Process(double delta)
        {
            _ = delta;
            RefreshPosition();
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

    private sealed class ShopBadgeFollower : Label
    {
        public Control? Slot { get; init; }
        public int ShopRow { get; init; }
        public bool PauseFollowing { get; set; }

        public override void _Process(double delta)
        {
            _ = delta;
            if (PauseFollowing) return;
            RefreshPosition();
        }

        public void RefreshPosition()
        {
            if (Slot is null || !GodotObject.IsInstanceValid(Slot) || !Slot.IsInsideTree())
            {
                QueueFree();
                return;
            }
            var origin = Slot.GetGlobalTransformWithCanvas() * new Vector2(Slot.Size.X / 2f, 0f);
            Position = ShopBadgePosition(origin, GetViewportRect().Size, Size.X, ShopRow);
        }
    }

    private sealed class SelectionBadgeFollower : Label
    {
        public Control? Target { get; init; }
        public Node? SelectionScreen { get; init; }
        public bool PauseFollowing { get; set; }

        public override void _Process(double delta)
        {
            _ = delta;
            if (!PauseFollowing) RefreshPosition();
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
            if (badge is ShopBadgeFollower follower) follower.PauseFollowing = true;
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
            if (badge is ShopBadgeFollower follower)
            {
                follower.PauseFollowing = false;
                follower.RefreshPosition();
            }
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
        var heading = new Label { Text = L("Карты для поиска"), Position = new Vector2(0, 0), Size = new Vector2(900, 36) };
        heading.AddThemeFontSizeOverride("font_size", 24);
        var note = new Label { Text = L("Список учитывает вашу колоду и выбранный архетип. Измените архетип сверху и нажмите «Обновить»."), Position = new Vector2(0, 38), Size = new Vector2(1500, 30) };
        note.AddThemeFontSizeOverride("font_size", 17); note.AddThemeColorOverride("font_color", new Color("a6c4d9"));
        var type = Filter("Все типы", "Атака", "Навык", "Талант", new Vector2(0, 82));
        var rarity = Filter("Любая редкость", "Обычная", "Необычная", "Редкая", new Vector2(270, 82));
        var tier = Filter("Любая метка", "S", "A", "B", "C", "D", new Vector2(540, 82));
        var sorting = Filter("Сортировка: приоритет", "Сортировка: польза колоде", "Сортировка: надёжность", "Сортировка: редкость", new Vector2(810, 82));
        var refresh = new Button { Text = L("Обновить"), Position = new Vector2(1080, 82), Size = new Vector2(180, 40) };
        refresh.AddThemeStyleboxOverride("normal", Box(new Color("1c6675"), 7));
        var resultsScroll = new ScrollContainer { Position = new Vector2(0, 140), Size = new Vector2(1620, 620), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, VerticalScrollMode = ScrollContainer.ScrollMode.Auto };
        var results = new VBoxContainer { CustomMinimumSize = new Vector2(1550, 620) };
        results.AddThemeConstantOverride("separation", 10); resultsScroll.AddChild(results);

        void Update()
        {
            var liveContext = RunContextReader.TryRead() ?? context;
            var filtered = source().Where(x => MatchesType(x.Card, type.Selected) && MatchesRarity(x.Card, rarity.Selected) && MatchesTier(x.Score.Final, tier.Selected));
            var entries = SortSearch(filtered, sorting.Selected).Take(30).ToArray();
            foreach (var child in results.GetChildren().ToArray()) { results.RemoveChild(child); child.QueueFree(); }
            var summary = new Label { Text = L($"Архетип: {ArchetypePreferences.Get(liveContext.Character)} · Найдено: {entries.Length}"), CustomMinimumSize = new Vector2(1500, 34) };
            summary.AddThemeFontSizeOverride("font_size", 20); results.AddChild(summary);
            if (entries.Length == 0)
            {
                results.AddChild(new Label { Text = L("Нет карт с такими фильтрами."), CustomMinimumSize = new Vector2(1400, 44) });
                return;
            }
            foreach (var entry in entries)
            {
                var row = new PanelContainer { CustomMinimumSize = new Vector2(1510, 180) };
                row.AddThemeStyleboxOverride("panel", Box(new Color("0b1a28"), 8));
                var content = new HBoxContainer(); content.AddThemeConstantOverride("separation", 18);
                var preview = new TextureRect { CustomMinimumSize = new Vector2(118, 170), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, TooltipText = entry.Card.Name };
                var details = new Label { Text = L($"{Tier(entry.Score.Final)}  {entry.Score.Final:F0}/100   {entry.Card.Name}\n{TypeName(entry.Card.Type)} · {RarityName(entry.Card.Rarity)} · стоимость {entry.Card.Cost}\n{ScoreExplanation.Formula(entry.Score)}\nМетки: {entry.Score.TagText}\n{entry.Score.Impact}"), AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(1340, 170), VerticalAlignment = VerticalAlignment.Center };
                details.AddThemeFontSizeOverride("font_size", 18);
                content.AddChild(preview); content.AddChild(details); row.AddChild(content); results.AddChild(row);
                _ = CardImageCache.LoadIntoAsync(preview, entry.Card.Id, entry.Card.ImageUrl);
            }
            results.CustomMinimumSize = new Vector2(1550, Math.Max(620, 50 + entries.Length * 190));
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
        var status = new Label { Text = L(text), Position = Vector2.Zero, Size = new Vector2(1500, 52), AutowrapMode = TextServer.AutowrapMode.WordSmart };
        status.AddThemeFontSizeOverride("font_size", 22);
        search.AddChild(status);
        searchRefresh = null;
        searchCharacter = null;
    }

    private static Button Tab(string name, string text, Vector2 position)
    {
        var tab = new Button { Name = name, Text = L(text), Position = position, Size = new Vector2(text == "Анализ забега" ? 140 : 122, 38) };
        tab.AddThemeStyleboxOverride("normal", Box(new Color("123a53"), 6)); return tab;
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
        var panel = new Control { Name = TagsName, Position = new Vector2(28, 118), Size = new Vector2(1644, 784), Visible = false };
        var heading = new Label { Text = L($"Справочник меток Build Advisor · {CardSemanticTags.Reference.Count} признаков"), Position = Vector2.Zero, Size = new Vector2(1100, 38) };
        heading.AddThemeFontSizeOverride("font_size", 24);
        var query = new LineEdit { PlaceholderText = L("Поиск по названию или описанию…"), Position = new Vector2(0, 48), Size = new Vector2(700, 42) };
        var category = new OptionButton { Position = new Vector2(720, 48), Size = new Vector2(320, 42) };
        var categories = CardSemanticTags.Reference.Select(x => x.Category).Distinct().OrderBy(x => x).ToArray();
        var scroll = new ScrollContainer { Position = new Vector2(0, 108), Size = new Vector2(1620, 650), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, VerticalScrollMode = ScrollContainer.ScrollMode.Auto };
        var content = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(1550, 650) };
        content.AddThemeFontSizeOverride("font_size", 18); scroll.AddChild(content);
        void Rebuild()
        {
            var needle = query.Text.Trim();
            var selected = category.Selected <= 0 ? null : categories[category.Selected - 1];
            var rows = CardSemanticTags.Reference.Where(x => (selected is null || x.Category == selected) &&
                (needle.Length == 0 || x.Label.Contains(needle, StringComparison.OrdinalIgnoreCase) || L(x.Label).Contains(needle, StringComparison.OrdinalIgnoreCase) || x.Description.Contains(needle, StringComparison.OrdinalIgnoreCase) || L(x.Description).Contains(needle, StringComparison.OrdinalIgnoreCase) || x.Key.Contains(needle, StringComparison.OrdinalIgnoreCase)))
                .GroupBy(x => x.Category).Select(group => $"{L(group.Key).ToUpperInvariant()}\n" + string.Join("\n", group.Select(x => $"  {L(x.Label)} — {L(x.Description)}")));
            content.Text = string.Join("\n\n", rows);
            content.CustomMinimumSize = new Vector2(1550, Math.Max(650, 80 + content.Text.Count(c => c == '\n') * 27));
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
    private static Texture2D? LoadEmblem()
    {
        try
        {
            var folder = Path.GetDirectoryName(typeof(AdviceOverlay).Assembly.Location);
            if (string.IsNullOrWhiteSpace(folder)) return null;
            var image = Image.LoadFromFile(Path.Combine(folder, "tandrex-emblem.png"));
            if (image is null || image.IsEmpty()) return null;
            image.Resize(68, 68, Image.Interpolation.Lanczos);
            return ImageTexture.CreateFromImage(image);
        }
        catch { return null; }
    }
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
            label.CustomMinimumSize = new Vector2(1500, HeightFor(label.Text));
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
        label.CustomMinimumSize = new Vector2(1580, HeightFor(label.Text));
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
        label.CustomMinimumSize = new Vector2(1500, HeightFor(label.Text));
    }
    private static void Relocalize(Node host)
    {
        var label = Find<Label>(host, TextName);
        if (label is not null)
        {
            label.Text = L(currentRawText);
            label.CustomMinimumSize = new Vector2(1500, HeightFor(label.Text));
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

    private sealed class LiveUpdater : Node
    {
        private double elapsed;
        private bool hotkeyDown;
        public override void _Process(double delta)
        {
            // Polled once per frame with edge detection: one press toggles once, holding the key
            // does not flicker, and no input event is consumed from the game.
            var down = Input.IsKeyPressed(PanelHotkey);
            if (down && !hotkeyDown && GetParent() is { } host) TogglePanel(host);
            hotkeyDown = down;
            elapsed += delta;
            if (elapsed < 1) return;
            elapsed = 0;
            UpdateLive(GetParent());
        }
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
    private static float HeightFor(string text) => Math.Max(760, 80 + (text.Count(c => c == '\n') + 1) * 30);
}

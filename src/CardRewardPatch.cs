using Godot;
using HarmonyLib;
using System.Reflection;
using System.Runtime.CompilerServices;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using BuildAdvisor.Data;
using BuildAdvisor.UI;

namespace BuildAdvisor.Integration;

[HarmonyPatch(typeof(NCardRewardSelectionScreen), "RefreshOptions")]
public static class CardRewardPatch
{
    private static readonly SpireCodexMetricsClient Metrics = new();
    private static readonly PickCoachClient Coach = new();
    private static readonly CardCatalogClient Catalog = new();
    private static CancellationTokenSource? activeRequest;
    private static readonly ConditionalWeakTable<NCardRewardSelectionScreen, object> AutoPicked = new();
    private static readonly object AutoPickedMarker = new();
    [HarmonyPostfix]
    public static async void AfterRefresh(object __instance, object[] __args)
    {
        if (__instance is not Node screen || __args.FirstOrDefault() is not System.Collections.IEnumerable options) return;
        activeRequest?.Cancel(); activeRequest?.Dispose();
        activeRequest = new CancellationTokenSource();
        var token = activeRequest.Token;
        var optionObjects = options.Cast<object>().ToArray();
        RunContextReader.ObserveCards(optionObjects);
        var ids = ReflectionIds.FindIds(optionObjects); if (ids.Count == 0) ids = ReflectionIds.FindIds(screen);
        if (ids.Count == 0) { AdviceOverlay.Render(screen, "BUILD ADVISOR · карты\nНе удалось прочитать ID предложенных карт. Локальный совет появится, когда игра отдаст модели карт."); return; }
        var context = RunContextReader.TryRead();
        PersonalRunDiary.RecordOffer(context, ids);
        var local = LocalDeckAdvisor.ForCards(context, ids);
        AdviceOverlay.Render(screen, "BUILD ADVISOR · карты\nЗагрузка общей статистики карт…\nЕсли сеть недоступна, совет будет рассчитан по локальной модели.", PersonalRunDiary.RecordFeedback, context);
        // The on-disk catalogue is available before any network request.  Draw tiers and
        // populate search immediately so a slow API can never hide core UI features.
        var cachedCatalog = Catalog.TryGetCached();
        if (context is not null && cachedCatalog.Count > 0)
        {
            var emptyMetrics = new Dictionary<string, EntityMetric>(StringComparer.OrdinalIgnoreCase);
            AdviceOverlay.RenderBadges(screen, DeckBuildScoring.Evaluate(context, ids, cachedCatalog, emptyMetrics).Offers, ids);
            AdviceOverlay.EnableCardSearch(screen, context, () => BuildSearchEntries(RunContextReader.TryRead() ?? context, cachedCatalog, emptyMetrics));
        }
        try
        {
            var catalogTask = Catalog.GetAsync(token);
            // Populate search as soon as the catalogue arrives; community metrics can continue
            // downloading in the background and refine scores afterwards.
            if (context is not null) _ = PopulateSearchWhenReady(screen, context, ids, catalogTask);
            var metricsTask = Metrics.GetMetricsAsync("cards", context?.Bracket ?? "a10", token);
            var coachTask = context is null ? Task.FromResult<CoachAdvice?>(null) : Coach.GetAsync(context, ids, token);
            await Task.WhenAll(catalogTask, metricsTask);
            var metrics = await metricsTask;
            if (!GodotObject.IsInstanceValid(screen)) return;
            if (context is null)
            {
                AdviceOverlay.Render(screen, "BUILD ADVISOR · карты\n" + AdviceText.ForMetrics("Статистика сообщества", ids, metrics), null, context);
                return;
            }
            var catalog = await catalogTask;
            // Pick Coach is enrichment only. Never make the first advice wait for it.
            var coach = coachTask.IsCompletedSuccessfully ? coachTask.Result : null;
            var coachPending = !coachTask.IsCompleted;
            var coachMap = coach?.Offers.ToDictionary(x => SpireCodexMetricsClient.NormalizeId(x.Id), StringComparer.OrdinalIgnoreCase);
            var assessment = DeckBuildScoring.Evaluate(context, ids, catalog, metrics, coachMap);
            var text = FormatAdvice(context, assessment, metrics, coach, coachPending);
            AdviceOverlay.RenderBadges(screen, assessment.Offers, ids);
            AdviceOverlay.Render(screen, text, null, context);
            AdviceOverlay.SetRefreshAction(async () =>
            {
                if (!GodotObject.IsInstanceValid(screen)) return;
                try
                {
                    // A language switch changes the Spire Codex catalogue. Re-read it instead
                    // of rebuilding advice with the Russian catalogue captured by this closure.
                    var liveContext = RunContextReader.TryRead() ?? context;
                    var localizedCatalog = await Catalog.GetAsync();
                    if (!GodotObject.IsInstanceValid(screen)) return;
                    var refreshed = DeckBuildScoring.Evaluate(liveContext, ids, localizedCatalog, metrics, coachMap);
                    AdviceOverlay.RenderBadges(screen, refreshed.Offers, ids);
                    AdviceOverlay.Render(screen, FormatAdvice(liveContext, refreshed, metrics, coach, false), null, liveContext);
                    AdviceOverlay.EnableCardSearch(screen, liveContext, () => BuildSearchEntries(RunContextReader.TryRead() ?? liveContext, localizedCatalog, metrics));
                }
                catch (Exception error) { GD.PrintErr("Build Advisor language refresh: " + error); }
            });
            AdviceOverlay.EnableCardSearch(screen, context, () => BuildSearchEntries(RunContextReader.TryRead() ?? context, catalog, metrics));
            _ = TryAutoPickAsync((NCardRewardSelectionScreen)screen, optionObjects, assessment, token);
            if (coachPending) _ = ApplyCoachWhenReady(screen, context, ids, catalog, metrics, coachTask, token);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (GodotObject.IsInstanceValid(screen)) AdviceOverlay.Render(screen, "BUILD ADVISOR · карты\n" + local + "\n\nСетевые данные недоступны — показан локальный совет.", null, context); GD.PrintErr($"Build Advisor: {error}"); }
    }

    private static async Task TryAutoPickAsync(NCardRewardSelectionScreen screen, IReadOnlyList<object> options, BuildAssessment assessment, CancellationToken token)
    {
        if (!AutoPickSettings.Enabled || !AutoPickSettings.ShouldPick(assessment) || AutoPicked.TryGetValue(screen, out _)) return;
        var best = assessment.Offers.OrderByDescending(x => x.Final).FirstOrDefault();
        if (best is null) return;
        var option = options.FirstOrDefault(item => string.Equals(SpireCodexMetricsClient.NormalizeId(ReflectionIds.FindId(item) ?? ""), SpireCodexMetricsClient.NormalizeId(best.Id), StringComparison.OrdinalIgnoreCase));
        if (option is null || ReflectionIds.Member(option, "Card") is not CardModel card) return;

        AutoPicked.Add(screen, AutoPickedMarker);
        try
        {
            // Lets the player see both the result and the choice; cancellation protects screen changes.
            await Task.Delay(900, token);
            if (token.IsCancellationRequested || !AutoPickSettings.Enabled || !GodotObject.IsInstanceValid(screen) || !screen.IsInsideTree()) return;
            var holder = screen.GetCardHolder(card);
            if (holder is null) return;
            // SelectCard is intentionally private in the game UI. Invoking that same handler
            // preserves its normal reward bookkeeping instead of simulating a mouse click.
            var select = screen.GetType().GetMethod("SelectCard", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            select?.Invoke(screen, [holder]);
            GD.Print($"Build Advisor auto-picked {best.Id} ({best.Final:F0}/100).");
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { GD.PrintErr("Build Advisor auto-pick: " + error); }
    }

    private static async Task ApplyCoachWhenReady(Node screen, RunContext context, IReadOnlyList<string> ids,
        IReadOnlyDictionary<string, CardDefinition> catalog, IReadOnlyDictionary<string, EntityMetric> metrics,
        Task<CoachAdvice?> coachTask, CancellationToken token)
    {
        try
        {
            var coach = await coachTask;
            token.ThrowIfCancellationRequested();
            if (!GodotObject.IsInstanceValid(screen) || !screen.IsInsideTree()) return;
            // The player may have changed language while Pick Coach was in flight.
            catalog = await Catalog.GetAsync(token);
            var coachMap = coach?.Offers.ToDictionary(x => SpireCodexMetricsClient.NormalizeId(x.Id), StringComparer.OrdinalIgnoreCase);
            var assessment = DeckBuildScoring.Evaluate(RunContextReader.TryRead() ?? context, ids, catalog, metrics, coachMap);
            AdviceOverlay.RenderBadges(screen, assessment.Offers, ids);
            AdviceOverlay.Render(screen, FormatAdvice(context, assessment, metrics, coach, false), null, context);
            GD.Print("Build Advisor Pick Coach: " + (coach is null ? "no matching build" : $"loaded {coach.Archetype}, {coach.Offers.Count} offers"));
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { GD.PrintErr("Build Advisor Pick Coach: " + error); }
    }

    private static async Task PopulateSearchWhenReady(Node screen, RunContext context, IReadOnlyList<string> ids, Task<IReadOnlyDictionary<string, CardDefinition>> catalogTask)
    {
        try
        {
            var catalog = await catalogTask;
            var emptyMetrics = new Dictionary<string, EntityMetric>(StringComparer.OrdinalIgnoreCase);
            AdviceOverlay.EnableCardSearch(screen, context, () => BuildSearchEntries(RunContextReader.TryRead() ?? context, catalog, emptyMetrics));
            if (GodotObject.IsInstanceValid(screen)) AdviceOverlay.RenderBadges(screen, DeckBuildScoring.Evaluate(context, ids, catalog, emptyMetrics).Offers, ids);
        }
        catch { /* The main request shows the fallback if the catalogue is unavailable. */ }
    }

    private static string FormatAdvice(RunContext context, BuildAssessment assessment, IReadOnlyDictionary<string, EntityMetric> metrics, CoachAdvice? coach, bool coachPending = false)
    {
        var lines = assessment.Offers.Select(x =>
        {
            var personal = PersonalRunDiary.ChoiceCount(x.Id);
            return $"{x.Name} [{x.Role}]: {x.Final:F0}/100" +
                   (personal > 0 ? $" · брали раньше: {personal}" : "") +
                   $"\n  {ScoreExplanation.Formula(x)}\n  Метки: {x.TagText}\n  {x.Impact}";
        });
        _ = metrics;
        return "BUILD ADVISOR · карты\n" + assessment.Summary +
               "\n\nОценка вариантов:\n" + string.Join("\n", lines) +
               $"\n\nВероятность пропуска: {assessment.SkipScore}% — " + (assessment.SkipScore >= RunHistoryInsights.SkipThreshold * 100m ? "похожие решения чаще указывают на пропуск." : "похожие решения чаще указывают на выбор карты.") +
               "\n\n" + DeckActionPlanner.Create(context, assessment, CardRewardPatch.Catalog.TryGetCached()) +
               (coachPending ? "\nПохожие победные колоды ещё проверяются…" : coach is null ? "\nПохожая победная колода не найдена — использована базовая модель." : $"\nНайдена похожая победная колода: {coach.Archetype}; сравнено вариантов: {coach.Offers.Count}.") +
               "\nБаза — общая сила карты по статистике и нашей модели. Поправки в скобках показывают влияние именно на текущую колоду.";
    }

    internal static IReadOnlyList<AdviceOverlay.CardSearchEntry> BuildSearchEntries(RunContext context, IReadOnlyDictionary<string, CardDefinition> catalog, IReadOnlyDictionary<string, EntityMetric> metrics)
    {
        var candidates = catalog.Values.GroupBy(card => SpireCodexMetricsClient.NormalizeId(card.Id), StringComparer.OrdinalIgnoreCase).Select(group => group.First())
            .Where(card => BelongsToCharacter(card, context)).ToArray();
        var assessment = DeckBuildScoring.Evaluate(context, candidates.Select(card => card.Id), catalog, metrics);
        var definitions = candidates.ToDictionary(card => SpireCodexMetricsClient.NormalizeId(card.Id), StringComparer.OrdinalIgnoreCase);
        return assessment.Offers.Where(score => definitions.TryGetValue(SpireCodexMetricsClient.NormalizeId(score.Id), out _))
            .Select(score => new AdviceOverlay.CardSearchEntry(definitions[SpireCodexMetricsClient.NormalizeId(score.Id)], score)).ToArray();
    }

    private static bool BelongsToCharacter(CardDefinition card, RunContext context)
    {
        var color = card.Character.ToLowerInvariant();
        return context.Character switch
        {
            "ironclad" => color.Contains("ironclad") || color.Contains("red"),
            "silent" => color.Contains("silent") || color.Contains("green"),
            "defect" => color.Contains("defect") || color.Contains("blue"),
            "regent" => color.Contains("regent") ||
                        (ArchetypePreferences.Get(context.Character) == "Бесцветные карты" && color.Contains("colorless")),
            "necrobinder" => color.Contains("necrobinder"),
            _ => false
        };
    }
}

[HarmonyPatch(typeof(NCardRewardSelectionScreen), "_ExitTree")]
public static class CardRewardExitBadgeCleanup
{
    [HarmonyPrefix]
    public static void BeforeExit(Node __instance)
    {
        AdviceOverlay.ClearBadges(__instance);
        PersonalRunDiary.CloseOffer();
    }
}

using BuildAdvisor.Data;

namespace BuildAdvisor.Integration;

internal sealed record ShopItem(string Id, int Price);

internal static class ShopPlanBuilder
{
    public static string Create(RunContext context, IReadOnlyList<ShopItem> cards, IReadOnlyList<ShopItem> relics,
        IReadOnlyDictionary<string, CardDefinition> catalog, IReadOnlyDictionary<string, EntityMetric> cardMetrics,
        IReadOnlyDictionary<string, EntityMetric> relicMetrics, IReadOnlyDictionary<string, string>? relicNames = null,
        IReadOnlyList<ShopItem>? potions = null, IReadOnlyDictionary<string, EntityMetric>? potionMetrics = null, IReadOnlyDictionary<string, string>? potionNames = null,
        int removalPrice = 0)
    {
        var cardAssessment = DeckBuildScoring.Evaluate(context, cards.Select(x => x.Id), catalog, cardMetrics);
        var alternatives = DeckActionPlanner.Evaluate(context, cardAssessment, catalog);
        var scoredCards = cardAssessment.Offers.Select(score =>
        {
            var item = cards.First(x => SpireCodexMetricsClient.NormalizeId(x.Id) == SpireCodexMetricsClient.NormalizeId(score.Id));
            return (Id: score.Id, score.Name, Score: score.Final, item.Price, Formula: ScoreExplanation.Formula(score), score.Impact, WinDelta: TakeSkipModel.WinDelta(score));
        }).ToArray();
        var cardLines = scoredCards.OrderByDescending(x => Value(x.Score, x.Price)).Take(3).Select(x =>
            $"{x.Name}: {x.Score:F0}/100 · {TakeSkipModel.Format(x.WinDelta)} п.п. к победе" + (x.Price > 0 ? $" · {x.Price} золота" : " · цена не прочитана") + $"\n  {x.Formula}\n  {x.Impact}");
        var scoredRelics = relics.Select(item => ScoreRelic(item, context, relicMetrics, relicNames)).ToArray();
        var relicLines = scoredRelics.OrderByDescending(x => Value(x.Score, x.Price)).Take(3).Select(x =>
            $"{x.Name}: {x.Score:F0}/100" + (x.Price > 0 ? $" · {x.Price} золота" : " · цена не прочитана") + $" — {x.Reason}");
        var removalAdvice = DeckActionPlanner.Removal(context, catalog);
        var removal = removalAdvice.Target is null
            ? "Удаление: " + removalAdvice.Reason + "."
            : $"Удаление ({removalAdvice.Tier}): {removalAdvice.Target} — {removalAdvice.Reason}" + (removalPrice > 0 ? $" · цена {removalPrice}." : ".");
        var budget = context.Gold > 0 ? $"Бюджет: {context.Gold} золота." : "Золото не прочитано.";
        var reserved = context.Gold - BasketBudget(context.Gold, removalAdvice, removalPrice);
        var basket = BestBasket(context.Gold,
            scoredCards.Select(x => (x.Name, x.Score, x.Price)).Concat(scoredRelics.Select(x => (x.Name, x.Score, x.Price))).ToArray(), reserved);
        var actionComparison = string.Join("; ", alternatives.OrderByDescending(x => x.Delta).Take(4)
            .Select(x => $"{ActionLabel(x)} Δ{x.Delta:+0;-0;0}"));
        return "План магазина\n" + budget + "\n" + removal + "\n" + basket + "\nСравнение действий: " + actionComparison +
               "\n\nКарты (лучшее соотношение пользы и цены):\n" + string.Join("\n", cardLines) +
               "\n\nРеликвии:\n" + string.Join("\n", relicLines) +
               "\n\n" + (potions is { Count: > 0 }
                   ? PotionAdvisor.Format(context, PotionAdvisor.Rate(context, potions.Select(x => (x.Id, x.Price)).ToArray(),
                       potionMetrics ?? new Dictionary<string, EntityMetric>(), potionNames, SegmentRiskModel.Assess(context) is { IsAvailable: true } risk ? risk.Probability : null))
                   : "Зелья: берите перед элитой/боссом, если они закрывают текущий риск; иначе сохраните золото на ключевую покупку.");
    }

    private static string ActionLabel(PlannedAction action) => action.Kind switch
    {
        AdvisorActionKind.Take => "купить " + action.Target,
        AdvisorActionKind.Skip => "не покупать карту",
        AdvisorActionKind.Remove => "удалить " + action.Target,
        AdvisorActionKind.Upgrade => "улучшить " + action.Target,
        AdvisorActionKind.Rest => "отдых",
        _ => "сохранить золото"
    };

    private static decimal Value(decimal score, int price) => price > 0 ? Math.Max(0m, score - 45m) / price : 0m;

    // 7.1: the basket is data as well as text: auto-pick buys exactly what the plan shows.
    internal static IReadOnlyList<int> BasketIndices(int gold, IReadOnlyList<(string Name, decimal Score, int Price)> source)
    {
        var candidates = source.Select((x, i) => (Item: x, Index: i)).Where(x => x.Item.Price > 0 && x.Item.Price <= gold && x.Item.Score >= 55m)
            .OrderByDescending(x => x.Item.Score).Take(12).ToArray();
        if (gold <= 0 || candidates.Length == 0) return [];
        decimal bestUtility = 0; int bestCost = 0; int bestMask = 0;
        for (var mask = 1; mask < 1 << candidates.Length; mask++)
        {
            var cost = 0; var utility = 0m;
            for (var i = 0; i < candidates.Length; i++) if ((mask & (1 << i)) != 0)
            {
                cost += candidates[i].Item.Price;
                utility += candidates[i].Item.Score - 50m;
            }
            if (cost <= gold && (utility > bestUtility || utility == bestUtility && cost < bestCost))
            { bestUtility = utility; bestCost = cost; bestMask = mask; }
        }
        return Enumerable.Range(0, candidates.Length).Where(i => (bestMask & (1 << i)) != 0)
            .Select(i => candidates[i]).OrderByDescending(x => x.Item.Score).Select(x => x.Index).ToArray();
    }

    // Gold the basket may spend: a high-priority removal (tier S/A) that fits the budget is paid first.
    internal static int BasketBudget(int gold, DeckActionPlanner.RemovalAdvice? removal, int removalPrice) =>
        removal is { Tier: "S" or "A" } && removalPrice > 0 && removalPrice <= gold ? gold - removalPrice : gold;

    private static string BestBasket(int gold, IReadOnlyList<(string Name, decimal Score, int Price)> source, int reserved = 0)
    {
        var budget = gold - reserved;
        var selected = BasketIndices(budget, source).Select(i => source[i]).ToArray();
        if (gold <= 0 || !source.Any(x => x.Price > 0 && x.Price <= budget && x.Score >= 55m))
            return "Корзина: нет уверенной покупки с прочитанной ценой в текущий бюджет." + (reserved > 0 ? $" {reserved} золота отложено на удаление." : "");
        if (selected.Length == 0) return "Корзина: лучше сохранить золото.";
        var cost = selected.Sum(x => x.Price);
        return $"Оптимальная корзина по текущей модели: {string.Join(" + ", selected.Select(x => x.Name))} · {cost}/{gold} золота · останется {gold - cost}" +
               (reserved > 0 ? $" (из них {reserved} — на удаление)." : ".");
    }

    // The items the basket buys, by shop id, in purchase order (best first).
    internal static IReadOnlyList<string> BasketIds(RunContext context, IReadOnlyList<ShopItem> cards, IReadOnlyList<ShopItem> relics,
        IReadOnlyDictionary<string, CardDefinition> catalog, IReadOnlyDictionary<string, EntityMetric> cardMetrics,
        IReadOnlyDictionary<string, EntityMetric> relicMetrics, IReadOnlyDictionary<string, string>? relicNames, int removalPrice = 0)
    {
        var items = Candidates(context, cards, relics, catalog, cardMetrics, relicMetrics, relicNames);
        var budget = BasketBudget(context.Gold, DeckActionPlanner.Removal(context, catalog), removalPrice);
        return BasketIndices(budget, items.Select(x => (x.Name, x.Score, x.Price)).ToArray()).Select(i => items[i].Id).ToArray();
    }

    private static IReadOnlyList<(string Id, string Name, decimal Score, int Price)> Candidates(RunContext context, IReadOnlyList<ShopItem> cards, IReadOnlyList<ShopItem> relics,
        IReadOnlyDictionary<string, CardDefinition> catalog, IReadOnlyDictionary<string, EntityMetric> cardMetrics,
        IReadOnlyDictionary<string, EntityMetric> relicMetrics, IReadOnlyDictionary<string, string>? relicNames)
    {
        var cardAssessment = DeckBuildScoring.Evaluate(context, cards.Select(x => x.Id), catalog, cardMetrics);
        var scoredCards = cardAssessment.Offers.Select(score => (score.Id, score.Name, Score: score.Final,
            cards.First(x => SpireCodexMetricsClient.NormalizeId(x.Id) == SpireCodexMetricsClient.NormalizeId(score.Id)).Price));
        var scoredRelics = relics.Select(item => { var r = ScoreRelic(item, context, relicMetrics, relicNames); return (item.Id, r.Name, r.Score, r.Price); });
        return scoredCards.Concat(scoredRelics).ToArray();
    }

    private static (string Name, decimal Score, int Price, string Reason) ScoreRelic(ShopItem item, RunContext context, IReadOnlyDictionary<string, EntityMetric> metrics, IReadOnlyDictionary<string, string>? names)
    {
        // M9: one relic score for the shop, the relic choice screen and Ancient event options.
        var scored = RelicAdvisor.Score(context, item.Id, metrics, names);
        var reason = scored.Reason;
        if (metrics.TryGetValue(SpireCodexMetricsClient.NormalizeId(item.Id), out var metric)) reason += BuildAdvisor.UI.AdviceText.Lift(metric);
        return (scored.Name, scored.Final, item.Price, reason);
    }
}

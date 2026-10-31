using BuildAdvisor.Data;

namespace BuildAdvisor.Integration;

internal sealed record ShopItem(string Id, int Price);

internal static class ShopPlanBuilder
{
    public static string Create(RunContext context, IReadOnlyList<ShopItem> cards, IReadOnlyList<ShopItem> relics,
        IReadOnlyDictionary<string, CardDefinition> catalog, IReadOnlyDictionary<string, EntityMetric> cardMetrics,
        IReadOnlyDictionary<string, EntityMetric> relicMetrics, IReadOnlyDictionary<string, string>? relicNames = null,
        IReadOnlyList<ShopItem>? potions = null, IReadOnlyDictionary<string, EntityMetric>? potionMetrics = null, IReadOnlyDictionary<string, string>? potionNames = null)
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
        var removal = context.Cards.Count >= 15 && context.Cards.Any(id => IsStarterCard(id, catalog))
            ? "Удаление: высокий приоритет — колода уже большая, базовая карта снижает качество добора."
            : "Удаление: средний приоритет; не жертвуйте ключевой покупкой без явного слабого базового слота.";
        var budget = context.Gold > 0 ? $"Бюджет: {context.Gold} золота." : "Золото не прочитано.";
        var basket = BestBasket(context.Gold,
            scoredCards.Select(x => (x.Name, x.Score, x.Price)).Concat(scoredRelics.Select(x => (x.Name, x.Score, x.Price))).ToArray());
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

    private static string BestBasket(int gold, IReadOnlyList<(string Name, decimal Score, int Price)> source)
    {
        var items = source.Where(x => x.Price > 0 && x.Price <= gold && x.Score >= 55m).Take(12).ToArray();
        if (gold <= 0 || items.Length == 0) return "Корзина: нет уверенной покупки с прочитанной ценой в текущий бюджет.";
        decimal bestUtility = 0; int bestCost = 0; int bestMask = 0;
        for (var mask = 1; mask < 1 << items.Length; mask++)
        {
            var cost = 0; var utility = 0m;
            for (var i = 0; i < items.Length; i++) if ((mask & (1 << i)) != 0)
            {
                cost += items[i].Price;
                utility += items[i].Score - 50m;
            }
            if (cost <= gold && (utility > bestUtility || utility == bestUtility && cost < bestCost))
            { bestUtility = utility; bestCost = cost; bestMask = mask; }
        }
        if (bestMask == 0) return "Корзина: лучше сохранить золото.";
        var selected = Enumerable.Range(0, items.Length).Where(i => (bestMask & (1 << i)) != 0).Select(i => items[i].Name);
        return $"Оптимальная корзина по текущей модели: {string.Join(" + ", selected)} · {bestCost}/{gold} золота · останется {gold - bestCost}.";
    }

    private static (string Name, decimal Score, int Price, string Reason) ScoreRelic(ShopItem item, RunContext context, IReadOnlyDictionary<string, EntityMetric> metrics, IReadOnlyDictionary<string, string>? names)
    {
        // M9: one relic score for the shop, the relic choice screen and Ancient event options.
        var scored = RelicAdvisor.Score(context, item.Id, metrics, names);
        var reason = scored.Reason;
        if (metrics.TryGetValue(SpireCodexMetricsClient.NormalizeId(item.Id), out var metric)) reason += BuildAdvisor.UI.AdviceText.Lift(metric);
        return (scored.Name, scored.Final, item.Price, reason);
    }
    private static bool IsStarterCard(string id, IReadOnlyDictionary<string, CardDefinition> catalog) =>
        catalog.TryGetValue(SpireCodexMetricsClient.NormalizeId(id), out var card)
            ? CardTaxonomy.IsStarterStrikeOrDefend(card)
            : SpireCodexMetricsClient.NormalizeId(id) is "STRIKE" or "DEFEND" ||
              SpireCodexMetricsClient.NormalizeId(id).StartsWith("STRIKE_", StringComparison.Ordinal) ||
              SpireCodexMetricsClient.NormalizeId(id).StartsWith("DEFEND_", StringComparison.Ordinal);
}

using BuildAdvisor.Data;

namespace BuildAdvisor.Integration;

internal sealed record ShopItem(string Id, int Price);

internal static class ShopPlanBuilder
{
    public static string Create(RunContext context, IReadOnlyList<ShopItem> cards, IReadOnlyList<ShopItem> relics,
        IReadOnlyDictionary<string, CardDefinition> catalog, IReadOnlyDictionary<string, EntityMetric> cardMetrics,
        IReadOnlyDictionary<string, EntityMetric> relicMetrics, IReadOnlyDictionary<string, string>? relicNames = null)
    {
        var cardAssessment = DeckBuildScoring.Evaluate(context, cards.Select(x => x.Id), catalog, cardMetrics);
        var alternatives = DeckActionPlanner.Evaluate(context, cardAssessment, catalog);
        var scoredCards = cardAssessment.Offers.Select(score =>
        {
            var item = cards.First(x => SpireCodexMetricsClient.NormalizeId(x.Id) == SpireCodexMetricsClient.NormalizeId(score.Id));
            return (Id: score.Id, score.Name, Score: score.Final, item.Price, Formula: ScoreExplanation.Formula(score), score.Impact);
        }).ToArray();
        var cardLines = scoredCards.OrderByDescending(x => Value(x.Score, x.Price)).Take(3).Select(x =>
            $"{x.Name}: {x.Score:F0}/100 · Δ{x.Score - cardAssessment.SkipScore:+0;-0;0}" + (x.Price > 0 ? $" · {x.Price} золота" : " · цена не прочитана") + $"\n  {x.Formula}\n  {x.Impact}");
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
               "\n\nЗелья: берите перед элитой/боссом, если они закрывают текущий риск; иначе сохраните золото на ключевую покупку.";
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
        var key = SpireCodexMetricsClient.NormalizeId(item.Id);
        var metric = metrics.TryGetValue(key, out var found) ? found : null;
        var score = metric is null ? 50m : metric.CodexScore ?? Math.Clamp(50m + (metric.WinRate - .5m) * 100m, 0m, 100m);
        var reason = "общая ценность по данным сообщества";
        if (context.AverageCost >= 1.7m && RelicKnowledge.GivesEnergy(item.Id)) { score += 24; reason = "решает дефицит энергии дорогой колоды"; }
        else if (context.Hp > 0 && context.Hp * 3 < context.MaxHp && RelicKnowledge.HelpsSurvival(item.Id)) { score += 16; reason = "поддерживает выживаемость при низком HP"; }
        else if (context.Gold < 100 && RelicKnowledge.HelpsEconomy(item.Id)) { score += 10; reason = "улучшает экономику следующих магазинов"; }
        var displayName = names is not null && names.TryGetValue(key, out var localized) ? localized : metric?.Name ?? item.Id;
        return (displayName, Math.Clamp(score, 0m, 100m), item.Price, reason);
    }
    private static bool IsStarterCard(string id, IReadOnlyDictionary<string, CardDefinition> catalog) =>
        catalog.TryGetValue(SpireCodexMetricsClient.NormalizeId(id), out var card)
            ? CardTaxonomy.IsStarterStrikeOrDefend(card)
            : SpireCodexMetricsClient.NormalizeId(id) is "STRIKE" or "DEFEND" ||
              SpireCodexMetricsClient.NormalizeId(id).StartsWith("STRIKE_", StringComparison.Ordinal) ||
              SpireCodexMetricsClient.NormalizeId(id).StartsWith("DEFEND_", StringComparison.Ordinal);
}

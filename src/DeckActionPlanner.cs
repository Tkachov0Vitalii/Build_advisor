using BuildAdvisor.Data;

namespace BuildAdvisor.Integration;

// One compact counterfactual plan. These are deterministic alternatives now; the 200k corpus
// will later replace their thresholds with held-out estimates of the value of each action.
internal enum AdvisorActionKind { Take, Skip, Remove, Upgrade, Rest, SaveGold }
internal sealed record PlannedAction(AdvisorActionKind Kind, string Target, decimal Delta, string Reason);

internal static class DeckActionPlanner
{
    public static string Create(RunContext context, BuildAssessment assessment, IReadOnlyDictionary<string, CardDefinition> catalog)
    {
        var actions = Evaluate(context, assessment, catalog);
        var best = assessment.Offers.OrderByDescending(x => x.Final).FirstOrDefault();
        var confidence = DecisionConfidenceModel.Evaluate(assessment);
        var reward = TakeSkipModel.Evaluate(assessment).Text;
        var remove = RemovalCandidate(context.Cards, catalog);
        var upgrade = UpgradeCandidate(context, catalog);
        var gold = remove is not null && context.Gold < 150
            ? "сохранять золото на удаление слабой карты"
            : "сохранять золото, если покупка не закрывает явную потребность";
        var campfire = actions.Where(x => x.Kind is AdvisorActionKind.Rest or AdvisorActionKind.Upgrade)
            .OrderByDescending(x => x.Delta).FirstOrDefault();
        return "План следующих действий: " + reward +
               $" · удалить: {remove ?? "нет очевидной цели"}" +
               $" · улучшить: {upgrade ?? "решать по ближайшему бою"}" +
               $" · костёр: {(campfire is null ? "решать по ближайшему бою" : campfire.Kind == AdvisorActionKind.Rest ? "отдых" : "улучшение " + campfire.Target)}" +
               $" · золото: {gold}.\nСравнение действий: {string.Join("; ", actions.OrderByDescending(x => x.Delta).Take(5).Select(x => $"{ActionName(x)} Δ{x.Delta:+0;-0;0}"))}." +
               $"\nУверенность: {confidence.Label} — {confidence.Explanation}.";
    }

    internal static IReadOnlyList<PlannedAction> Evaluate(RunContext context, BuildAssessment assessment, IReadOnlyDictionary<string, CardDefinition> catalog)
    {
        var result = new List<PlannedAction>();
        var verdict = TakeSkipModel.Evaluate(assessment);
        result.Add(new(AdvisorActionKind.Skip, "", 0m, "не меняет колоду"));
        // The other actions are scored in 0-100 points, so the take value (percentage points of
        // win rate) is converted back with the same slope that produced it.
        if (verdict.Best is not null) result.Add(new(AdvisorActionKind.Take, verdict.Best.Name, decimal.Round(verdict.WinDelta / TakeSkipModel.PointsPerScore, 0), "ценность карты относительно пропуска"));
        var remove = RemovalCandidate(context.Cards, catalog);
        if (remove is not null)
        {
            var excess = Math.Max(0, context.Cards.Count - RunHistoryInsights.ExpectedDeckSize(context.Character, context.Floor));
            result.Add(new(AdvisorActionKind.Remove, remove, 10m + Math.Min(18m, excess * 2m), "улучшает качество добора"));
        }
        var upgrade = UpgradeCard(context, catalog);
        if (upgrade is not null) result.Add(new(AdvisorActionKind.Upgrade, upgrade.Name, Math.Clamp(UpgradePriority(upgrade) * 2m, 4m, 24m), "усиливает существующую карту без увеличения колоды"));
        var missingHp = context.MaxHp <= 0 ? 0m : Math.Max(0m, (decimal)(context.MaxHp - context.Hp) / context.MaxHp);
        var routePressure = context.Route.Options.Select(x => x.Pressure).DefaultIfEmpty(0).Max();
        var segmentRisk = SegmentRiskModel.Assess(context);
        var learnedRisk = segmentRisk.IsAvailable ? segmentRisk.High * 16m : 0m;
        result.Add(new(AdvisorActionKind.Rest, "", Math.Clamp(missingHp * 35m + Math.Max(0, routePressure) + learnedRisk, 0m, 40m), "снижает риск ближайшего участка"));
        result.Add(new(AdvisorActionKind.SaveGold, "", context.Gold < 150 ? 7m : 3m, "сохраняет будущую гибкость"));
        return result;
    }

    private static string ActionName(PlannedAction action) => action.Kind switch
    {
        AdvisorActionKind.Take => "взять " + action.Target,
        AdvisorActionKind.Skip => "пропустить",
        AdvisorActionKind.Remove => "удалить " + action.Target,
        AdvisorActionKind.Upgrade => "улучшить " + action.Target,
        AdvisorActionKind.Rest => "отдых",
        _ => "сохранить золото"
    };

    private static string? RemovalCandidate(IEnumerable<string> ids, IReadOnlyDictionary<string, CardDefinition> catalog)
    {
        var cards = ids.Select(id => catalog.TryGetValue(SpireCodexMetricsClient.NormalizeId(id), out var card) ? card : null).Where(x => x is not null).Cast<CardDefinition>().ToArray();
        var curse = cards.FirstOrDefault(x => x.Type.Contains("curse", StringComparison.OrdinalIgnoreCase) || x.Type.Contains("status", StringComparison.OrdinalIgnoreCase));
        if (curse is not null) return curse.Name;
        var strikes = cards.Where(x => CardTaxonomy.IsStarterCopy(x, "STRIKE")).ToArray();
        var defends = cards.Where(x => CardTaxonomy.IsStarterCopy(x, "DEFEND")).ToArray();
        if (strikes.Length > 0 && cards.Count(x => x.Damage > 0) >= 5) return strikes[0].Name;
        if (defends.Length > 0 && cards.Count(x => x.Block > 0) >= 5) return defends[0].Name;
        return null;
    }

    private static string? UpgradeCandidate(RunContext context, IReadOnlyDictionary<string, CardDefinition> catalog)
        => UpgradeCard(context, catalog)?.Name;

    private static CardDefinition? UpgradeCard(RunContext context, IReadOnlyDictionary<string, CardDefinition> catalog)
    {
        // Runtime state is authoritative. If it is unavailable (for an offline fixture), retain
        // the old ID-only fallback so historical evaluations remain reproducible.
        var ids = context.CardStates.Count > 0
            ? context.CardStates.Where(x => x.CanUpgrade).Select(x => x.Id)
            : context.Cards;
        return ids
        .Select(id => catalog.TryGetValue(SpireCodexMetricsClient.NormalizeId(id), out var card) ? card : null)
        .Where(x => x is not null && !CardTaxonomy.IsStarterStrikeOrDefend(x))
        .Cast<CardDefinition>()
        .OrderByDescending(x => UpgradePriority(x))
        .FirstOrDefault();
    }

    // M5: the catalogue says what the upgrade changes. A cost cut or extra draw outranks a +3
    // damage bump; card role (power, rarity) still breaks ties.
    private static int UpgradePriority(CardDefinition card) =>
        (int)Math.Round(CardUpgrades.Value(card)) +
        (card.Type.Contains("power", StringComparison.OrdinalIgnoreCase) ? 3 : 0) +
        (CardTaxonomy.RarityName(card.Rarity) == "Редкая" ? 2 : 0);
}

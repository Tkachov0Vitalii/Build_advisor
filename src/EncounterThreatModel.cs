using BuildAdvisor.Data;

namespace BuildAdvisor.Integration;

internal sealed record ThreatAssessment(string Name, IReadOnlyList<string> Priorities, string Explanation)
{
    public int RoutePressure { get; init; }
    public string NextNode { get; init; } = "unknown";
    public IReadOnlyList<string> RouteChoices { get; init; } = [];
}

// Transparent baseline until the encounter-level corpus is trained. Boss ids come from the
// live run state; bonuses are intentionally small so they refine, never replace, card quality.
internal static class EncounterThreatModel
{
    public static ThreatAssessment Assess(RunContext context)
    {
        var route = context.Route;
        var choices = route.Options.Select(option => string.Join(" → ", option.Nodes)).ToArray();
        var next = route.Options.OrderByDescending(x => x.Pressure).FirstOrDefault()?.Next ?? "unknown";
        var pressure = route.Options.Select(x => x.Pressure).DefaultIfEmpty(0).Max();
        if (string.IsNullOrWhiteSpace(context.Boss))
        {
            var priorities = next is "elite" or "boss" ? new[] { "быстрый урон", "защита" } : Array.Empty<string>();
            return new("босс ещё неизвестен", priorities, route.IsKnown ? "маршрут прочитан; профиль босса ещё неизвестен" : "маршрут и ближайшая угроза пока не прочитаны")
            { RoutePressure = pressure, NextNode = next, RouteChoices = choices };
        }
        var boss = context.Boss.ToUpperInvariant();
        if (Has(boss, "KIN", "KAISER_CRAB", "QUEEN"))
            return WithRoute(new(context.Boss, ["массовый урон", "быстрый урон", "защита"], "бой с несколькими целями требует темпа и устойчивости"), route, pressure, next, choices);
        if (Has(boss, "WATERFALL_GIANT", "LAGAVULIN_MATRIARCH", "INSATIABLE", "AEONGLASS"))
            return WithRoute(new(context.Boss, ["масштабирование", "защита", "добор"], "затяжной бой проверяет рост силы и стабильность колоды"), route, pressure, next, choices);
        if (Has(boss, "VANTOM", "KNOWLEDGE_DEMON", "TEST_SUBJECT", "SOUL_FYSH"))
            return WithRoute(new(context.Boss, ["добор", "защита", "быстрый урон"], "помехи и давление требуют стабильных рук"), route, pressure, next, choices);
        return WithRoute(new(context.Boss, ["защита", "быстрый урон"], "универсальная подготовка к неизвестному профилю боя"), route, pressure, next, choices);
    }

    public static decimal Adjustment(CardDefinition card, ThreatAssessment threat)
    {
        if (threat.Priorities.Count == 0) return 0m;
        var text = (card.Id + " " + card.Description).ToUpperInvariant();
        var matches = 0;
        if (threat.Priorities.Contains("массовый урон") && (text.Contains("ALL ENEM") || text.Contains("ВСЕМ ВРАГ"))) matches++;
        if (threat.Priorities.Contains("быстрый урон") && card.Damage > 0 && card.Cost <= 2) matches++;
        if (threat.Priorities.Contains("защита") && card.Block > 0) matches++;
        if (threat.Priorities.Contains("добор") && card.Draw > 0) matches++;
        if (threat.Priorities.Contains("масштабирование") &&
            (card.Type.Contains("power", StringComparison.OrdinalIgnoreCase) || Has(text, "EACH TURN", "WHENEVER", "FOCUS", "STRENGTH", "КАЖДЫЙ ХОД", "ФОКУС", "СИЛА"))) matches++;
        return Math.Min(6m, matches * 3m);
    }

    public static string Summary(ThreatAssessment threat)
    {
        var route = threat.RouteChoices.Count == 0 ? "" : $" Маршрут: {string.Join(" | ", threat.RouteChoices)}.";
        return threat.Priorities.Count == 0
            ? $"Ближайшая угроза: {threat.Explanation}.{route}"
            : $"Ближайший босс: {threat.Name}. Следующий узел: {threat.NextNode}. Приоритеты: {string.Join(", ", threat.Priorities)} — {threat.Explanation}.{route}";
    }

    private static ThreatAssessment WithRoute(ThreatAssessment threat, RouteSnapshot route, int pressure, string next, IReadOnlyList<string> choices) =>
        threat with { RoutePressure = pressure, NextNode = next, RouteChoices = choices };

    private static bool Has(string text, params string[] tokens) => tokens.Any(text.Contains);
}

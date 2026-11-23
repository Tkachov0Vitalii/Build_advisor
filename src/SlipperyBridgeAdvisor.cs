using BuildAdvisor.Data;

namespace BuildAdvisor.Integration;

// One card of the Slippery Bridge decision: the card on offer, or one the next roll can show.
internal sealed record BridgeCard(string Id, bool Upgraded);

// What the event shows now (MegaCrit.Sts2.Core.Models.Events.SlipperyBridge, game v0.111.1):
// "Overcome" removes the shown card; "Hold On" costs HpCost HP (3, then +1 per hold) and rolls a
// new card uniformly from the removable deck cards of another type that were not shown before.
internal sealed record BridgeState(BridgeCard Shown, int HpCost, IReadOnlyList<BridgeCard> NextPool);

// HoldOn null: no verdict yet (card statistics are loading).
internal sealed record BridgeAdvice(bool? HoldOn, string OvercomeLine, string HoldLine)
{
    internal const string LoadingLine = "оцениваю карту на кону…";
    public static BridgeAdvice Loading { get; } = new(null, LoadingLine, LoadingLine);
}

// 7.6: Slippery Bridge is decided by the card at stake and the HP price, not by community lift.
// Spire Codex lift for HOLD_ON_N grows with N (a10:v0.111.0: +5.1 pp at the first hold, +15.1 at
// the seventh), because only runs with HP to spare keep holding on. 7.5 marked "Hold On" as best
// on every page until the option became lethal, then advised removing the card.
// The thresholds below are judgement calls (the corpus has no unbiased HP price): never pay HP
// below half of max HP, pay at most 5 HP per roll for an ordinary card and 8 for a key card.
internal static class SlipperyBridgeAdvisor
{
    internal const string EventId = "SLIPPERY_BRIDGE";
    internal const decimal SafeHpShare = .5m;
    internal const int OrdinaryCostLimit = 5;
    internal const int KeyCostLimit = 8;
    // Percentage points above the take threshold (TakeSkipModel.WinDelta) that make a key card.
    internal const decimal KeyCard = 2m;
    // Holding on must save at least this much card value against the expected next card.
    internal const decimal MinimumSaving = 1m;

    internal sealed record CardValue(BridgeCard Card, string Name, bool Free, decimal Delta)
    {
        // What losing the card costs: nothing for a starter card, a curse or a card below the threshold.
        public decimal Loss => Free ? 0m : Math.Max(0m, Delta);
    }

    public static BridgeAdvice Advise(RunContext context, BridgeState state,
        IReadOnlyDictionary<string, CardDefinition> catalog, IReadOnlyDictionary<string, EntityMetric> metrics)
    {
        var cache = new Dictionary<BridgeCard, CardValue>();
        CardValue Value(BridgeCard card) => cache.TryGetValue(card, out var known) ? known : cache[card] = Measure(context, card, catalog, metrics);
        var shown = Value(state.Shown);
        var pool = state.NextPool.Select(Value).ToArray();
        var weak = pool.Length == 0 ? 0m : (decimal)pool.Count(x => x.Loss == 0m) / pool.Length;
        var expectedLoss = pool.Length == 0 ? shown.Loss : pool.Average(x => x.Loss);
        var hpAfter = context.Hp - state.HpCost;
        var safe = hpAfter > 0 && hpAfter >= context.MaxHp * SafeHpShare;
        var key = shown.Delta >= KeyCard;
        var saving = shown.Loss - expectedLoss;
        var odds = $"следующая карта случайная из {pool.Length}: базовые, проклятия и слабые — {weak * 100m:0}%";
        var price = $"−{state.HpCost} HP (останется {Math.Max(0, hpAfter)}/{context.MaxHp})";

        if (shown.Free)
            return new(false, $"потеряете «{shown.Name}» — базовая карта или проклятие: это бесплатное удаление", $"{price}; не стоит: «{shown.Name}» и так лучше удалить");
        if (shown.Loss == 0m)
            return new(false, $"потеряете «{shown.Name}» — слабая для этой колоды ({TakeSkipModel.Format(shown.Delta)} п.п. к порогу взятия: наградой вы бы её пропустили)",
                $"{price}; не стоит платить HP за слабую карту");
        var value = $"«{shown.Name}» ({TakeSkipModel.Format(shown.Delta)} п.п. к порогу взятия{(key ? ", ключевая карта" : "")})";
        if (!safe)
            return new(false, $"потеряете {value} — HP важнее карты", $"{price}; не стоит: меньше половины HP перед следующими боями");
        if (state.HpCost > (key ? KeyCostLimit : OrdinaryCostLimit))
            return new(false, $"потеряете {value} — дальше держаться слишком дорого", $"{price}; не стоит: цена растёт с каждой попыткой");
        if (saving < MinimumSaving)
            return new(false, $"потеряете {value} — следующая карта в среднем не слабее", $"{price}; {odds} — замена почти ничего не даёт");
        return new(true, $"потеряете {value}", $"{price}; {odds}");
    }

    internal static CardValue Measure(RunContext context, BridgeCard card,
        IReadOnlyDictionary<string, CardDefinition> catalog, IReadOnlyDictionary<string, EntityMetric> metrics)
    {
        var key = SpireCodexMetricsClient.NormalizeId(card.Id);
        catalog.TryGetValue(key, out var definition);
        var name = (definition?.Name ?? card.Id) + (card.Upgraded ? "+" : "");
        if (definition is not null && (CardTaxonomy.IsRemovableCurse(definition) || CardTaxonomy.IsStarterStrikeOrDefend(definition)))
            return new(card, name, true, 0m);
        // The card is valued as if offered again to the deck without it, so it is not taxed as its own copy.
        var deck = context.Cards.ToList();
        var index = deck.FindIndex(x => SpireCodexMetricsClient.NormalizeId(x) == key);
        if (index >= 0) deck.RemoveAt(index);
        var without = context with { Cards = deck };
        var upgraded = card.Upgraded ? new HashSet<string>([key], StringComparer.OrdinalIgnoreCase) : null;
        var offer = DeckBuildScoring.Evaluate(without, [card.Id], catalog, metrics, upgradedOffers: upgraded).Offers.FirstOrDefault();
        return new(card, name, false, offer is null ? 0m : TakeSkipModel.WinDelta(offer));
    }
}

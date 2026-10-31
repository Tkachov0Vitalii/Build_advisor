using System.Collections;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Runs;

namespace BuildAdvisor.Integration;

internal sealed record RunContext(string Character, IReadOnlyList<string> Cards, IReadOnlyList<string> Relics, int Ascension, int Act, int Floor, int Hp, int MaxHp, int Gold, string? Boss, decimal AverageCost, int AttackCards, int SkillCards, int PowerCards, IReadOnlyList<string> History)
{
    // Spire Codex accepts only "all", "a10" and the A10 win-rate cohorts; "a0".."a9" return
    // 400 "unknown bracket", which made every lower-ascension run fall back to local advice.
    public string Bracket => Ascension >= 10 ? "a10" : "all";
    public IReadOnlyList<DeckCardState> CardStates { get; init; } = [];
    public RouteSnapshot Route { get; init; } = RouteSnapshot.Empty;
    public int PlayerCount { get; init; } = 1;
    public string? RunSeed { get; init; }
    // M3: held potions (IDs) and the number of potion slots.
    public IReadOnlyList<string> Potions { get; init; } = [];
    public int PotionSlots { get; init; }
    public bool HasOpenPotionSlot => PotionSlots <= 0 || Potions.Count < PotionSlots;
    public bool IsMultiplayer => PlayerCount > 1;
}

internal sealed record DeckCardState(string Id, int UpgradeLevel, int MaxUpgradeLevel)
{
    public bool CanUpgrade => UpgradeLevel < MaxUpgradeLevel;
}

internal sealed record RouteOption(IReadOnlyList<string> Nodes)
{
    public string Next => Nodes.FirstOrDefault() ?? "unknown";
    public int Pressure => Nodes.Sum(RouteSnapshot.NodePressure);
}

internal sealed record RouteSnapshot(string Current, IReadOnlyList<RouteOption> Options)
{
    public static readonly RouteSnapshot Empty = new("unknown", []);
    public bool IsKnown => Options.Count > 0;
    public static int NodePressure(string node) => node.ToLowerInvariant() switch
    {
        "boss" => 6, "elite" => 4, "monster" => 2, "unknown" => 1,
        "rest_site" => -2, "shop" => -1, _ => 0
    };
}

internal static class RunContextReader
{
    private static readonly BuildAdvisor.Data.CardCatalogClient Catalog = new();
    public static RunContext? TryRead()
    {
        var manager = RunManager.Instance;
        if (manager is null) return null;
        var state = ReflectionIds.Member(manager, "State");
        if (state is null) return null;
        var players = Values(ReflectionIds.Member(state, "Players")).ToArray();
        var player = LocalPlayer(players);
        if (player is null) return null;
        var character = ReflectionIds.FindId(ReflectionIds.Member(player, "Character")!);
        if (string.IsNullOrWhiteSpace(character)) return null;
        var deck = ReflectionIds.Member(player, "Deck");
        var cardObjects = Values(ReflectionIds.Member(deck!, "Cards")).ToArray();
        ObserveCards(cardObjects);
        // A deck is a multiset: preserving duplicate instances is essential for duplicate
        // penalties and for distinguishing an upgraded copy from an unupgraded copy.
        var cards = ReflectionIds.FindIdsWithDuplicates(cardObjects);
        var cardStates = cardObjects.Select(ReadCardState).Where(x => x is not null).Cast<DeckCardState>().ToArray();
        PersonalRunDiary.ObserveDeck(cards);
        var relics = ReflectionIds.FindIds(Values(ReflectionIds.Member(player, "Relics")));
        var ascension = Number(ReflectionIds.Member(state, "AscensionLevel"));
        var act = Number(ReflectionIds.Member(state, "CurrentActIndex")) + 1;
        var floor = Number(ReflectionIds.Member(state, "TotalFloor"));
        // HP lives on Player.Creature (CurrentHp/MaxHp); the player object itself has none, which
        // made every run read as HP 0/1 and inflated the segment-risk model.
        var creature = ReflectionIds.Member(player, "Creature");
        var hp = Number(First(player, "CurrentHp", "Hp", "Health", "CurrentHealth") ?? (creature is null ? null : First(creature, "CurrentHp", "Hp")));
        var maxHp = Number(First(player, "MaxHp", "MaxHealth", "MaximumHp") ?? (creature is null ? null : First(creature, "MaxHp", "MaxHealth")));
        var gold = Number(First(player, "Gold", "GoldAmount", "Money"));
        var actModel = ReflectionIds.Member(state, "Act");
        var bossValue = First(state, "ActBoss", "Boss", "BossId", "UpcomingBoss") ??
                        (actModel is null ? null : First(actModel, "BossEncounter", "Boss", "BossId"));
        var boss = bossValue is null ? null : ReflectionIds.FindId(bossValue) ?? bossValue.ToString();
        var route = ReadRoute(state);
        var catalog = Catalog.TryGetCached();
        var facts = cards.Select(id =>
        {
            var fact = CardKnowledge.Get(id);
            if (fact.Cost >= 0 || !catalog.TryGetValue(Data.SpireCodexMetricsClient.NormalizeId(id), out var definition)) return fact;
            return new CardFact(definition.Cost, definition.Type);
        }).ToArray();
        var knownCosts = facts.Where(x => x.Cost >= 0).Select(x => x.Cost).ToArray();
        var averageCost = knownCosts.Length == 0 ? 0 : decimal.Round((decimal)knownCosts.Average(), 2);
        var attacks = facts.Count(x => x.Kind.Contains("ATTACK", StringComparison.OrdinalIgnoreCase));
        var skills = facts.Count(x => x.Kind.Contains("SKILL", StringComparison.OrdinalIgnoreCase));
        var powers = facts.Count(x => x.Kind.Contains("POWER", StringComparison.OrdinalIgnoreCase));
        return new RunContext(character.ToLowerInvariant(), cards, relics, ascension, act, floor, hp, maxHp <= 0 ? Math.Max(hp, 1) : maxHp, gold, boss, averageCost, attacks, skills, powers, RunTimeline.Update(cards, relics, floor))
        {
            CardStates = cardStates,
            Route = route,
            PlayerCount = Math.Max(1, players.Length),
            RunSeed = ReflectionIds.Member(ReflectionIds.Member(state, "Rng") ?? new object(), "StringSeed")?.ToString(),
            // PotionSlots holds one entry per slot, null for an empty slot.
            Potions = ReflectionIds.FindIds(Values(ReflectionIds.Member(player, "PotionSlots")).Where(x => x is not null)),
            PotionSlots = Math.Max(Number(ReflectionIds.Member(player, "MaxPotionCount")), Values(ReflectionIds.Member(player, "PotionSlots")).Count())
        };
    }

    // H1: the map point the player stands on (null before the first move of an act).
    public static object? CurrentMapPoint()
    {
        var state = RunManager.Instance is { } manager ? ReflectionIds.Member(manager, "State") : null;
        return state is null ? null : ReflectionIds.Member(state, "CurrentMapPoint");
    }

    internal static string MapPointType(object? point) => MapNodeName(point is null ? null : ReflectionIds.Member(point, "PointType"));

    // The live Player model of this client (null outside a run).
    public static Player? LocalPlayerModel()
    {
        var state = RunManager.Instance is { } manager ? ReflectionIds.Member(manager, "State") : null;
        return state is null ? null : LocalPlayer(Values(ReflectionIds.Member(state, "Players")).ToArray()) as Player;
    }

    // In co-op the player list starts with the host, not necessarily with this client.
    // LocalContext.GetMe returns null in single player (no NetId), so the first player
    // remains the correct fallback there.
    private static object? LocalPlayer(object[] players)
    {
        if (players.Length <= 1) return players.FirstOrDefault();
        try
        {
            if (LocalContext.GetMe(players.OfType<Player>()) is { } me) return me;
        }
        catch { }
        return players.FirstOrDefault();
    }

    private static RouteSnapshot ReadRoute(object state)
    {
        var current = ReflectionIds.Member(state, "CurrentMapPoint");
        if (current is null) return RouteSnapshot.Empty;
        var currentType = MapNodeName(ReflectionIds.Member(current, "PointType"));
        var options = Values(ReflectionIds.Member(current, "Children"))
            .Select(child => new RouteOption(ReadPath(child, 3)))
            .Where(option => option.Nodes.Count > 0)
            .ToArray();
        return new RouteSnapshot(currentType, options);
    }

    private static IReadOnlyList<string> ReadPath(object point, int depth)
    {
        var nodes = new List<string>();
        var cursor = point;
        for (var index = 0; index < depth; index++)
        {
            nodes.Add(MapNodeName(ReflectionIds.Member(cursor, "PointType")));
            var children = Values(ReflectionIds.Member(cursor, "Children")).ToArray();
            if (children.Length == 0) break;
            // For a compact preview, follow the highest-pressure continuation. Every immediate
            // branch remains a separate RouteOption and is evaluated independently later.
            cursor = children.OrderByDescending(child => RouteSnapshot.NodePressure(
                MapNodeName(ReflectionIds.Member(child, "PointType")))).First();
        }
        return nodes;
    }
    private static string MapNodeName(object? value)
    {
        var raw = value?.ToString()?.Trim().ToLowerInvariant().Replace("-", "_").Replace(" ", "_") ?? "unknown";
        return raw switch { "restsite" => "rest_site", "normal" => "monster", "ancient" => "ancient", _ => raw };
    }

    internal static DeckCardState? ReadCardState(object card)
    {
        var id = ReflectionIds.FindId(card);
        if (string.IsNullOrWhiteSpace(id)) return null;
        var upgraded = ReflectionIds.FindBool(card, "IsUpgraded") == true;
        var level = ReflectionIds.FindInt(card, "CurrentUpgradeLevel", "UpgradeLevel") ?? (upgraded ? 1 : 0);
        var max = ReflectionIds.FindInt(card, "MaxUpgradeLevel") ?? 1;
        return new DeckCardState(id, Math.Max(0, level), Math.Max(0, max));
    }
    private static IEnumerable<object> Values(object? source) => source is IEnumerable values ? values.Cast<object>() : [];
    private static int Number(object? value) => value is null ? 0 : Convert.ToInt32(value);
    private static object? First(object source, params string[] names) => names.Select(x => ReflectionIds.Member(source, x)).FirstOrDefault(x => x is not null);

    public static void ObserveCards(IEnumerable<object> cards)
    {
        foreach (var card in cards)
        {
            var id = ReflectionIds.FindId(card);
            if (id is null) continue;
            var cost = ReflectionIds.FindInt(card, "Cost", "EnergyCost", "BaseCost", "BaseEnergyCost") ?? -1;
            var kind = ReflectionIds.FindText(card, "CardType", "Type", "Category") ?? "";
            CardKnowledge.Observe(id, cost, kind);
        }
    }
}

internal readonly record struct CardFact(int Cost, string Kind);
internal static class CardKnowledge
{
    private static readonly Dictionary<string, CardFact> Facts = new(StringComparer.OrdinalIgnoreCase);
    public static void Observe(string id, int cost, string kind) => Facts[Data.SpireCodexMetricsClient.NormalizeId(id)] = new CardFact(cost, kind);
    public static CardFact Get(string id) => Facts.TryGetValue(Data.SpireCodexMetricsClient.NormalizeId(id), out var fact) ? fact : new CardFact(-1, "");
}

internal static class RunTimeline
{
    private static Dictionary<string, int>? previous;
    private static readonly Queue<string> entries = new();
    public static IReadOnlyList<string> Update(IEnumerable<string> cards, IEnumerable<string> relics, int floor)
    {
        var current = cards.Concat(relics).GroupBy(x => x).ToDictionary(x => x.Key, x => x.Count());
        if (previous is not null)
            foreach (var pair in current.Where(p => p.Value > previous.GetValueOrDefault(p.Key)))
                for (var i = previous.GetValueOrDefault(pair.Key); i < pair.Value; i++) { entries.Enqueue($"#{floor}: {pair.Key}"); if (entries.Count > 8) entries.Dequeue(); }
        previous = current;
        return entries.ToArray();
    }
}

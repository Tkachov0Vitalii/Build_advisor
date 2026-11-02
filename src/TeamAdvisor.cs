using BuildAdvisor.Data;

namespace BuildAdvisor.Integration;

// H5: co-op. The advisor scored every card as if the player were alone. In a co-op run it now
// reads the teammates' decks and HP, describes each teammate's role and archetype, and adjusts
// the cards that act on other players:
//  - cards that protect another player (Lift, Intercept, Rally, Tank, Beacon of Hope, Mimic,
//    Demonic Shield) when a teammate is low on HP or has little defence of their own;
//  - cards that empower another player (Coordinate, Believe in You, Huddle Up, Energy Surge…)
//    and cards that reward teammates' attacks (Flanking, Tag Team, Gang Up, Knockdown, Sneaky)
//    when a teammate is an attacker;
//  - a card of the archetype a teammate with the same character already builds, when the
//    player's own deck does not: two copies of one engine compete for the same key cards and
//    relics, a second engine covers fights the first one is weak against.
// These cards' community scores already come from co-op runs (they are multiplayer-only); the
// adjustments are hand values like the other deck adjustments, since the run corpus is solo.
internal sealed record TeammateProfile(string Character, int Cards, int HpPercent, bool Fragile, bool Attacker,
    bool Supporter, IReadOnlyList<string> Archetypes)
{
    public string Role => Attacker && !Fragile ? "урон" : Fragile ? "нужна защита" : Supporter ? "поддержка" : "универсал";
}

internal static class TeamAdvisor
{
    internal const decimal ProtectBonus = 10m;
    internal const decimal EmpowerBonus = 7m;
    internal const decimal ComboBonus = 6m;
    internal const decimal OverlapPenalty = -6m;

    private static readonly string[] OtherPlayer =
        ["ДРУГОМУ ИГРОК", "ДРУГОЙ ИГРОК", "ДРУГИЕ ИГРОКИ", "ДРУГИХ ИГРОК", "ВСЕМ ИГРОКАМ", "ВСЕ ИГРОКИ", "ANOTHER PLAYER", "OTHER PLAYER", "ALL PLAYERS"];
    private static readonly string[] Protection = ["ЗАЩИТ", "BLOCK", "ПОЛОВИНУ УРОНА", "HALF", "ПЕРЕНАПРАВЛЯЕТ", "REDIRECT"];
    private static readonly string[] TeammateAttacks =
        ["АТАК ДРУГИХ ИГРОК", "АТАКА ДРУГОГО ИГРОКА", "АТАКУ ДРУГОГО ИГРОКА", "ДРУГОЙ ИГРОК АТАКУЕТ", "УРОН ОТ ДРУГИХ ИГРОК", "OTHER PLAYERS' ATTACKS", "ANOTHER PLAYER'S", "ANOTHER PLAYER ATTACKS"];

    private static string Text(CardDefinition card) => (card.Id + " " + card.Description).ToUpperInvariant();

    internal static bool TargetsTeam(CardDefinition card) =>
        card.Target is "AnyAlly" or "AllAllies" || OtherPlayer.Any(Text(card).Contains);

    internal static bool ProtectsTeam(CardDefinition card) => TargetsTeam(card) && Protection.Any(Text(card).Contains);

    internal static bool RewardsTeammateAttacks(CardDefinition card) => TeammateAttacks.Any(Text(card).Contains);

    internal static TeammateProfile Profile(TeammateDeck mate, IReadOnlyDictionary<string, CardDefinition> catalog, int act, int floor)
    {
        var deck = mate.Cards.Select(id => catalog.TryGetValue(SpireCodexMetricsClient.NormalizeId(id), out var card) ? card : null)
            .Where(x => x is not null).Cast<CardDefinition>().ToArray();
        var core = deck.Where(x => !CardTaxonomy.IsStarterStrikeOrDefend(x)).ToArray();
        var coreDefense = core.Count(x => x.Block > 0);
        var coreAttacks = core.Count(x => x.Damage > 0 && x.Type.Contains("attack", StringComparison.OrdinalIgnoreCase));
        var defenseTarget = act switch { <= 1 => floor >= 8 ? 1 : 0, 2 => 2, _ => 3 };
        var hpPercent = mate.MaxHp <= 0 ? 100 : (int)Math.Round(100m * mate.Hp / mate.MaxHp);
        var fragile = hpPercent < 50 || coreDefense < defenseTarget;
        var attacker = coreAttacks >= 2 && coreAttacks >= coreDefense;
        var supporter = core.Count(TargetsTeam) >= 2;
        var archetypes = DeckBuildScoring.DetectArchetypes(mate.Character, deck, floor);
        return new TeammateProfile(mate.Character, mate.Cards.Count, hpPercent, fragile, attacker, supporter, archetypes);
    }

    // Adjustments for one offered card. ownArchetypes: the player's detected archetypes.
    internal static IReadOnlyList<(decimal Value, string Label)> Adjust(CardDefinition card, string character,
        IReadOnlyList<string> ownArchetypes, IReadOnlyList<TeammateProfile> team, Func<string, bool> matchesArchetype)
    {
        var result = new List<(decimal, string)>();
        if (team.Count == 0) return result;
        if (ProtectsTeam(card) && team.Any(x => x.Fragile)) result.Add((ProtectBonus, "защита напарника"));
        else if (TargetsTeam(card) && !ProtectsTeam(card) && !RewardsTeammateAttacks(card) && team.Any(x => x.Attacker)) result.Add((EmpowerBonus, "усиление напарника"));
        if (RewardsTeammateAttacks(card) && team.Any(x => x.Attacker)) result.Add((ComboBonus, "атаки напарников"));
        var taken = team.Where(x => x.Character == character).SelectMany(x => x.Archetypes)
            .Where(x => !ownArchetypes.Contains(x)).Distinct().ToArray();
        // A card that hands Strength or block to a teammate is not a second copy of their engine.
        if (!TargetsTeam(card) && taken.Any(matchesArchetype)) result.Add((OverlapPenalty, "архетип напарника"));
        return result;
    }

    internal static string Summary(IReadOnlyList<TeammateProfile> team)
    {
        if (team.Count == 0) return "";
        var lines = team.Select(x =>
            $"{CharacterName(x.Character)}: {x.Cards} карт, ОЗ {x.HpPercent}% · роль: {x.Role}" +
            (x.Archetypes.Count > 0 ? " · архетип: " + string.Join(" + ", x.Archetypes) : ""));
        return "Команда: " + string.Join("; ", lines) + ".";
    }

    private static string CharacterName(string character) => character switch
    {
        "ironclad" => "Латоносец", "silent" => "Безмолвная", "defect" => "Дефект",
        "regent" => "Наследник", "necrobinder" => "Призывающая", _ => character
    };
}

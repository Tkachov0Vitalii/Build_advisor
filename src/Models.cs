namespace BuildAdvisor.Core;

public sealed record RunSnapshot(
    string GameVersion,
    string Character,
    int Ascension,
    int Floor,
    IReadOnlySet<string> Deck,
    IReadOnlySet<string> Relics,
    string? UpcomingBoss,
    IReadOnlyList<string> OfferedCards,
    string? PickedCard,
    bool Won);

public sealed record CurrentRunState(
    string GameVersion,
    string Character,
    int Ascension,
    int Floor,
    IReadOnlySet<string> Deck,
    IReadOnlySet<string> Relics,
    string? UpcomingBoss,
    IReadOnlyList<string> OfferedCards);

public sealed record CardAdvice(
    string CardId,
    int Matches,
    int Picked,
    int WinsAfterPick,
    decimal PickRate,
    decimal WinRateAfterPick,
    decimal Confidence,
    string Verdict);

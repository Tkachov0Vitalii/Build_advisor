using BuildAdvisor.Data;

namespace BuildAdvisor.Integration;

// 7.4: the value of an Ancient relic at the moment it is chosen, from the run corpus
// (tools/measure_ancient_relics.py, report docs/ancient-relics-report.json).
// Spire Codex relic lift mixes every point of the run, so a relic taken at the start of act 3 looks
// strong just because its runs already survived two acts. Here a pick is compared with the other
// Ancient picks of the same character at the same act start: those runs all reached that point.
// 7.5 refit on game versions v0.111.0/v0.111.1 only (174k runs): per-relic values correlate 0.89
// with the train values on held-out runs, and runs whose pick was in the train top third won
// 7.9 pp more often than runs whose pick was in the bottom third.
internal static partial class AncientRelicValues
{
    // Fewer runs than this for a character falls back to every character at that act.
    internal const int MinimumCharacterRuns = 150;

    internal static string Key(string relicId) =>
        new string(relicId.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();

    // 7.5: relics left out of the table until it is refitted after a patch that changed them.
    // Empty since the v0.111.x refit (2026-11-24: 174k runs of v0.111.0/v0.111.1); v0.111.1 had
    // changed Diamond Diadem, Toasty Mittens, Beautiful Bracelet, Fur Coat, Signet Ring, Meat
    // Cleaver, Toybox, Seal of Gold, Sere Talon, Distinguished Cape, Jeweled Mask, Sand Castle.
    internal static readonly IReadOnlySet<string> ChangedSinceTable = new HashSet<string>(StringComparer.Ordinal);

    internal static (decimal Pp, int Runs, bool ForCharacter)? Value(string? character, int act, string relicId)
    {
        var relic = Key(relicId);
        if (ChangedSinceTable.Contains(relic)) return null;
        var stage = Math.Clamp(act, 1, 3);
        if (character is { Length: > 0 } && Table.TryGetValue($"{character.ToLowerInvariant()}:{stage}:{relic}", out var own) && own.Runs >= MinimumCharacterRuns)
            return (own.Pp, own.Runs, true);
        return Table.TryGetValue($"all:{stage}:{relic}", out var all) ? (all.Pp, all.Runs, false) : null;
    }
}

// 7.4 (H8): relic × card synergies measured on the corpus (tools/measure_relic_synergies.py,
// report docs/relic-synergies-report.json). Only pairs whose relic text names the card feature
// are tested; a pair ships when it passes train (z ≥ 2) and held-out (z ≥ 2) checks and has a
// mechanical reason. 7.5 refit on v0.111.x runs (134k runs, 150 pairs): Mummified Hand × Power
// (a Power makes a card in hand free) and Red Mask × Weak (the relic already applies Weak to all
// enemies) pass. Sword of Stone × Power and Lasting Candy × Power pass too but have no mechanism
// (reward composition) and are left out. Game Piece × Power, shipped in 7.4 on mixed builds, did
// not hold on v0.111.x held-out runs (+1.8 ± 1.5 pp) and is dropped. Shuriken, Kunai, Nunchaku,
// Pen Nib, Ornamental Fan with Attacks and Letter Opener with Skills do not hold either.
internal static class RelicSynergies
{
    internal sealed record Synergy(string Relic, string Feature, decimal Pp, string Text);

    internal static readonly IReadOnlyList<Synergy> Verified =
    [
        new("MUMMIFIEDHAND", "type:power", 2.34m, "Высохшая рука: сила делает карту в руке бесплатной"),
        new("REDMASK", "power:WEAK", -2.77m, "Красная маска уже накладывает слабость на всех врагов"),
    ];

    internal static bool HasFeature(CardDefinition card, string feature) => feature switch
    {
        "type:power" => card.Type.Contains("power", StringComparison.OrdinalIgnoreCase),
        "type:attack" => card.Type.Contains("attack", StringComparison.OrdinalIgnoreCase),
        "type:skill" => card.Type.Contains("skill", StringComparison.OrdinalIgnoreCase),
        _ when feature.StartsWith("power:", StringComparison.Ordinal) =>
            card.Signals.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(feature["power:".Length..], StringComparer.OrdinalIgnoreCase),
        _ => false
    };

    // Matches for one card and the relics held: (score points, text). Points convert the measured
    // win-rate gain back to the score scale (TakeSkipModel.PointsPerScore pp per point).
    internal static IEnumerable<(decimal Points, string Text)> For(CardDefinition card, IEnumerable<string> relics)
    {
        var held = relics.Select(AncientRelicValues.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var synergy in Verified)
            if (held.Contains(synergy.Relic) && HasFeature(card, synergy.Feature))
                yield return (decimal.Round(synergy.Pp / TakeSkipModel.PointsPerScore, 1), synergy.Text);
    }
}

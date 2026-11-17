using BuildAdvisor.Data;

namespace BuildAdvisor.Integration;

// 7.4: the value of an Ancient relic at the moment it is chosen, from the run corpus
// (tools/measure_ancient_relics.py, report docs/ancient-relics-report.json).
// Spire Codex relic lift mixes every point of the run, so a relic taken at the start of act 3 looks
// strong just because its runs already survived two acts. Here a pick is compared with the other
// Ancient picks of the same character at the same act start: those runs all reached that point.
// Check on held-out runs: per-relic values correlate 0.87 with the train values, and runs whose
// pick was in the train top third won 8 pp more often than runs whose pick was in the bottom third.
internal static partial class AncientRelicValues
{
    // Fewer runs than this for a character falls back to every character at that act.
    internal const int MinimumCharacterRuns = 150;

    internal static string Key(string relicId) =>
        new string(relicId.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();

    // 7.5: Ancient relics changed in v0.108–v0.111 (Major Update #3, main branch v0.111.1). The
    // table mixes builds (most runs v0.107.1), so these keep the version-sliced community score
    // until the table is refitted on v0.111+ runs. Neow's Sacrifice and Dowsing Rod are new.
    internal static readonly IReadOnlySet<string> ChangedSinceTable = new HashSet<string>(StringComparer.Ordinal)
    {
        "DIAMONDDIADEM", "BEAUTIFULBRACELET", "FURCOAT", "SIGNETRING", "MEATCLEAVER", "TOYBOX",
        "SEALOFGOLD", "TOASTYMITTENS", "SERETALON", "DISTINGUISHEDCAPE", "JEWELEDMASK", "SANDCASTLE"
    };

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

// 7.4 (H7): relic × card synergies measured on the corpus (tools/measure_relic_synergies.py,
// report docs/relic-synergies-report.json). Only pairs whose relic text names the card feature
// were tested (151 with data); one passed train (z ≥ 2) and held-out (z ≥ 2) checks with a
// mechanical reason: Game Piece draws a card per Power played. Lasting Candy × Power also passed,
// but it adds a Power to rewards, so that is reward composition, not synergy, and is left out.
// Shuriken, Kunai, Nunchaku, Pen Nib, Ornamental Fan with Attacks and Letter Opener with Skills
// showed 0–1.7 pp on train and did not hold on held-out runs.
internal static class RelicSynergies
{
    internal sealed record Synergy(string Relic, string Feature, decimal Pp, string Text);

    internal static readonly IReadOnlyList<Synergy> Verified =
    [
        new("GAMEPIECE", "type:power", 2.51m, "Игровая фигура: каждая сила тянет карту"),
    ];

    internal static bool HasFeature(CardDefinition card, string feature) => feature switch
    {
        "type:power" => card.Type.Contains("power", StringComparison.OrdinalIgnoreCase),
        "type:attack" => card.Type.Contains("attack", StringComparison.OrdinalIgnoreCase),
        "type:skill" => card.Type.Contains("skill", StringComparison.OrdinalIgnoreCase),
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

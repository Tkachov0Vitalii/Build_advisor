using System.Text.Json;

namespace BuildAdvisor.Integration;

// H3 (7.0): the hand-set deck adjustments (+25 "needs block", +16 archetype fit, −8 hard to play…)
// are multiplied by weights fitted on the offline run corpus (tools/calibrate_score_weights.py).
// A weight of 1 is the original hand value. Missing or invalid data keeps every weight at 1, so
// an installation without the file behaves exactly like 6.22.
internal static class ScoreWeights
{
    internal const string FileName = "score-weights-v1.data";
    internal const string Contract = "score-weights-v1";
    internal const decimal Minimum = 0m;
    internal const decimal Maximum = 3m;

    // Every adjustment the corpus can measure. Coach, key-card, personal and manual-archetype
    // adjustments are absent from historical runs and keep their hand values.
    internal static readonly string[] Keys =
    [
        "нужна защита", "быстрый урон", "усиление к поздней игре", "нужен добор", "даёт энергию",
        "дорогая при нехватке энергии", "сложность разыгрывания", "подготовка к боссу", "синергия архетипа",
        "улучшенная версия", "копия", "размер колоды", "ценность в акте", "несколько структурных минусов",
        "похожие ситуации"
    ];

    private static readonly Lazy<IReadOnlyDictionary<string, decimal>> Bundled = new(LoadBundled);
    private static IReadOnlyDictionary<string, decimal>? overrideWeights;

    // The calibration tool scores the corpus with candidate weights; nothing else sets this.
    internal static IReadOnlyDictionary<string, decimal>? Override
    {
        get => overrideWeights;
        set => overrideWeights = value;
    }

    // The regression matrix pins hand values (its expectations predate H3); the H3 regression
    // sets Override explicitly and checks the bundled file separately.
    internal static IReadOnlyDictionary<string, decimal> Current =>
        overrideWeights ?? (BuildAdvisorRegression.IsRunning ? Empty : Bundled.Value);
    private static readonly IReadOnlyDictionary<string, decimal> Empty = new Dictionary<string, decimal>();
    internal static IReadOnlyDictionary<string, decimal> BundledWeights => Bundled.Value;

    // Labels carry details ("копия 2", "ценность в акте 3"): the key is the matching prefix.
    internal static string? KeyOf(string label) => Keys.FirstOrDefault(key => label.StartsWith(key, StringComparison.Ordinal));

    internal static decimal For(string label) =>
        KeyOf(label) is { } key && Current.TryGetValue(key, out var weight) ? weight : 1m;

    internal static IReadOnlyDictionary<string, decimal> Parse(string json)
    {
        var result = new Dictionary<string, decimal>(StringComparer.Ordinal);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("contract", out var contract) || contract.GetString() != Contract) return result;
        if (!root.TryGetProperty("weights", out var weights) || weights.ValueKind != JsonValueKind.Object) return result;
        foreach (var row in weights.EnumerateObject())
        {
            if (!Keys.Contains(row.Name, StringComparer.Ordinal) || row.Value.ValueKind != JsonValueKind.Number) continue;
            var value = row.Value.GetDecimal();
            if (value >= Minimum && value <= Maximum) result[row.Name] = value;
        }
        return result;
    }

    private static IReadOnlyDictionary<string, decimal> LoadBundled()
    {
        try
        {
            var file = Path.Combine(Path.GetDirectoryName(typeof(ScoreWeights).Assembly.Location) ?? "", FileName);
            return File.Exists(file) ? Parse(File.ReadAllText(file)) : new Dictionary<string, decimal>();
        }
        catch { return new Dictionary<string, decimal>(); }
    }
}

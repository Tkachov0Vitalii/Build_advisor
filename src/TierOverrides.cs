using System.Text.Json;

namespace BuildAdvisor.Integration;

// L5: personal tier corrections, the way Deck Compass and TierLister let players override a list.
// File: <local app data>/BuildAdvisor/tier-overrides.json
//
// {
//   "cards":  { "BLOODLETTING": 10, "ironclad:TWIN_STRIKE": -15 },
//   "relics": { "SOZU": -10 }
// }
//
// Values are points on the 0-100 advice scale, limited to ±30. A "character:ID" key applies only
// to that character and wins over a plain ID. The correction is shown in the score formula as
// "ваша поправка", so it never changes advice silently. The file is re-read when it changes.
internal static class TierOverrides
{
    internal const decimal Limit = 30m;
    public static readonly string FilePath = ModPaths.UserData("tier-overrides.json");
    private static readonly object Gate = new();
    private static DateTime loadedStamp = DateTime.MinValue;
    private static Overrides current = Overrides.Empty;
    private static DateTime checkedAt = DateTime.MinValue;

    internal sealed record Overrides(IReadOnlyDictionary<string, decimal> Cards, IReadOnlyDictionary<string, decimal> Relics)
    {
        public static readonly Overrides Empty = new(new Dictionary<string, decimal>(), new Dictionary<string, decimal>());
    }

    public static decimal ForCard(string character, string id) => Lookup(Current().Cards, character, id);
    public static decimal ForRelic(string character, string id) => Lookup(Current().Relics, character, id);

    // Regression scenarios use a fixed, file-free configuration.
    internal static Overrides? TestOverrides { get; set; }

    private static Overrides Current()
    {
        if (TestOverrides is { } test) return test;
        if (BuildAdvisorRegression.IsRunning) return Overrides.Empty;
        lock (Gate)
        {
            // The card search scores hundreds of cards in a row; the file is checked once a second.
            if (DateTime.UtcNow - checkedAt < TimeSpan.FromSeconds(1)) return current;
            checkedAt = DateTime.UtcNow;
            try
            {
                var stamp = File.Exists(FilePath) ? File.GetLastWriteTimeUtc(FilePath) : DateTime.MinValue;
                if (stamp == loadedStamp) return current;
                loadedStamp = stamp;
                current = stamp == DateTime.MinValue ? Overrides.Empty : Parse(File.ReadAllText(FilePath));
            }
            catch (Exception error)
            {
                // A typo in a hand-written file must not break advice; the previous valid
                // configuration stays active and the log says what is wrong.
                Godot.GD.PrintErr("Build Advisor tier-overrides.json: " + error.Message);
            }
            return current;
        }
    }

    internal static Overrides Parse(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        return new Overrides(Section(document.RootElement, "cards"), Section(document.RootElement, "relics"));
    }

    private static IReadOnlyDictionary<string, decimal> Section(JsonElement root, string name)
    {
        var result = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out var section) || section.ValueKind != JsonValueKind.Object) return result;
        foreach (var entry in section.EnumerateObject())
            if (entry.Value.ValueKind == JsonValueKind.Number && entry.Value.TryGetDecimal(out var value))
                result[Key(entry.Name)] = Math.Clamp(value, -Limit, Limit);
        return result;
    }

    private static decimal Lookup(IReadOnlyDictionary<string, decimal> values, string character, string id)
    {
        if (values.Count == 0) return 0m;
        var key = Data.SpireCodexMetricsClient.NormalizeId(id);
        if (values.TryGetValue(character.Trim().ToLowerInvariant() + ":" + key, out var specific)) return specific;
        return values.TryGetValue(key, out var general) ? general : 0m;
    }

    // A ready-to-edit sample next to the real file, so players can find the location.
    public static void WriteExampleIfMissing()
    {
        try
        {
            var example = Path.ChangeExtension(FilePath, ".example.json");
            if (File.Exists(example)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(example)!);
            File.WriteAllText(example, """
            // Copy this file to tier-overrides.json and edit it. Values: -30..+30 points.
            // "character:ID" applies to one character only (ironclad, silent, defect, regent, necrobinder).
            {
              "cards": {
                "BLOODLETTING": 5,
                "ironclad:TWIN_STRIKE": -10
              },
              "relics": {
                "SOZU": -5
              }
            }
            """);
        }
        catch { /* optional convenience */ }
    }

    private static string Key(string raw)
    {
        var separator = raw.IndexOf(':');
        return separator < 0
            ? Data.SpireCodexMetricsClient.NormalizeId(raw)
            : raw[..separator].Trim().ToLowerInvariant() + ":" + Data.SpireCodexMetricsClient.NormalizeId(raw[(separator + 1)..]);
    }
}

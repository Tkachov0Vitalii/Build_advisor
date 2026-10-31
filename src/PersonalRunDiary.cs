using System.Text.Json;
using Godot;

namespace BuildAdvisor.Integration;

// Local-only decision history. No run data is uploaded by Build Advisor.
internal sealed record PersonalDecision(DateTimeOffset At, string Character, int Act, int Floor, IReadOnlyList<string> Offered, string? Chosen, string? Feedback);

internal static class PersonalRunDiary
{
    private static readonly object Gate = new();
    // The .NET local application-data directory is platform-correct on Windows, Linux and macOS.
    private static readonly string Path = ModPaths.UserData("personal-history.json");
    private static Pending? pending;
    private static IReadOnlyList<PersonalDecision>? cached;
    private sealed record Pending(PersonalDecision Decision, IReadOnlyDictionary<string, int> Before);

    public static void RecordOffer(RunContext? context, IEnumerable<string> ids)
    {
        if (context is null) return;
        var offered = ids.Distinct().ToArray(); if (offered.Length == 0) return;
        lock (Gate) pending = new Pending(new PersonalDecision(DateTimeOffset.UtcNow, context.Character, context.Act, context.Floor, offered, null, null), Counts(context.Cards));
    }
    public static void ObserveDeck(IEnumerable<string> cards)
    {
        Pending? item; lock (Gate) item = pending;
        if (item is null) return;
        var now = Counts(cards);
        var chosen = item.Decision.Offered.FirstOrDefault(id => now.GetValueOrDefault(id) > item.Before.GetValueOrDefault(id));
        if (chosen is null) return;
        Save(item.Decision with { Chosen = chosen });
        lock (Gate) pending = null;
    }
    public static int ChoiceCount(string id)
    {
        try { return Load().Count(x => string.Equals(x.Chosen, id, StringComparison.OrdinalIgnoreCase)); } catch { return 0; }
    }
    public static decimal PreferenceBonus(string character, string id)
    {
        if (System.Environment.GetEnvironmentVariable("BUILD_ADVISOR_OFFLINE_EVALUATION") == "1") return 0;
        try
        {
            var decisions = Load().Where(x => string.Equals(x.Character, character, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (decisions.Length < 3) return 0;
            var key = Data.SpireCodexMetricsClient.NormalizeId(id);
            var chosen = decisions.Count(x => x.Chosen is not null && Data.SpireCodexMetricsClient.NormalizeId(x.Chosen) == key);
            var offered = decisions.Count(x => x.Offered.Any(card => Data.SpireCodexMetricsClient.NormalizeId(card) == key));
            if (offered < 2) return 0;
            // This is preference, not a claim about win rate. Keep it deliberately small so
            // personal habits can break close ties but never overpower deck needs.
            var rate = (decimal)chosen / offered;
            return Math.Clamp((rate - .34m) * 9m, -3m, 5m);
        }
        catch { return 0; }
    }
    public static string Summary(string character)
    {
        try
        {
            var decisions = Load().Where(x => string.Equals(x.Character, character, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (decisions.Length == 0) return "Личный профиль: данных пока нет — он начнёт работать после нескольких выборов.";
            var picks = decisions.Where(x => !string.IsNullOrWhiteSpace(x.Chosen)).ToArray();
            var top = picks.GroupBy(x => x.Chosen!, StringComparer.OrdinalIgnoreCase).OrderByDescending(x => x.Count()).Take(3)
                .Select(x => $"{x.Key} ×{x.Count()}");
            return $"Личный профиль: {decisions.Length} решений, подтверждено выборов {picks.Length}" +
                   (top.Any() ? "; чаще выбирались: " + string.Join(", ", top) : "") + ".";
        }
        catch { return "Личный профиль: история временно недоступна."; }
    }
    public static void RecordFeedback(bool helpful)
    {
        Pending? item; lock (Gate) item = pending;
        if (item is null) return;
        Save(item.Decision with { Feedback = helpful ? "useful" : "questionable" });
    }
    private static void Save(PersonalDecision decision)
    {
        try
        {
            var all = Load().Append(decision).TakeLast(500).ToArray();
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path, JsonSerializer.Serialize(all));
            cached = all;
        }
        catch { /* a diary write must never affect a run */ }
    }
    private static IReadOnlyList<PersonalDecision> Load()
    {
        lock (Gate)
        {
            if (cached is not null) return cached;
            if (!File.Exists(Path)) return cached = [];
            return cached = JsonSerializer.Deserialize<PersonalDecision[]>(File.ReadAllText(Path)) ?? [];
        }
    }
    private static IReadOnlyDictionary<string, int> Counts(IEnumerable<string> ids) => ids.GroupBy(x => x, StringComparer.OrdinalIgnoreCase).ToDictionary(x => x.Key, x => x.Count(), StringComparer.OrdinalIgnoreCase);
}

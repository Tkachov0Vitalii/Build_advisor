using System.Text.Json;
using Godot;

namespace BuildAdvisor.Integration;

// Local-only decision history; the diary itself is never uploaded. (Pick Coach separately sends
// the current deck, relics and offer to spire-codex.com without a player identifier.)
// M7: Seed ties a decision to its run; Advised is the mod's advice (a card ID or SkipMarker) and
// Deltas the win-rate value of each offered card against skipping, so a finished run can be
// reviewed. Files written before 6.21 lack these fields and stay readable.
internal sealed record PersonalDecision(DateTimeOffset At, string Character, int Act, int Floor, IReadOnlyList<string> Offered, string? Chosen, string? Feedback,
    string? Seed = null, string? Advised = null, IReadOnlyDictionary<string, decimal>? Deltas = null);

internal sealed record RunOutcome(DateTimeOffset At, string Character, string Seed, bool Win, int Floor, int Ascension);

internal static class PersonalRunDiary
{
    private static readonly object Gate = new();
    // The .NET local application-data directory is platform-correct on Windows, Linux and macOS.
    private static readonly string Path = ModPaths.UserData("personal-history.json");
    private static Pending? pending;
    private static IReadOnlyList<PersonalDecision>? cached;
    // Marker stored in Chosen when the reward screen closed without a new offered card.
    public const string SkipMarker = "SKIP";
    // The game may add the picked card to the deck shortly after the reward screen leaves the
    // tree, so a closed offer waits briefly before it is recorded as a skip.
    private static readonly TimeSpan SkipGrace = TimeSpan.FromSeconds(4);
    private sealed record Pending(PersonalDecision Decision, IReadOnlyDictionary<string, int> Before, DateTimeOffset? ClosedAt = null);

    public static void RecordOffer(RunContext? context, IEnumerable<string> ids)
    {
        if (context is null) return;
        var offered = ids.Distinct().ToArray(); if (offered.Length == 0) return;
        Pending? closed = null;
        lock (Gate)
        {
            // A refresh of the same open screen replaces the offer; a previous screen that was
            // already closed and never produced a pick is a skip and is finalised first.
            if (pending?.ClosedAt is not null) closed = pending;
            pending = new Pending(new PersonalDecision(DateTimeOffset.UtcNow, context.Character, context.Act, context.Floor, offered, null, null, context.RunSeed), Counts(context.Cards));
        }
        if (closed is not null) Save(closed.Decision with { Chosen = SkipMarker });
    }

    public static void CloseOffer()
    {
        lock (Gate) if (pending is not null && pending.ClosedAt is null) pending = pending with { ClosedAt = DateTimeOffset.UtcNow };
    }

    public static void ObserveDeck(IEnumerable<string> cards)
    {
        Pending? item; lock (Gate) item = pending;
        if (item is null) return;
        var now = Counts(cards);
        var chosen = item.Decision.Offered.FirstOrDefault(id => now.GetValueOrDefault(id) > item.Before.GetValueOrDefault(id));
        if (chosen is null)
        {
            if (item.ClosedAt is null || DateTimeOffset.UtcNow - item.ClosedAt.Value < SkipGrace) return;
            chosen = SkipMarker;
        }
        lock (Gate)
        {
            // Another thread or a new offer may have replaced the pending decision meanwhile.
            if (!ReferenceEquals(pending, item)) return;
            pending = null;
        }
        Save(item.Decision with { Chosen = chosen });
    }
    public static int ChoiceCount(string id)
    {
        try { return Load().Count(x => string.Equals(x.Chosen, id, StringComparison.OrdinalIgnoreCase)); } catch { return 0; }
    }
    public static decimal PreferenceBonus(string character, string id)
    {
        if (BuildAdvisorRegression.IsRunning || System.Environment.GetEnvironmentVariable("BUILD_ADVISOR_OFFLINE_EVALUATION") == "1") return 0;
        try
        {
            var decisions = Load().Where(x => string.Equals(x.Character, character, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (decisions.Length < 3) return 0;
            var key = Data.SpireCodexMetricsClient.NormalizeId(id);
            var chosen = decisions.Count(x => x.Chosen is not null && x.Chosen != SkipMarker && Data.SpireCodexMetricsClient.NormalizeId(x.Chosen) == key);
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
            var picks = decisions.Where(x => !string.IsNullOrWhiteSpace(x.Chosen) && x.Chosen != SkipMarker).ToArray();
            var skips = decisions.Count(x => x.Chosen == SkipMarker);
            var top = picks.GroupBy(x => x.Chosen!, StringComparer.OrdinalIgnoreCase).OrderByDescending(x => x.Count()).Take(3)
                .Select(x => $"{x.Key} ×{x.Count()}");
            return $"Личный профиль: {decisions.Length} решений, подтверждено выборов {picks.Length}, пропусков {skips}" +
                   (top.Any() ? "; чаще выбирались: " + string.Join(", ", top) : "") + ".";
        }
        catch { return "Личный профиль: история временно недоступна."; }
    }
    // Feedback belongs to the pending decision and is written once, together with the pick or
    // skip. Saving a copy here produced a second record that inflated the "offered" count.
    public static void RecordFeedback(bool helpful)
    {
        lock (Gate)
        {
            if (pending is null) return;
            pending = pending with { Decision = pending.Decision with { Feedback = helpful ? "useful" : "questionable" } };
        }
    }
    // M7: the advice shown for the pending offer; written together with the pick or skip.
    public static void RecordAdvice(BuildAssessment assessment)
    {
        var verdict = TakeSkipModel.Evaluate(assessment);
        var deltas = assessment.Offers.GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => TakeSkipModel.WinDelta(x.First()), StringComparer.OrdinalIgnoreCase);
        var advised = verdict.Take && verdict.Best is not null ? verdict.Best.Id : SkipMarker;
        lock (Gate)
        {
            if (pending is null) return;
            pending = pending with { Decision = pending.Decision with { Advised = advised, Deltas = deltas } };
        }
    }

    public static IReadOnlyList<PersonalDecision> Decisions() { try { return Load(); } catch { return []; } }

    private static readonly string OutcomePath = ModPaths.UserData("personal-runs.json");
    private static IReadOnlyList<RunOutcome>? outcomes;

    public static IReadOnlyList<RunOutcome> Outcomes()
    {
        lock (Gate)
        {
            if (outcomes is not null) return outcomes;
            try { outcomes = File.Exists(OutcomePath) ? JsonSerializer.Deserialize<RunOutcome[]>(File.ReadAllText(OutcomePath)) ?? [] : []; }
            catch { outcomes = []; }
            return outcomes;
        }
    }

    // One record per run seed; the game-over screen can open more than once for the same run.
    public static void RecordOutcome(RunOutcome outcome)
    {
        if (BuildAdvisorRegression.IsRunning) return;
        try
        {
            var all = Outcomes().Where(x => x.Seed != outcome.Seed).Append(outcome).TakeLast(200).ToArray();
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(OutcomePath)!);
            File.WriteAllText(OutcomePath + ".tmp", JsonSerializer.Serialize(all));
            File.Move(OutcomePath + ".tmp", OutcomePath, true);
            lock (Gate) outcomes = all;
        }
        catch { /* never affect the game */ }
    }

    // The last reward screen of a run may still be pending when the run ends.
    public static void FlushPending()
    {
        Pending? item;
        lock (Gate) { item = pending; pending = null; }
        if (item is not null && item.ClosedAt is not null) Save(item.Decision with { Chosen = SkipMarker });
    }

    private static void Save(PersonalDecision decision)
    {
        try
        {
            var all = Load().Append(decision).TakeLast(500).ToArray();
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            var temporary = Path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(all));
            File.Move(temporary, Path, true);
            lock (Gate) cached = all;
        }
        catch { /* a diary write must never affect a run */ }
    }
    private static IReadOnlyList<PersonalDecision> Load()
    {
        lock (Gate)
        {
            if (cached is not null) return cached;
            if (!File.Exists(Path)) return cached = [];
            try { cached = Collapse(JsonSerializer.Deserialize<PersonalDecision[]>(File.ReadAllText(Path)) ?? []); }
            catch { cached = []; }
            return cached ?? [];
        }
    }
    // Files written by 6.16.x and earlier may contain two records for one decision: a feedback
    // copy without a pick and the real pick. Records with the same timestamp and offer are merged.
    private static IReadOnlyList<PersonalDecision> Collapse(IReadOnlyList<PersonalDecision> decisions) => decisions
        .GroupBy(x => (x.At, x.Character, Offer: string.Join(",", x.Offered)))
        .Select(group => group.Last() with
        {
            Chosen = group.Select(x => x.Chosen).LastOrDefault(x => x is not null),
            Feedback = group.Select(x => x.Feedback).LastOrDefault(x => x is not null)
        })
        .OrderBy(x => x.At)
        .ToArray();

    private static IReadOnlyDictionary<string, int> Counts(IEnumerable<string> ids) => ids.GroupBy(x => x, StringComparer.OrdinalIgnoreCase).ToDictionary(x => x.Key, x => x.Count(), StringComparer.OrdinalIgnoreCase);
}

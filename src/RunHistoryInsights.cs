using System.Text.Json;
using BuildAdvisor.Data;

namespace BuildAdvisor.Integration;

internal sealed record DuplicateInsight(int DuplicateRuns, int DuplicateWins, int SingleRuns, int SingleWins)
{
    public decimal DuplicateWinRate => DuplicateRuns == 0 ? 0 : (decimal)DuplicateWins / DuplicateRuns;
    public decimal SingleWinRate => SingleRuns == 0 ? 0 : (decimal)SingleWins / SingleRuns;
}
internal sealed record DuplicatePreference(decimal Adjustment, int Offers);
internal sealed record ChoicePreference(decimal Rate, int Offers);
internal sealed record SkipDecisionModel(decimal Bias, IReadOnlyDictionary<string, decimal> Weights, decimal Threshold);

// Reads only the player's local .run files. Results are deliberately shrinked and are never
// treated as causal evidence: short/abandoned runs make raw deck-size comparisons misleading.
internal static class RunHistoryInsights
{
    private sealed record HistoryRun(string Key, string Character, bool Win, bool Abandoned, int MaxFloor, IReadOnlyList<(string Id, int Floor)> Deck);
    private sealed record Model(IReadOnlyList<HistoryRun> Runs, IReadOnlyDictionary<string, DuplicateInsight> Duplicates,
        IReadOnlyDictionary<string, DuplicatePreference> DuplicatePreferences,
        IReadOnlyDictionary<string, ChoicePreference> ChoicePreferences,
        IReadOnlyDictionary<string, IReadOnlyDictionary<int, int>> ExpectedDeck, SkipDecisionModel? SkipModel, int PriorRuns);
    private static readonly Lazy<Model> Data = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    public static int ExpectedDeckSize(string character, int floor)
    {
        floor = Math.Max(1, floor);
        var relevant = Data.Value.Runs.Where(x => x.Character == character && x.Win && x.MaxFloor >= floor).ToArray();
        // A handful of personal runs is far too noisy to redefine the target curve. Prefer
        // the global winner prior until the player has a genuinely representative history.
        if (relevant.Length < 10 && Data.Value.ExpectedDeck.TryGetValue(character, out var priorCurve) && priorCurve.Count > 0)
        {
            var nearest = priorCurve.OrderBy(x => Math.Abs(x.Key - floor)).First();
            return nearest.Value;
        }
        if (relevant.Length < 2) relevant = Data.Value.Runs.Where(x => x.Character == character && !x.Abandoned && x.MaxFloor >= floor).ToArray();
        if (relevant.Length == 0) return Math.Clamp(12 + floor / 3, 15, 28);
        var sizes = relevant.Select(run => run.Deck.Count(card => card.Floor <= floor)).OrderBy(x => x).ToArray();
        return sizes[sizes.Length / 2];
    }

    public static decimal DeckSizeAdjustment(RunContext context, int afterSize)
    {
        var expected = ExpectedDeckSize(context.Character, context.Floor);
        var allowance = context.Act switch { 1 => 2, 2 => 1, _ => 0 };
        var excess = afterSize - expected - allowance;
        var rate = context.Act switch { 1 => 1.5m, 2 => 2.5m, _ => 3.75m };
        return excess <= 0 ? 0m : -Math.Min(36m, excess * rate);
    }

    public static decimal DuplicateAdjustment(string character, string cardId, int copiesAfter, bool repeatable, bool fillsNeed, int act = 1)
    {
        if (copiesAfter <= 1) return 0m;
        var structural = repeatable
            ? copiesAfter switch { 2 => -1.5m, 3 => -4m, _ => -Math.Min(14m, 4m + (copiesAfter - 3) * 2.5m) }
            : copiesAfter switch { 2 => -4m, 3 => -10m, _ => -Math.Min(28m, 10m + (copiesAfter - 3) * 5m) };
        if (act >= 3) structural *= 1.7m;
        else if (act == 2) structural *= 1.3m;
        if (fillsNeed) structural *= .8m;
        var key = character + "|" + SpireCodexMetricsClient.NormalizeId(cardId);
        var learned = 0m;
        var preferenceKey = key + "|" + Math.Clamp(act, 1, 3) + "|" + Math.Clamp(copiesAfter - 1, 1, 3);
        if (Data.Value.DuplicatePreferences.TryGetValue(preferenceKey, out var preference))
        {
            var preferenceReliability = Math.Min(1m, preference.Offers / 150m);
            learned = Math.Clamp(preference.Adjustment * preferenceReliability, -12m, 6m);
        }
        if (!Data.Value.Duplicates.TryGetValue(key, out var evidence) || evidence.DuplicateRuns < 10 || evidence.SingleRuns < 10)
            return Math.Min(-.5m, structural + learned);
        var delta = evidence.DuplicateWinRate - evidence.SingleWinRate;
        // The smaller group limits the comparison. A 3-vs-30 split is not as trustworthy
        // as 30-vs-30, even though both contain 33 observations in total.
        var reliability = Math.Min(1m, Math.Min(evidence.DuplicateRuns, evidence.SingleRuns) / 100m);
        // Observational history may soften a structural penalty, but never erase it entirely.
        // Better duplicate win rates are heavily affected by survivor/selection bias: strong
        // runs both live long enough and have more opportunities to acquire a second copy.
        // Evidence may soften the tax slightly, but can never make duplication a bonus.
        return Math.Min(-.5m, structural + learned + Math.Clamp(delta * 4m * reliability, -4m, 1m));
    }

    public static string Summary(string character, int floor)
    {
        var runs = Data.Value.Runs.Where(x => x.Character == character).ToArray();
        var wins = runs.Count(x => x.Win);
        return $"История: локально {runs.Length} забегов ({wins} побед), глобальный офлайн-приор {Data.Value.PriorRuns}; ориентир размера к этажу {floor}: {ExpectedDeckSize(character, floor)} карт. " +
               "Личные результаты используются только при достаточной повторяемости наблюдений.";
    }

    public static DuplicateInsight? ForCard(string character, string id) =>
        Data.Value.Duplicates.GetValueOrDefault(character + "|" + SpireCodexMetricsClient.NormalizeId(id));

    public static bool HasReliableDuplicateEvidence(DuplicateInsight? evidence) =>
        evidence is { DuplicateRuns: >= 8, SingleRuns: >= 8 };

    public static decimal? ChoiceSignal(string character, string cardId, int act, int deckSize, int copiesBefore)
    {
        var key = $"{character.ToLowerInvariant()}|{NormalizeId(cardId)}|{Math.Clamp(act, 1, 3)}|{Math.Min(5, Math.Max(0, deckSize) / 8)}|{Math.Clamp(copiesBefore, 0, 3)}";
        return Data.Value.ChoicePreferences.TryGetValue(key, out var preference) && preference.Offers >= 20 ? preference.Rate : null;
    }

    public static decimal SkipThreshold => Data.Value.SkipModel?.Threshold ?? .45m;

    public static decimal? SkipProbability(RunContext context, IReadOnlyList<string> offeredIds, IReadOnlyList<decimal?> signals)
    {
        var model = Data.Value.SkipModel;
        if (model is null || offeredIds.Count < 2) return null;
        const decimal neutral = .24m;
        var values = signals.Select(x => x ?? neutral).ToArray();
        var best = values.Length == 0 ? neutral : values.Max();
        var mean = values.Length == 0 ? neutral : values.Average();
        var expected = ExpectedDeckSize(context.Character, context.Floor);
        var excessBucket = Bucket(context.Cards.Count - expected, -4m, -1m, 2m, 5m, 9m);
        var bestBucket = Bucket(best, .10m, .20m, .30m, .40m, .50m);
        var counts = context.Cards.GroupBy(NormalizeId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.Count(), StringComparer.OrdinalIgnoreCase);
        var maxCopies = offeredIds.Select(x => counts.GetValueOrDefault(NormalizeId(x))).DefaultIfEmpty(0).Max();
        var character = NormalizeCharacter(context.Character);
        var act = Math.Max(1, context.Act);
        var features = new[]
        {
            $"character={character}", $"act={act}",
            $"deck={Bucket(context.Cards.Count, 12m, 18m, 24m, 30m, 36m)}", $"excess={excessBucket}",
            $"best={bestBucket}", $"mean={Bucket(mean, .10m, .20m, .30m, .40m)}", $"copies={Math.Min(3, maxCopies)}",
            $"hp={Bucket(context.MaxHp <= 0 ? 1m : (decimal)context.Hp / context.MaxHp, .25m, .50m, .75m)}",
            $"ascension={Bucket(context.Ascension, 0m, 4m, 9m)}", $"offers={Math.Min(4, offeredIds.Count)}",
            $"act_excess={act}:{excessBucket}", $"best_excess={bestBucket}:{excessBucket}", $"character_act={character}:{act}"
        };
        var logit = model.Bias + features.Sum(x => model.Weights.GetValueOrDefault(x));
        var bounded = Math.Clamp((double)logit, -40d, 40d);
        return (decimal)(1d / (1d + Math.Exp(-bounded)));
    }

    private static int Bucket(decimal value, params decimal[] boundaries) => boundaries.Count(boundary => value > boundary);

    private static Model Load()
    {
        var runs = new List<HistoryRun>(); var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in HistoryFiles().OrderByDescending(File.GetLastWriteTimeUtc).Take(300))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file)); var root = doc.RootElement;
                if (!root.TryGetProperty("players", out var players) || players.GetArrayLength() == 0) continue;
                var player = players[0];
                var character = NormalizeCharacter(player.GetProperty("character").GetString() ?? "");
                var seed = root.TryGetProperty("seed", out var seedValue) ? seedValue.ToString() : "";
                var start = root.TryGetProperty("start_time", out var startValue) ? startValue.ToString() : "";
                var key = seed + "|" + start + "|" + character; if (!seen.Add(key)) continue;
                var deck = new List<(string, int)>();
                if (player.TryGetProperty("deck", out var cards)) foreach (var card in cards.EnumerateArray())
                {
                    var id = card.TryGetProperty("id", out var idValue) ? NormalizeId(idValue.GetString() ?? "") : "";
                    var added = card.TryGetProperty("floor_added_to_deck", out var floorValue) && floorValue.TryGetInt32(out var parsed) ? parsed : 1;
                    if (id.Length > 0) deck.Add((id, added));
                }
                var maxFloor = 0;
                if (root.TryGetProperty("map_point_history", out var acts)) foreach (var act in acts.EnumerateArray()) if (act.ValueKind == JsonValueKind.Array) maxFloor += act.GetArrayLength();
                var win = root.TryGetProperty("win", out var winValue) && winValue.GetBoolean();
                var abandoned = root.TryGetProperty("was_abandoned", out var abandonedValue) && abandonedValue.GetBoolean();
                runs.Add(new HistoryRun(key, character, win, abandoned, Math.Max(maxFloor, deck.Select(x => x.Item2).DefaultIfEmpty(1).Max()), deck));
            }
            catch { /* One corrupt history file must not affect advice. */ }
        }
        var evidence = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase);
        var preferences = new Dictionary<string, DuplicatePreference>(StringComparer.OrdinalIgnoreCase);
        var choicePreferences = new Dictionary<string, ChoicePreference>(StringComparer.OrdinalIgnoreCase);
        var expectedDeck = new Dictionary<string, IReadOnlyDictionary<int, int>>(StringComparer.OrdinalIgnoreCase);
        var (priorRuns, skipModel) = LoadPriors(evidence, preferences, choicePreferences, expectedDeck);
        foreach (var run in runs.Where(x => !x.Abandoned))
            foreach (var group in run.Deck.Where(x => x.Floor > 1).GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase))
            {
                var key = run.Character + "|" + group.Key;
                if (!evidence.TryGetValue(key, out var counts)) evidence[key] = counts = new int[4];
                if (group.Count() >= 2) { counts[0]++; if (run.Win) counts[1]++; }
                else { counts[2]++; if (run.Win) counts[3]++; }
            }
        return new Model(runs, evidence.ToDictionary(x => x.Key, x => new DuplicateInsight(x.Value[0], x.Value[1], x.Value[2], x.Value[3]), StringComparer.OrdinalIgnoreCase), preferences, choicePreferences, expectedDeck, skipModel, priorRuns);
    }

    private static (int Runs, SkipDecisionModel? SkipModel) LoadPriors(Dictionary<string, int[]> evidence, Dictionary<string, DuplicatePreference> preferences,
        Dictionary<string, ChoicePreference> choicePreferences,
        Dictionary<string, IReadOnlyDictionary<int, int>> expectedDeck)
    {
        try
        {
            var file = Path.Combine(Path.GetDirectoryName(typeof(RunHistoryInsights).Assembly.Location) ?? "", "build-advisor-run-priors.data");
            if (!File.Exists(file)) return (0, null);
            using var doc = JsonDocument.Parse(File.ReadAllText(file)); var root = doc.RootElement;
            if (root.TryGetProperty("expected_deck_by_floor", out var curves)) foreach (var character in curves.EnumerateObject())
            {
                var curve = new Dictionary<int, int>();
                foreach (var point in character.Value.EnumerateObject()) if (int.TryParse(point.Name, out var floor) && point.Value.TryGetInt32(out var size)) curve[floor] = size;
                expectedDeck[character.Name.ToLowerInvariant()] = curve;
            }
            if (root.TryGetProperty("duplicates", out var rows)) foreach (var row in rows.EnumerateArray())
            {
                var character = row.GetProperty("character").GetString()?.ToLowerInvariant() ?? "";
                var card = NormalizeId(row.GetProperty("card").GetString() ?? "");
                if (character.Length == 0 || card.Length == 0) continue;
                evidence[character + "|" + card] = [row.GetProperty("duplicate_runs").GetInt32(), row.GetProperty("duplicate_wins").GetInt32(), row.GetProperty("single_runs").GetInt32(), row.GetProperty("single_wins").GetInt32()];
            }
            if (root.TryGetProperty("duplicate_preferences", out var preferenceRows)) foreach (var row in preferenceRows.EnumerateArray())
            {
                var character = row.GetProperty("character").GetString()?.ToLowerInvariant() ?? "";
                var card = NormalizeId(row.GetProperty("card").GetString() ?? "");
                var act = row.GetProperty("act").GetInt32();
                var copiesBefore = row.GetProperty("copies_before").GetInt32();
                var offers = row.GetProperty("offers").GetInt32();
                var adjustment = row.GetProperty("adjustment").GetDecimal();
                if (character.Length == 0 || card.Length == 0 || offers < 30) continue;
                preferences[$"{character}|{card}|{act}|{copiesBefore}"] = new DuplicatePreference(adjustment, offers);
            }
            if (root.TryGetProperty("choice_preferences", out var choices)) foreach (var row in choices.EnumerateObject())
            {
                if (row.Value.ValueKind != JsonValueKind.Array || row.Value.GetArrayLength() < 2) continue;
                var rate = row.Value[0].GetDecimal(); var offers = row.Value[1].GetInt32();
                if (offers >= 20) choicePreferences[row.Name] = new ChoicePreference(rate, offers);
            }
            SkipDecisionModel? skipModel = null;
            if (root.TryGetProperty("skip_model", out var skip) &&
                skip.TryGetProperty("feature_contract", out var contract) && contract.GetString() == "skip-v1" &&
                skip.TryGetProperty("bias", out var bias) && skip.TryGetProperty("threshold", out var threshold) &&
                skip.TryGetProperty("weights", out var weightsElement))
            {
                var weights = new Dictionary<string, decimal>(StringComparer.Ordinal);
                foreach (var weight in weightsElement.EnumerateObject()) weights[weight.Name] = weight.Value.GetDecimal();
                skipModel = new SkipDecisionModel(bias.GetDecimal(), weights, threshold.GetDecimal());
            }
            var runs = root.TryGetProperty("runs", out var count) && count.TryGetInt32(out var parsedRuns) ? parsedRuns : 0;
            return (runs, skipModel);
        }
        catch (Exception error) { Console.WriteLine("Build Advisor priors: " + error.Message); return (0, null); }
    }

    private static IEnumerable<string> HistoryFiles()
    {
        var roaming = System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData);
        var local = System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData);
        var home = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
        var roots = new[]
        {
            Path.Combine(roaming, "SlayTheSpire2"),
            Path.Combine(roaming, "GSE Saves", "2868840", "remote"),
            Path.Combine(local, "SlayTheSpire2"),
            Path.Combine(home, ".local", "share", "SlayTheSpire2"),
            Path.Combine(home, ".config", "SlayTheSpire2"),
            Path.Combine(home, "Library", "Application Support", "SlayTheSpire2")
        }.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            IEnumerable<string> files; try { files = Directory.EnumerateFiles(root, "*.run", SearchOption.AllDirectories).ToArray(); } catch { continue; }
            foreach (var file in files) yield return file;
        }
    }
    private static string NormalizeCharacter(string value) => value.Replace("CHARACTER.", "", StringComparison.OrdinalIgnoreCase).ToLowerInvariant();
    private static string NormalizeId(string value) => SpireCodexMetricsClient.NormalizeId(value.Replace("CARD.", "", StringComparison.OrdinalIgnoreCase));
}

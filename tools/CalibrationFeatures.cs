using System.IO.Compression;
using System.Text.Json;
using BuildAdvisor.Data;
using BuildAdvisor.Integration;

// H3: replays every card reward of the corpus extract through the mod's own scoring and writes,
// for each offered card, the community baseline and the raw (unweighted) deck adjustments.
// tools/calibrate_score_weights.py fits the adjustment weights on these features.
//
// Input: part-*.jsonl.gz from the corpus extract (one run per line: character, ascension, win,
// final deck with floor_added, removals, upgrades, relics and every card choice with HP/gold).
internal static class CalibrationFeatures
{
    public static int Export(string extractDir, string splitsPath, string catalogFile, string metricsDir, string output, bool includeSkips = false)
    {
        Environment.SetEnvironmentVariable("BUILD_ADVISOR_OFFLINE_EVALUATION", "1");
        if (!includeSkips) ScoreWeights.Override = new Dictionary<string, decimal>(); // raw hand values while replaying (H3); take/skip uses the shipped weights
        var catalog = CardCatalogClient.Parse(File.ReadAllText(catalogFile));
        var global = Metrics(Path.Combine(metricsDir, "metrics-cards-all.json"));
        var byCharacter = new Dictionary<string, IReadOnlyDictionary<string, EntityMetric>>(StringComparer.OrdinalIgnoreCase);
        foreach (var character in new[] { "ironclad", "silent", "defect", "regent", "necrobinder" })
        {
            var file = Path.Combine(metricsDir, $"metrics-cards-all-{character}.json");
            byCharacter[character] = File.Exists(file) ? SpireCodexMetricsClient.Merge(global, Metrics(file)) : global;
        }
        var splits = File.ReadLines(splitsPath).Select(x => x.Split('\t')).Where(x => x.Length == 2)
            .ToDictionary(x => x[0], x => x[1], StringComparer.Ordinal);
        long runs = 0, decisions = 0, unknown = 0;
        using var writer = new StreamWriter(output);
        foreach (var part in Directory.GetFiles(extractDir, "part-*.jsonl.gz").OrderBy(x => x, StringComparer.Ordinal))
        {
            using var reader = new StreamReader(new GZipStream(File.OpenRead(part), CompressionMode.Decompress));
            for (var line = reader.ReadLine(); line is not null; line = reader.ReadLine())
            {
                using var doc = JsonDocument.Parse(line);
                var run = doc.RootElement;
                var hash = run.GetProperty("h").GetString() ?? "";
                var character = (run.GetProperty("c").GetString() ?? "").ToLowerInvariant();
                if (!byCharacter.TryGetValue(character, out var metrics)) continue;
                var split = splits.GetValueOrDefault(hash, "unknown");
                var win = run.GetProperty("w").GetInt32();
                var abandoned = run.GetProperty("ab").GetInt32();
                var ascension = run.GetProperty("a").ValueKind == JsonValueKind.Number ? run.GetProperty("a").GetInt32() : 0;
                var cards = new List<(string Id, int Added, int? Removed)>();
                foreach (var x in run.GetProperty("deck").EnumerateArray()) cards.Add((x[0].GetString() ?? "", Math.Max(1, Int(x[1])), null));
                foreach (var x in run.GetProperty("rm").EnumerateArray()) cards.Add((x[0].GetString() ?? "", Math.Max(1, Int(x[1])), Int(x[2])));
                var relics = run.GetProperty("rel").EnumerateArray().Select(x => (Id: x[0].GetString() ?? "", Added: Math.Max(1, Int(x[1])))).ToArray();
                var upgrades = run.GetProperty("up").EnumerateArray().Select(x => (Id: x[0].GetString() ?? "", Floor: Int(x[1]))).ToArray();
                runs++;
                foreach (var point in run.GetProperty("pts").EnumerateArray())
                {
                    var floor = Int(point[0]); var act = Int(point[1]);
                    var picked = Int(point[8]);
                    if (picked < 0 && !includeSkips) continue;
                    var offered = point[6].EnumerateArray().Select(x => x.GetString() ?? "").ToArray();
                    if (offered.Distinct(StringComparer.OrdinalIgnoreCase).Count() != offered.Length) continue;
                    var deck = cards.Where(x => (x.Added == 1 || x.Added < floor) && (x.Removed is null || x.Removed >= floor)).Select(x => x.Id).ToArray();
                    var upgraded = upgrades.Where(x => x.Floor < floor).GroupBy(x => x.Id).ToDictionary(x => x.Key, x => x.Count());
                    var states = deck.Select(id =>
                    {
                        var level = upgraded.TryGetValue(id, out var left) && left > 0 ? 1 : 0;
                        if (level > 0) upgraded[id] = left - 1;
                        return new DeckCardState(id, level, 1);
                    }).ToArray();
                    var hp = Int(point[3]); var maxHp = Math.Max(hp, Int(point[4]));
                    var context = new RunContext(character, deck, relics.Where(x => x.Added == 1 || x.Added < floor).Select(x => x.Id).ToArray(),
                        ascension, act, floor, hp, Math.Max(1, maxHp), Int(point[5]), null, 0, 0, 0, 0, []) { CardStates = states };
                    var offeredUpgraded = point[7].EnumerateArray().Select((x, i) => (Level: x.ValueKind == JsonValueKind.Number ? x.GetInt32() : 0, Id: offered[i]))
                        .Where(x => x.Level > 0).Select(x => SpireCodexMetricsClient.NormalizeId(x.Id)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var assessment = DeckBuildScoring.Evaluate(context, offered, catalog, metrics, null, offeredUpgraded);
                    var scores = assessment.Offers.ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
                    if (offered.Any(id => !scores.ContainsKey(id) || scores[id].RawAdjustments is null)) { unknown++; continue; }
                    var rows = offered.Select(id =>
                    {
                        var score = scores[id];
                        var raw = new Dictionary<string, decimal>(StringComparer.Ordinal);
                        foreach (var adjustment in score.RawAdjustments!)
                            if (ScoreWeights.KeyOf(adjustment.Label) is { } key) raw[key] = raw.GetValueOrDefault(key) + adjustment.Value;
                            else raw["fixed"] = raw.GetValueOrDefault("fixed") + adjustment.Value;
                        return new { id, c = Math.Round(score.Community, 3), f = Math.Round(score.Final, 3), r = raw,
                            d = TakeSkipModel.WinDelta(score), l = score.CommunityLift, ln = score.LiftSamples };
                    }).ToArray();
                    if (includeSkips)
                        writer.WriteLine(JsonSerializer.Serialize(new { s = split, h = hash, w = win, ab = abandoned, a = ascension, ch = character, act, fl = floor, n = deck.Length, p = picked,
                            sk = assessment.SkipScore, ex = RunHistoryInsights.ExpectedDeckSize(character, floor), hp = Math.Round((decimal)hp / Math.Max(1, maxHp), 3), o = rows }));
                    else
                        writer.WriteLine(JsonSerializer.Serialize(new { s = split, h = hash, w = win, ab = abandoned, a = ascension, ch = character, act, fl = floor, n = deck.Length, p = picked, o = rows }));
                    decisions++;
                }
                if (runs % 10000 == 0) Console.WriteLine($"runs={runs:N0} decisions={decisions:N0} unknown={unknown:N0}");
            }
        }
        Console.WriteLine(JsonSerializer.Serialize(new { runs, decisions, unknown }));
        return 0;
    }

    private static IReadOnlyDictionary<string, EntityMetric> Metrics(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return SpireCodexMetricsClient.ParseMetrics(doc.RootElement, "cards");
    }

    private static int Int(JsonElement value) => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : 0;
}

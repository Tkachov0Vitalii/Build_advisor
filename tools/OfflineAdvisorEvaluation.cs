using System.Text.Json;
using BuildAdvisor.Data;
using BuildAdvisor.Integration;

internal static class OfflineAdvisorEvaluation
{
    private sealed record HistoricalCard(string Id, int Added, int? Removed);
    private sealed record Example(string Split, string Character, int Act, int DeckBand, IReadOnlyList<string> Offered,
        string Chosen, IReadOnlyDictionary<string, decimal> Fixed, IReadOnlyDictionary<string, decimal> Broken,
        IReadOnlyDictionary<string, decimal> Learned);
    private sealed record SkipExample(string Split, bool Skipped, int Probability);
    private sealed record AutoPickExample(string Split, string Character, int Act, int DeckSize, bool Skipped,
        bool Agreed, decimal BestScore, decimal Lead, decimal Evidence, int SkipProbability);

    public static int Main(string[] args)
    {
        if (args is ["--validate-patches"])
        {
            try
            {
                var harmony = new HarmonyLib.Harmony("build-advisor.offline-patch-validation");
                harmony.PatchAll(typeof(BuildAdvisor.Integration.EventCardAdvisor).Assembly);
                var owned = HarmonyLib.Harmony.GetAllPatchedMethods().Count(method =>
                    HarmonyLib.Harmony.GetPatchInfo(method)?.Owners.Contains("build-advisor.offline-patch-validation") == true);
                Console.WriteLine(JsonSerializer.Serialize(new { PatchesValid = true, OwnedMethods = owned }));
                harmony.UnpatchAll("build-advisor.offline-patch-validation");
                return owned > 0 ? 0 : 1;
            }
            catch (Exception error)
            {
                Console.WriteLine(JsonSerializer.Serialize(new { PatchesValid = false, Error = error.ToString() }));
                return 1;
            }
        }
        if (args is ["--inspect-card-selection-types"])
        {
            var assembly = typeof(MegaCrit.Sts2.Core.Runs.RunState).Assembly;
            var requested = new[]
            {
                "NChooseACardSelectionScreen", "NCardGridSelectionScreen", "NChooseABundleSelectionScreen", "NCardSelectionScreen",
                "NCardHolder", "NCardGrid", "CardSelectorPrefs", "CardSelectCmd"
            };
            foreach (var type in assembly.GetTypes().Where(t => requested.Any(name =>
                         t.Name.Contains(name, StringComparison.OrdinalIgnoreCase))))
            {
                Console.WriteLine("TYPE " + type.FullName);
                foreach (var member in type.GetMembers(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static |
                                                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                                                        System.Reflection.BindingFlags.DeclaredOnly)
                             .Where(m => m.MemberType is System.Reflection.MemberTypes.Property or System.Reflection.MemberTypes.Field or System.Reflection.MemberTypes.Method)
                             .OrderBy(m => m.MemberType).ThenBy(m => m.Name))
                {
                    var detail = member switch
                    {
                        System.Reflection.FieldInfo f => f.FieldType.FullName,
                        System.Reflection.PropertyInfo p => p.PropertyType.FullName,
                        System.Reflection.MethodInfo method => method.ReturnType.FullName + " (" +
                            string.Join(", ", method.GetParameters().Select(p => p.ParameterType.FullName + " " + p.Name)) + ")",
                        _ => ""
                    };
                    Console.WriteLine($"  {member.MemberType} {member.Name}: {detail}");
                }
            }
            return 0;
        }
        if (args is ["--inspect-run-map-types"])
        {
            var assembly = typeof(MegaCrit.Sts2.Core.Runs.RunState).Assembly;
            foreach (var type in assembly.GetTypes().Where(t =>
                         t.FullName?.Contains("MegaCrit.Sts2.Core", StringComparison.Ordinal) == true &&
                         (t.Name.Contains("RunState", StringComparison.OrdinalIgnoreCase) ||
                          t.Name.Contains("ActModel", StringComparison.OrdinalIgnoreCase) ||
                          t.Name.Equals("Act", StringComparison.OrdinalIgnoreCase) ||
                          t.Name.Contains("ActMap", StringComparison.OrdinalIgnoreCase) ||
                          t.Name.Contains("MapPoint", StringComparison.OrdinalIgnoreCase) ||
                          t.Name.Contains("MapCoord", StringComparison.OrdinalIgnoreCase))))
            {
                Console.WriteLine("TYPE " + type.FullName);
                foreach (var member in type.GetMembers(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
                             .Where(m => m.MemberType is System.Reflection.MemberTypes.Property or System.Reflection.MemberTypes.Field))
                    Console.WriteLine("  " + member.MemberType + " " + member.Name);
            }
            return 0;
        }
        if (args is ["--regression"])
        {
            Environment.SetEnvironmentVariable("BUILD_ADVISOR_OFFLINE_EVALUATION", "1");
            var passed = BuildAdvisorRegression.Run();
            Console.WriteLine(JsonSerializer.Serialize(new { Regression = passed, BuildAdvisorRegression.LastError }));
            return passed ? 0 : 1;
        }
        if (args.Length < 6)
        {
            Console.Error.WriteLine("usage: corpus manifest catalog metrics model-report output");
            return 2;
        }
        Environment.SetEnvironmentVariable("BUILD_ADVISOR_OFFLINE_EVALUATION", "1");
        var corpus = args[0]; var manifest = args[1]; var catalogPath = args[2]; var metricsPath = args[3];
        var modelPath = args[4]; var output = args[5];
        var catalog = ParseCatalog(File.ReadAllText(catalogPath));
        var metricJson = File.ReadAllText(metricsPath);
        using var fixedDoc = JsonDocument.Parse(metricJson);
        var fixedMetrics = SpireCodexMetricsClient.ParseMetrics(fixedDoc.RootElement, "cards");
        using var brokenDoc = JsonDocument.Parse(metricJson.Replace("\"upgraded\":true", "\"upgraded\":false", StringComparison.Ordinal));
        var brokenMetrics = SpireCodexMetricsClient.ParseMetrics(brokenDoc.RootElement, "cards");
        var learned = ParseLearned(modelPath);
        var examples = new List<Example>(); var skipExamples = new List<SkipExample>(); var autoPickExamples = new List<AutoPickExample>();
        var missing = 0; var corrupt = 0; var skipped = 0; var processed = 0;
        foreach (var line in File.ReadLines(manifest))
        {
            var cells = line.Split('\t'); if (cells.Length != 3) continue;
            var path = Path.Combine(corpus, "runs", cells[1].ToLowerInvariant(), cells[2] + ".run");
            if (!File.Exists(path)) { missing++; continue; }
            try { ProcessRun(path, cells[0], catalog, fixedMetrics, brokenMetrics, learned, examples, skipExamples, autoPickExamples, ref skipped); }
            catch { corrupt++; }
            processed++;
            if (processed % 5000 == 0) Console.WriteLine($"runs={processed:N0} examples={examples.Count:N0}");
        }
        var scales = new decimal[] { 0m, .10m, .20m, .30m, .40m, .50m, .75m, 1m };
        var validation = scales.Select(scale => (Scale: scale, Result: Evaluate(examples.Where(x => x.Split == "validation"), scale))).ToArray();
        var best = validation.OrderByDescending(x => x.Result.HybridTop1).ThenBy(x => x.Scale).First().Scale;
        var test = Evaluate(examples.Where(x => x.Split == "test"), best);
        var skipThreshold = (int)Math.Round(RunHistoryInsights.SkipThreshold * 100m, MidpointRounding.AwayFromZero);
        var learnedSkipValidation = EvaluateSkip(skipExamples.Where(x => x.Split == "validation"), skipThreshold);
        var learnedSkipTest = EvaluateSkip(skipExamples.Where(x => x.Split == "test"), skipThreshold);
        var policySearch = SearchAutoPickPolicy(autoPickExamples.Where(x => x.Split == "validation"));
        var autoPickValidation = EvaluateAutoPick(autoPickExamples.Where(x => x.Split == "validation"), policySearch.Policy);
        var autoPickTest = EvaluateAutoPick(autoPickExamples.Where(x => x.Split == "test"), policySearch.Policy);
        var payload = new { schema = 1, runs = processed, examples = examples.Count, skipped, missing, corrupt,
            validation = validation.Select(x => new { scale = x.Scale, x.Result }), best_scale = best, test,
            learned_skip_validation = learnedSkipValidation, learned_skip_test = learnedSkipTest,
            auto_pick_policy = policySearch.Policy, auto_pick_validation = autoPickValidation,
            auto_pick_test = autoPickTest, auto_pick_validation_candidates = policySearch.Candidates };
        File.WriteAllText(output, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(new { examples = examples.Count, best_scale = best, test }));
        return 0;
    }

    private sealed record Result(int Decisions, decimal FixedTop1, decimal BrokenTop1, decimal LearnedTop1, decimal HybridTop1,
        IReadOnlyDictionary<string, decimal> HybridBySlice);
    private sealed record SkipResult(int Events, int Skips, int Threshold, decimal BalancedAccuracy, decimal SkipRecall,
        decimal TakeRecall, decimal Accuracy, decimal Brier);
    private sealed record AutoPickResult(int Events, int Eligible, decimal Coverage, decimal Agreement,
        decimal AgreementWhenTaken, decimal HistoricalSkipRate, decimal AverageLead, decimal AverageEvidence,
        IReadOnlyDictionary<string, decimal> CoverageBySlice, IReadOnlyDictionary<string, decimal> AgreementBySlice);
    private sealed record AutoPickPolicy(decimal MinimumScore, decimal MinimumLead, int MaximumSkipProbability);
    private sealed record AutoPickPolicySearch(AutoPickPolicy Policy, IReadOnlyList<object> Candidates);

    private static SkipResult EvaluateSkip(IEnumerable<SkipExample> source, int threshold)
    {
        var rows = source.ToArray();
        var tp = rows.Count(x => x.Skipped && x.Probability >= threshold);
        var fn = rows.Count(x => x.Skipped && x.Probability < threshold);
        var tn = rows.Count(x => !x.Skipped && x.Probability < threshold);
        var fp = rows.Count(x => !x.Skipped && x.Probability >= threshold);
        var skipRecall = (decimal)tp / Math.Max(1, tp + fn); var takeRecall = (decimal)tn / Math.Max(1, tn + fp);
        var brier = rows.Length == 0 ? 0m : rows.Average(x =>
        {
            var probability = x.Probability / 100m; var target = x.Skipped ? 1m : 0m;
            return (probability - target) * (probability - target);
        });
        return new SkipResult(rows.Length, tp + fn, threshold, (skipRecall + takeRecall) / 2m, skipRecall, takeRecall,
            (decimal)(tp + tn) / Math.Max(1, rows.Length), brier);
    }

    private static AutoPickPolicySearch SearchAutoPickPolicy(IEnumerable<AutoPickExample> source)
    {
        var rows = source.ToArray(); var candidates = new List<(AutoPickPolicy Policy, AutoPickResult Result)>();
        foreach (var score in Enumerable.Range(0, 10).Select(x => 78m + x * 2m))
        foreach (var lead in Enumerable.Range(0, 9).Select(x => 8m + x * 4m))
        foreach (var skip in new[] { 8, 11, 14, 17, 20, 23 })
        {
            var policy = new AutoPickPolicy(score, lead, skip);
            var result = EvaluateAutoPick(rows, policy);
            if (result.Eligible >= 200) candidates.Add((policy, result));
        }
        var safe = candidates.Where(x => x.Result.Agreement >= .65m && x.Result.HistoricalSkipRate <= .04m)
            .OrderByDescending(x => x.Result.Coverage).ThenByDescending(x => x.Result.Agreement).FirstOrDefault();
        var selected = safe.Policy is not null ? safe : candidates.OrderByDescending(x => x.Result.Agreement)
            .ThenBy(x => x.Result.HistoricalSkipRate).ThenByDescending(x => x.Result.Coverage).First();
        var leaders = candidates.OrderByDescending(x => x.Result.Agreement).ThenByDescending(x => x.Result.Coverage).Take(12)
            .Select(x => (object)new { x.Policy, x.Result.Eligible, x.Result.Coverage, x.Result.Agreement, x.Result.HistoricalSkipRate }).ToArray();
        return new AutoPickPolicySearch(selected.Policy, leaders);
    }

    private static AutoPickResult EvaluateAutoPick(IEnumerable<AutoPickExample> source, AutoPickPolicy policy)
    {
        var rows = source.ToArray(); var eligible = rows.Where(x => x.Evidence >= .30m && x.BestScore >= policy.MinimumScore &&
            x.Lead >= policy.MinimumLead && x.SkipProbability <= policy.MaximumSkipProbability).ToArray();
        var eligibleTaken = eligible.Where(x => !x.Skipped).ToArray();
        var coverageSlices = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase);
        var agreementSlices = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            foreach (var label in new[] { row.Character, "act-" + row.Act, "deck-" + (row.DeckSize / 8) })
            {
                if (!coverageSlices.TryGetValue(label, out var coverage)) coverageSlices[label] = coverage = new int[2];
                var isEligible = row.Evidence >= .30m && row.BestScore >= policy.MinimumScore &&
                    row.Lead >= policy.MinimumLead && row.SkipProbability <= policy.MaximumSkipProbability;
                if (isEligible) coverage[0]++; coverage[1]++;
                if (!isEligible) continue;
                if (!agreementSlices.TryGetValue(label, out var agreement)) agreementSlices[label] = agreement = new int[2];
                if (row.Agreed) agreement[0]++; agreement[1]++;
            }
        }
        return new AutoPickResult(rows.Length, eligible.Length,
            (decimal)eligible.Length / Math.Max(1, rows.Length),
            (decimal)eligible.Count(x => x.Agreed) / Math.Max(1, eligible.Length),
            (decimal)eligibleTaken.Count(x => x.Agreed) / Math.Max(1, eligibleTaken.Length),
            (decimal)eligible.Count(x => x.Skipped) / Math.Max(1, eligible.Length),
            eligible.Select(x => x.Lead).DefaultIfEmpty(0m).Average(),
            eligible.Select(x => x.Evidence).DefaultIfEmpty(0m).Average(),
            coverageSlices.ToDictionary(x => x.Key, x => (decimal)x.Value[0] / Math.Max(1, x.Value[1]), StringComparer.OrdinalIgnoreCase),
            agreementSlices.ToDictionary(x => x.Key, x => (decimal)x.Value[0] / Math.Max(1, x.Value[1]), StringComparer.OrdinalIgnoreCase));
    }

    private static Result Evaluate(IEnumerable<Example> source, decimal scale)
    {
        var rows = source.ToArray(); decimal fixedHits = 0, brokenHits = 0, learnedHits = 0, hybridHits = 0;
        var slices = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            fixedHits += IsTop(row.Fixed, row.Chosen); brokenHits += IsTop(row.Broken, row.Chosen); learnedHits += IsTop(row.Learned, row.Chosen);
            var average = row.Learned.Values.DefaultIfEmpty(0m).Average();
            var hybrid = row.Fixed.ToDictionary(x => x.Key, x => x.Value + Math.Clamp((row.Learned.GetValueOrDefault(x.Key) - average) * 100m * scale, -10m, 10m), StringComparer.OrdinalIgnoreCase);
            var hit = IsTop(hybrid, row.Chosen); hybridHits += hit;
            foreach (var label in new[] { row.Character, "act-" + row.Act, "deck-band-" + row.DeckBand })
            {
                if (!slices.TryGetValue(label, out var count)) slices[label] = count = new int[2];
                count[0] += hit; count[1]++;
            }
        }
        var n = Math.Max(1, rows.Length);
        return new Result(rows.Length, fixedHits / n, brokenHits / n, learnedHits / n, hybridHits / n,
            slices.ToDictionary(x => x.Key, x => (decimal)x.Value[0] / x.Value[1], StringComparer.OrdinalIgnoreCase));
    }

    private static int IsTop(IReadOnlyDictionary<string, decimal> scores, string chosen)
    {
        if (!scores.TryGetValue(chosen, out var value) || scores.Count == 0) return 0;
        return value >= scores.Values.Max() - .001m ? 1 : 0;
    }

    private static void ProcessRun(string path, string split, IReadOnlyDictionary<string, CardDefinition> catalog,
        IReadOnlyDictionary<string, EntityMetric> fixedMetrics, IReadOnlyDictionary<string, EntityMetric> brokenMetrics,
        IReadOnlyDictionary<string, decimal> learned, List<Example> output, List<SkipExample> skipOutput,
        List<AutoPickExample> autoPickOutput, ref int skipped)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path)); var root = doc.RootElement;
        if (!root.TryGetProperty("players", out var players) || players.GetArrayLength() == 0) return;
        var player = players[0]; var character = Clean(Text(player, "character"));
        var history = HistoricalDeck(root, player); var relics = HistoricalRelics(player);
        var ascension = Number(root, "ascension"); var floor = 0; var act = 0;
        foreach (var actElement in root.GetProperty("map_point_history").EnumerateArray())
        {
            act++;
            foreach (var point in actElement.EnumerateArray())
            {
                floor++;
                if (Text(point, "map_point_type").Equals("shop", StringComparison.OrdinalIgnoreCase)) continue;
                var deck = history.Where(x => (x.Added == 1 || x.Added < floor) && (x.Removed is null || x.Removed >= floor)).Select(x => x.Id).ToArray();
                var currentRelics = relics.Where(x => x.Added == 1 || x.Added < floor).Select(x => x.Id).ToArray();
                if (!point.TryGetProperty("player_stats", out var statsRows)) continue;
                foreach (var stats in statsRows.EnumerateArray())
                {
                    if (!stats.TryGetProperty("card_choices", out var choices) || choices.GetArrayLength() < 2) continue;
                    var offered = choices.EnumerateArray().Select(x => Clean(Text(x.GetProperty("card"), "id"))).Where(x => x.Length > 0).Distinct().ToArray();
                    var chosen = choices.EnumerateArray().FirstOrDefault(x => Bool(x, "was_picked"));
                    var chosenId = chosen.ValueKind == JsonValueKind.Undefined ? "" : Clean(Text(chosen.GetProperty("card"), "id"));
                    var hp = Number(stats, "current_hp"); var maxHp = Math.Max(hp, Number(stats, "max_hp")); var gold = Number(stats, "current_gold");
                    var context = new RunContext(character.ToLowerInvariant(), deck, currentRelics, ascension, act, floor, hp, Math.Max(1, maxHp), gold, null, 0, 0, 0, 0, []);
                    var fixedAssessment = DeckBuildScoring.Evaluate(context, offered, catalog, fixedMetrics);
                    var confidence = DecisionConfidenceModel.Evaluate(fixedAssessment);
                    var best = fixedAssessment.Offers.OrderByDescending(x => x.Final).FirstOrDefault();
                    var bestId = best?.Id ?? "";
                    var isSkipped = chosenId.Length == 0;
                    skipOutput.Add(new SkipExample(split, isSkipped, fixedAssessment.SkipScore));
                    autoPickOutput.Add(new AutoPickExample(split, character.ToLowerInvariant(), act, deck.Length,
                        isSkipped, !isSkipped && bestId.Equals(chosenId, StringComparison.OrdinalIgnoreCase), best?.Final ?? 0m,
                        confidence.Lead, confidence.Evidence, fixedAssessment.SkipScore));
                    if (chosenId.Length == 0) { skipped++; continue; }
                    var fixedScores = fixedAssessment.Offers.ToDictionary(x => x.Id, x => x.Final, StringComparer.OrdinalIgnoreCase);
                    var brokenScores = DeckBuildScoring.Evaluate(context, offered, catalog, brokenMetrics).Offers.ToDictionary(x => x.Id, x => x.Final, StringComparer.OrdinalIgnoreCase);
                    var copies = deck.GroupBy(x => x, StringComparer.OrdinalIgnoreCase).ToDictionary(x => x.Key, x => x.Count(), StringComparer.OrdinalIgnoreCase);
                    var deckBand = Math.Min(5, deck.Length / 8);
                    var learnedScores = offered.ToDictionary(id => id, id => learned.GetValueOrDefault(Key(character, id, act, deckBand, Math.Min(3, copies.GetValueOrDefault(id))), .24m), StringComparer.OrdinalIgnoreCase);
                    output.Add(new Example(split, character.ToLowerInvariant(), act, deckBand, offered, chosenId, fixedScores, brokenScores, learnedScores));
                }
            }
        }
    }

    private static IReadOnlyDictionary<string, decimal> ParseLearned(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path)); var result = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in doc.RootElement.GetProperty("choice_contexts").EnumerateArray())
            result[Key(Text(row, "character"), Text(row, "card"), Number(row, "act"), Number(row, "deck_band"), Number(row, "copies_before"))] = row.GetProperty("pick_rate").GetDecimal();
        return result;
    }

    private static string Key(string character, string card, int act, int band, int copies) => $"{character}|{Clean(card)}|{act}|{band}|{copies}";
    private static List<HistoricalCard> HistoricalDeck(JsonElement root, JsonElement player)
    {
        var result = player.GetProperty("deck").EnumerateArray().Select(x => new HistoricalCard(Clean(Text(x, "id")), Math.Max(1, Number(x, "floor_added_to_deck")), null)).ToList();
        var floor = 0;
        foreach (var act in root.GetProperty("map_point_history").EnumerateArray()) foreach (var point in act.EnumerateArray())
        {
            floor++;
            if (!point.TryGetProperty("player_stats", out var rows)) continue;
            foreach (var stats in rows.EnumerateArray()) if (stats.TryGetProperty("cards_removed", out var removed)) foreach (var card in removed.EnumerateArray())
                result.Add(new HistoricalCard(Clean(Text(card, "id")), Math.Max(1, Number(card, "floor_added_to_deck")), floor));
        }
        return result.Where(x => x.Id.Length > 0).ToList();
    }
    private static List<HistoricalCard> HistoricalRelics(JsonElement player) => !player.TryGetProperty("relics", out var rows) ? [] :
        rows.EnumerateArray().Select(x => new HistoricalCard(Clean(Text(x, "id")), Math.Max(1, Number(x, "floor_added_to_deck")), null)).Where(x => x.Id.Length > 0).ToList();

    private static IReadOnlyDictionary<string, CardDefinition> ParseCatalog(string json)
    {
        using var doc = JsonDocument.Parse(json); var result = new Dictionary<string, CardDefinition>(StringComparer.OrdinalIgnoreCase);
        var rows = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray() : doc.RootElement.GetProperty("rows").EnumerateArray();
        foreach (var row in rows)
        {
            var id = Clean(Text(row, "id")); if (id.Length == 0) continue;
            var card = new CardDefinition(id, Text(row, "name"), Text(row, "color"), Text(row, "type_key", "type"), Number(row, "cost"), Number(row, "damage"), Number(row, "block"), Number(row, "cards_draw"), Number(row, "energy_gain"), Text(row, "rarity_key", "rarity"), Text(row, "description", "description_raw"), new HashSet<string>(), null);
            result[id] = card;
        }
        return result;
    }
    private static string Clean(string value) => value.Split('.').LastOrDefault()?.Trim().Replace(' ', '_').Replace('-', '_').ToUpperInvariant() ?? "";
    private static string Text(JsonElement row, params string[] names) { foreach (var name in names) if (row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String) return value.GetString() ?? ""; return ""; }
    private static int Number(JsonElement row, string name) => row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : 0;
    private static bool Bool(JsonElement row, string name) => row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}

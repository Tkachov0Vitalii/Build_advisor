using System.Text.Json;
using BuildAdvisor.Data;

namespace BuildAdvisor.Integration;

// One event option as Spire Codex reports it: /api/runs/metrics/events → {event, option, share,
// win_rate, lift, lift_n}. Rates are percent; Lift is percentage points of win rate.
internal sealed record EventOptionMetric(string Event, string Option, decimal Share, decimal WinRate, decimal? Lift, int LiftSamples);

// One option on screen. Key parts come from EventOption.TextKey
// ("SELF_HELP_BOOK.pages.INITIAL.options.READ_ENTIRE_BOOK"); Ancient options carry a relic.
internal sealed record EventChoice(string Text, string? EventId, string? OptionId, string? RelicId, bool Lethal);

internal static class EventKeys
{
    public static (string? Event, string? Option) Parse(string? textKey)
    {
        if (string.IsNullOrWhiteSpace(textKey)) return (null, null);
        var eventId = textKey.Split('.')[0];
        var marker = textKey.LastIndexOf(".options.", StringComparison.Ordinal);
        var option = marker < 0 ? null : textKey[(marker + ".options.".Length)..].Split('.')[0];
        return (SpireCodexMetricsClient.NormalizeId(eventId), option is null ? null : SpireCodexMetricsClient.NormalizeId(option));
    }
}

internal sealed class EventMetricsClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly Dictionary<string, (DateTimeOffset Until, IReadOnlyDictionary<string, EventOptionMetric> Data)> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static string Key(string eventId, string optionId) => SpireCodexMetricsClient.NormalizeId(eventId) + "|" + SpireCodexMetricsClient.NormalizeId(optionId);

    // 7.5: the played game version's slice first, the mixed statistics if it fails.
    public async Task<IReadOnlyDictionary<string, EventOptionMetric>> GetAsync(string bracket, CancellationToken token = default)
    {
        var versioned = StatsVersion.Apply(bracket, await StatsVersion.ResolveAsync(TimeSpan.FromSeconds(5)));
        if (versioned != bracket)
        {
            try { return await FetchAsync(versioned, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception) { }
        }
        return await FetchAsync(bracket, token);
    }

    private async Task<IReadOnlyDictionary<string, EventOptionMetric>> FetchAsync(string bracket, CancellationToken token = default)
    {
        if (Cache.TryGetValue(bracket, out var cached) && cached.Until > DateTimeOffset.UtcNow) return cached.Data;
        await Gate.WaitAsync(token);
        try
        {
            if (Cache.TryGetValue(bracket, out cached) && cached.Until > DateTimeOffset.UtcNow) return cached.Data;
            var disk = ModPaths.UserData($"events-{bracket.Replace(':', '_')}-v1.json");
            // Event statistics move slowly: a day-old copy is fresh enough, any copy beats none.
            if (File.Exists(disk) && (NetworkSettings.Offline || DateTime.UtcNow - File.GetLastWriteTimeUtc(disk) < TimeSpan.FromHours(24)))
            {
                var saved = await File.ReadAllTextAsync(disk, token);
                var fromDisk = await Task.Run(() => Parse(saved), token);
                if (fromDisk.Count > 0) { Cache[bracket] = (DateTimeOffset.UtcNow.AddHours(1), fromDisk); return fromDisk; }
            }
            NetworkSettings.ThrowIfOffline();
            var json = await Http.GetStringAsync($"https://spire-codex.com/api/runs/metrics/events?bracket={Uri.EscapeDataString(bracket)}", token);
            var data = await Task.Run(() => Parse(json), token);
            if (data.Count == 0) throw new InvalidDataException("Spire Codex returned no event metrics.");
            Directory.CreateDirectory(Path.GetDirectoryName(disk)!);
            await File.WriteAllTextAsync(disk + ".tmp", json, token);
            File.Move(disk + ".tmp", disk, true);
            Cache[bracket] = (DateTimeOffset.UtcNow.AddHours(1), data);
            return data;
        }
        finally { Gate.Release(); }
    }

    internal static IReadOnlyDictionary<string, EventOptionMetric> Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var rows = root.ValueKind == JsonValueKind.Array ? root
            : root.TryGetProperty("rows", out var r) ? r : root.TryGetProperty("data", out var d) ? d : default;
        var result = new Dictionary<string, EventOptionMetric>(StringComparer.OrdinalIgnoreCase);
        if (rows.ValueKind != JsonValueKind.Array) return result;
        foreach (var row in rows.EnumerateArray())
        {
            if (!row.TryGetProperty("event", out var e) || !row.TryGetProperty("option", out var o) || e.GetString() is not { } eventId || o.GetString() is not { } optionId) continue;
            decimal Num(string name) => row.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : 0m;
            decimal? lift = row.TryGetProperty("lift", out var l) && l.ValueKind == JsonValueKind.Number ? l.GetDecimal() : null;
            result[Key(eventId, optionId)] = new EventOptionMetric(eventId, optionId, Num("share"), Num("win_rate"), lift, (int)Num("lift_n"));
        }
        return result;
    }
}

// M2: event options rated by what the option actually led to across the community, instead of
// keyword guesses from the option text. Ancient options that grant a relic use that relic's lift.
// Options without data keep the local keyword hint.
internal static class EventAdvisor
{
    internal const int MinimumSamples = 100;

    internal const string AncientNote = "Реликвии Древнего сравниваются с другими выборами у Древнего в том же акте по корпусу 139 тыс. забегов: все эти забеги дошли до одной точки, поэтому выживание не завышает оценку.";

    // Preferred: the verdict of a rule-based event (source "bridge"), which has no lift to rank by.
    internal sealed record Rated(EventChoice Choice, decimal? Lift, int Samples, string Source, string Line, bool Preferred = false);

    internal const string BridgeNote = "Скользкий мост: статистика Spire Codex здесь не используется — дольше держатся только забеги с запасом HP, поэтому у «Удержать равновесие» завышенный винрейт. Совет сравнивает карту, которую вы потеряете, со следующей случайной и ценой в HP.";
    internal const string BridgeUnknown = "совет недоступен: карта на кону не прочитана (статистика этого события искажена и не используется)";

    public static IReadOnlyList<Rated> Rate(IReadOnlyList<EventChoice> choices, IReadOnlyDictionary<string, EventOptionMetric> events,
        IReadOnlyDictionary<string, EntityMetric> relics, IReadOnlyDictionary<string, string>? relicNames, Func<string, string> localHint,
        RunContext? context = null, decimal? segmentRisk = null, BridgeAdvice? bridge = null)
    {
        var rated = choices.Select(choice =>
        {
            // 7.6: Slippery Bridge never uses its survivor-biased lift (SlipperyBridgeAdvisor).
            if (choice.EventId == SlipperyBridgeAdvisor.EventId)
            {
                var hold = choice.OptionId?.StartsWith("HOLD_ON", StringComparison.Ordinal) == true;
                if (bridge is null) return new Rated(choice, null, 0, "bridge", BridgeUnknown);
                return hold
                    ? new Rated(choice, null, 0, "bridge", bridge.HoldLine, bridge.HoldOn == true)
                    : new Rated(choice, null, 0, "bridge", bridge.OvercomeLine, bridge.HoldOn == false);
            }
            if (RelicOf(choice, relics, relicNames) is { } relicId && relics.TryGetValue(SpireCodexMetricsClient.NormalizeId(relicId), out var relic) && relic.Lift is { } relicLift)
            {
                var name = relicNames is not null && relicNames.TryGetValue(SpireCodexMetricsClient.NormalizeId(relicId), out var localized) ? localized : relic.Name;
                // M9: with the run known, the relic is scored against the deck (energy, survival, economy).
                if (context is not null)
                {
                    var scored = RelicAdvisor.Score(context, relicId, relics, relicNames, segmentRisk);
                    // 7.4: an Ancient relic is valued at the moment of choice (AncientRelicValues):
                    // corpus value at this act plus the deck adjustments, instead of the run-long lift.
                    if (AncientRelicValues.Value(context.Character, context.Act, relicId) is { } ancient)
                    {
                        var deckShift = (scored.Final - scored.Community) * TakeSkipModel.PointsPerScore;
                        var value = decimal.Round(ancient.Pp + deckShift, 1);
                        var scope = ancient.ForCharacter ? "этого персонажа" : "всех персонажей";
                        return new Rated(choice, value, ancient.Runs, "ancient",
                            $"реликвия {name}: {TakeSkipModel.Format(value)} п.п. к победе против среднего выбора у Древнего в акте {Math.Clamp(context.Act, 1, 3)} " +
                            $"({ancient.Runs} забегов {scope}) · уверенность {TakeSkipModel.EvidenceLabel(ancient.Runs)}" +
                            (deckShift != 0m ? $" — {scored.Reason}" : ""));
                    }
                    var delta = TakeSkipModel.WinDelta(scored);
                    return new Rated(choice, delta, relic.LiftSamples, "relic",
                        $"реликвия {name}: {TakeSkipModel.Format(delta)} п.п. к победе · уверенность {TakeSkipModel.EvidenceLabel(relic.LiftSamples)} — {scored.Reason}");
                }
                return new Rated(choice, relicLift, relic.LiftSamples, "relic",
                    $"реликвия {name}: {TakeSkipModel.Format(relicLift)} п.п. к победе · уверенность {TakeSkipModel.EvidenceLabel(relic.LiftSamples)}");
            }
            if (choice.EventId is { } eventId && choice.OptionId is { } optionId && events.TryGetValue(EventMetricsClient.Key(eventId, optionId), out var metric) &&
                metric.Lift is { } lift && metric.LiftSamples >= MinimumSamples)
                return new Rated(choice, lift, metric.LiftSamples, "event",
                    $"{TakeSkipModel.Format(lift)} п.п. к победе · винрейт {metric.WinRate:0.#}% · выбирают {metric.Share:0.#}% · уверенность {TakeSkipModel.EvidenceLabel(metric.LiftSamples)}");
            return new Rated(choice, null, 0, "local", localHint(choice.Text));
        }).ToArray();
        return rated;
    }

    // The relic an option grants: the game's EventOption.Relic, else the option ID (Ancient options
    // are named after their relic: TEZCATARA…options.SEAL_OF_GOLD), else the relic whose localized
    // name opens the option text. 6.22.4 found none for Tezcatara and showed only local hints.
    internal static string? RelicOf(EventChoice choice, IReadOnlyDictionary<string, EntityMetric> relics, IReadOnlyDictionary<string, string>? names)
    {
        if (choice.RelicId is { Length: > 0 } direct) return direct;
        if (choice.OptionId is { } option && relics.ContainsKey(SpireCodexMetricsClient.NormalizeId(option))) return option;
        if (names is null) return null;
        var text = choice.Text.TrimStart();
        return names.Where(x => x.Value.Length >= 3 && relics.ContainsKey(SpireCodexMetricsClient.NormalizeId(x.Key)) &&
                text.StartsWith(x.Value, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => x.Value.Length).Select(x => x.Key).FirstOrDefault();
    }

    // The best option is marked only among options with data and only if it is not lethal.
    // A rule-based event (Slippery Bridge) marks its preferred option instead.
    public static Rated? Best(IReadOnlyList<Rated> rated) =>
        rated.Any(x => x.Source == "bridge")
            ? rated.FirstOrDefault(x => x.Source == "bridge" && x.Preferred && !x.Choice.Lethal)
            : rated.Where(x => x.Lift is not null && !x.Choice.Lethal && TakeSkipModel.EvidenceLabel(x.Samples) != "низкая")
                .OrderByDescending(x => x.Lift).FirstOrDefault();

    // Short on-screen verdict for an option button; options without data get none.
    public static (string Text, string Severity, bool Best) Tag(Rated rated, Rated? best)
    {
        if (rated.Choice.Lethal) return ("⚠ смерть", "high", false);
        if (rated.Source == "bridge")
            return best is null ? ("", "low", false) : ReferenceEquals(rated, best) ? ("★ лучше", "good", true) : ("не стоит", "medium", false);
        if (rated.Lift is not { } lift) return ("", "low", false);
        var isBest = ReferenceEquals(rated, best);
        return ($"{TakeSkipModel.Format(lift)} п.п.", isBest ? "good" : lift < 0m ? "medium" : "low", isBest);
    }

    public static string Format(IReadOnlyList<Rated> rated)
    {
        var best = Best(rated);
        var lines = rated.Select((x, index) =>
            $"{index + 1}. {(x.Choice.Lethal ? EventOptionPatch.DeadlyWarning : "")}{(ReferenceEquals(x, best) ? "★ " : "")}{x.Choice.Text} — {x.Line}");
        var withData = rated.Count(x => x.Lift is not null);
        var footer = withData == 0
            ? "Статистики по этому событию нет — показаны локальные подсказки."
            : "Лифт — насколько винрейт забегов, выбравших вариант, выше или ниже среднего по данным Spire Codex. Это корреляция: проверьте цену варианта (HP, золото, проклятия) для своей колоды.";
        if (rated.Any(x => x.Source == "ancient"))
            footer = AncientNote + "\n" + footer;
        if (rated.Any(x => x.Source == "bridge"))
            footer = BridgeNote;
        return "Ветки события:\n" + string.Join("\n", lines) + "\n\n" + footer;
    }
}

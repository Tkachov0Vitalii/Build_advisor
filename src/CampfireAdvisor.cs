using System.Text.Json;
using BuildAdvisor.Data;

namespace BuildAdvisor.Integration;

// One campfire choice as Spire Codex reports it: /api/runs/metrics/campfires → {choice, name,
// share, win_rate, lift, lift_n, low_hp_share}. Rates are percent.
internal sealed record CampfireMetric(string Choice, string Name, decimal Share, decimal WinRate, decimal? Lift, int LiftSamples, decimal LowHpShare);

// M6: rest or upgrade (and which card). Community lift for campfires is shown but not used to
// decide: players rest when they are already in trouble (65% of rests happen at low HP), so Rest
// carries a negative lift that says nothing about whether resting helps this run. The decision
// compares the HP the rest restores, weighted by the predicted risk of the next three nodes, with
// the value of the best upgrade on the same 0-100 point scale as card scores.
internal static class CampfireAdvisor
{
    internal const string Heal = "HEAL";
    internal const string Smith = "SMITH";

    internal sealed record Advice(string? Recommended, decimal RestValue, decimal UpgradeValue, string? UpgradeTarget, string Text);

    // Points per whole max HP restored before the risk weight; 30% heal at average risk ≈ 8 points,
    // the same as a typical +3 damage / +1 draw upgrade.
    private const decimal HealPoints = 30m;

    public static decimal RestValue(RunContext context, decimal healAmount, decimal segmentRisk)
    {
        if (context.MaxHp <= 0) return 0m;
        var missing = Math.Max(0, context.MaxHp - context.Hp);
        var effective = Math.Min(healAmount, missing) / context.MaxHp;
        var bossNext = context.Route.Options.Any(x => x.Next == "boss") || context.Route.Current == "boss";
        var weight = .6m + 1.6m * Math.Clamp(segmentRisk, 0m, 1m) + (bossNext ? .6m : 0m);
        var value = effective * HealPoints * weight;
        // Below a third of max HP one bad fight ends the run: resting wins unless HP is already full.
        if (context.Hp * 3 <= context.MaxHp && effective > 0) value += 15m;
        return decimal.Round(value, 1);
    }

    public static Advice Advise(RunContext context, IReadOnlyList<string> options, decimal? healAmount, decimal? segmentRisk,
        (string Name, decimal Value)? upgrade, IReadOnlyDictionary<string, CampfireMetric>? metrics = null)
    {
        var ids = options.Select(x => x.ToUpperInvariant()).Distinct().ToArray();
        var risk = segmentRisk ?? .2m;
        // The game heals 30% of max HP by default; relics change it, so the real amount wins when read.
        var heal = healAmount ?? Math.Round(context.MaxHp * .3m);
        var rest = ids.Contains(Heal) ? RestValue(context, heal, risk) : 0m;
        var smith = ids.Contains(Smith) && upgrade is { } best ? best.Value : 0m;
        string? recommended = null;
        if (ids.Contains(Heal) && (rest > smith || !ids.Contains(Smith) || upgrade is null) && rest > 0) recommended = Heal;
        else if (ids.Contains(Smith) && upgrade is not null) recommended = Smith;
        else if (ids.Contains(Heal)) recommended = Heal;

        var lines = new List<string>();
        var hpLine = context.MaxHp > 0 ? $"HP {context.Hp}/{context.MaxHp} · отдых восстановит {Math.Min(heal, Math.Max(0, context.MaxHp - context.Hp)):0}" : "HP не прочитано";
        lines.Add(hpLine + (segmentRisk is { } r ? $" · риск следующих узлов {r * 100m:0}%" : ""));
        if (recommended == Heal)
            lines.Add($"Совет: отдых ({rest:0.#} балла против {smith:0.#} у улучшения)" + (context.Hp * 3 <= context.MaxHp ? " — HP ниже трети, следующий тяжёлый бой опасен." : "."));
        else if (recommended == Smith && upgrade is { } chosen)
            lines.Add($"Совет: улучшить {chosen.Name} ({smith:0.#} балла против {rest:0.#} у отдыха).");
        else
            lines.Add("Совет: отдых и улучшение недоступны — сравните особые варианты по описанию.");
        if (ids.Contains(Smith) && upgrade is null) lines.Add("Подходящей карты для улучшения не найдено.");
        else if (recommended == Heal && upgrade is { } skipped) lines.Add($"Лучшее улучшение, если HP хватает: {skipped.Name}.");

        if (metrics is { Count: > 0 })
        {
            var known = ids.Select(id => metrics.TryGetValue(id, out var m) ? m : null).Where(m => m is not null).Cast<CampfireMetric>().ToArray();
            if (known.Length > 0)
            {
                lines.Add("");
                lines.Add("Статистика сообщества (справочно):");
                lines.AddRange(known.Select(m => $"{m.Name}: выбирают {m.Share:0.#}% · {TakeSkipModel.Format(m.Lift ?? 0m)} п.п. к победе · при низком HP {m.LowHpShare:0}%"));
                lines.Add("Отдыхают в основном при низком HP, поэтому его лифт отрицательный; совет выше учитывает ваше HP и риск.");
            }
        }
        return new Advice(recommended, rest, smith, upgrade?.Name, string.Join("\n", lines));
    }

    internal static IReadOnlyDictionary<string, CampfireMetric> Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var rows = root.ValueKind == JsonValueKind.Array ? root : root.TryGetProperty("rows", out var r) ? r : default;
        var result = new Dictionary<string, CampfireMetric>(StringComparer.OrdinalIgnoreCase);
        if (rows.ValueKind != JsonValueKind.Array) return result;
        foreach (var row in rows.EnumerateArray())
        {
            if (!row.TryGetProperty("choice", out var c) || c.GetString() is not { } choice) continue;
            decimal Num(string name) => row.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : 0m;
            decimal? lift = row.TryGetProperty("lift", out var l) && l.ValueKind == JsonValueKind.Number ? l.GetDecimal() : null;
            var name = row.TryGetProperty("name", out var n) && n.GetString() is { } text ? text : choice;
            result[choice.ToUpperInvariant()] = new CampfireMetric(choice, name, Num("share"), Num("win_rate"), lift, (int)Num("lift_n"), Num("low_hp_share"));
        }
        return result;
    }
}

internal sealed class CampfireMetricsClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly Dictionary<string, (DateTimeOffset Until, IReadOnlyDictionary<string, CampfireMetric> Data)> Cache = new(StringComparer.OrdinalIgnoreCase);

    public async Task<IReadOnlyDictionary<string, CampfireMetric>> GetAsync(string bracket, string language, CancellationToken token = default)
    {
        var key = bracket + ":" + language;
        if (Cache.TryGetValue(key, out var cached) && cached.Until > DateTimeOffset.UtcNow) return cached.Data;
        await Gate.WaitAsync(token);
        try
        {
            if (Cache.TryGetValue(key, out cached) && cached.Until > DateTimeOffset.UtcNow) return cached.Data;
            var disk = ModPaths.UserData($"campfires-{bracket}-{language}-v1.json");
            if (File.Exists(disk) && (NetworkSettings.Offline || DateTime.UtcNow - File.GetLastWriteTimeUtc(disk) < TimeSpan.FromHours(24)))
            {
                var saved = await File.ReadAllTextAsync(disk, token);
                var fromDisk = await Task.Run(() => CampfireAdvisor.Parse(saved), token);
                if (fromDisk.Count > 0) { Cache[key] = (DateTimeOffset.UtcNow.AddHours(1), fromDisk); return fromDisk; }
            }
            NetworkSettings.ThrowIfOffline();
            var json = await Http.GetStringAsync($"https://spire-codex.com/api/runs/metrics/campfires?bracket={Uri.EscapeDataString(bracket)}&lang={Uri.EscapeDataString(language)}", token);
            var data = await Task.Run(() => CampfireAdvisor.Parse(json), token);
            if (data.Count == 0) throw new InvalidDataException("Spire Codex returned no campfire metrics.");
            Directory.CreateDirectory(Path.GetDirectoryName(disk)!);
            await File.WriteAllTextAsync(disk + ".tmp", json, token);
            File.Move(disk + ".tmp", disk, true);
            Cache[key] = (DateTimeOffset.UtcNow.AddHours(1), data);
            return data;
        }
        finally { Gate.Release(); }
    }
}

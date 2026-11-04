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

    // 7.1: what relics add to the campfire. The game adds options through
    // RelicModel.TryModifyRestSiteOptions (Shovel → DIG, Girya → LIFT, Meat Cleaver → COOK,
    // Pael's Growth → CLONE, Pumpkin Candle → KINDLE, Byrdonis Egg → HATCH, co-op → MEND), keeps
    // the rest open after a choice with Miniature Tent (ShouldDisableRemainingRestSiteOptions) and
    // adds rewards to resting (Dream Catcher: a card reward, Tiny Mailbox: two potions, Stone
    // Humidifier: +5 max HP; Regal Pillow's +15 heal is already in the heal amount).
    internal sealed record CampfireExtras(int? GiryaLifts = null, int CloneCards = 0, int? KindleLeft = null,
        int RemovalTargets = 0, decimal? WeakestTeammateHp = null)
    {
        public static readonly CampfireExtras None = new();
    }

    internal const string Dig = "DIG";
    internal const string Lift = "LIFT";
    internal const string Cook = "COOK";
    internal const string Clone = "CLONE";
    internal const string Hatch = "HATCH";
    internal const string Kindle = "KINDLE";
    internal const string Mend = "MEND";

    internal static bool HasRelic(RunContext context, string compactId) =>
        context.Relics.Any(id => SpireCodexMetricsClient.NormalizeId(id).Replace("_", "") == compactId);

    internal static string OptionName(string id) => id switch
    {
        Heal => "отдых", Smith => "улучшение", Dig => "копать (лопата)", Lift => "тренировка (гиря)",
        Cook => "готовить (тесак)", Clone => "клонировать карты", Hatch => "высидеть яйцо", Kindle => "разжечь свечу",
        Mend => "лечить союзника", _ => id.ToLowerInvariant()
    };

    // Rest-only rewards in points: a card reward ≈ 4, two potions ≈ 3, +5 max HP ≈ 3.
    internal static (decimal Points, string Note) RestBonus(RunContext context)
    {
        var points = 0m; var notes = new List<string>();
        if (HasRelic(context, "DREAMCATCHER")) { points += 4m; notes.Add("ловец снов: награда картой"); }
        if (HasRelic(context, "TINYMAILBOX")) { points += 3m; notes.Add("почтовый ящик: 2 зелья"); }
        if (HasRelic(context, "STONEHUMIDIFIER")) { points += 3m; notes.Add("увлажнитель: +5 к макс. HP"); }
        return (points, string.Join(", ", notes));
    }

    // Value of one option on the 0-100 action point scale shared with rest and upgrade.
    internal static (decimal Value, string Why) OptionValue(string id, RunContext context, CampfireExtras extras)
    {
        var late = context.Act >= 3 ? -3m : context.Act <= 1 ? 3m : 0m;
        switch (id)
        {
            case Dig:
                return (12m + late, "случайная реликвия — обычно сильнее среднего улучшения, особенно в начале забега");
            case Lift:
            {
                var left = extras.GiryaLifts is { } done ? Math.Max(0, 3 - done) : 3;
                var attacks = Math.Min(6m, context.AttackCards / 2m);
                return left <= 0 ? (0m, "гиря уже поднята 3 раза")
                    : (8m + attacks + late * 2m / 3m, $"+1 сила в каждом бою до конца забега (подъёмов осталось {left}); ценнее с большим числом атак");
            }
            case Cook:
            {
                var targets = Math.Min(2, extras.RemovalTargets);
                var value = targets * 9m + 3m - (2 - targets) * 6m;
                return (value, targets == 2 ? "удаляет 2 слабые карты и даёт +9 к макс. HP"
                    : targets == 1 ? "удаляет 1 слабую карту, вторая — хорошая; +9 к макс. HP"
                    : "в колоде нет слабых карт для удаления — потеряете хорошие");
            }
            case Clone:
                return extras.CloneCards <= 0 ? (0m, "в колоде нет карт с зачарованием «Клон»")
                    : (Math.Min(24m, extras.CloneCards * 7m), $"копирует {extras.CloneCards} карт(ы) с зачарованием «Клон»");
            case Hatch:
                return (14m + late, "питомец-реликвия на весь забег");
            case Kindle:
            {
                var left = extras.KindleLeft ?? 0;
                return (Math.Max(0m, (5 - left) * 3.5m), left <= 0 ? "свеча погасла: +1 энергия на 5 боёв" : $"свеча горит ещё {left} боёв — разжечь сейчас добавит {5 - left}");
            }
            case Mend:
            {
                if (extras.WeakestTeammateHp is not { } share) return (0m, "здоровье союзников не прочитано");
                var value = share <= 1m / 3m ? 20m : Math.Max(0m, (1m - share) * 12m);
                return (decimal.Round(value, 1), $"самый раненый союзник: {share * 100m:0}% HP");
            }
            default:
                return (0m, "особый вариант — сравните по описанию");
        }
    }

    public static Advice Advise(RunContext context, IReadOnlyList<string> options, decimal? healAmount, decimal? segmentRisk,
        (string Name, decimal Value)? upgrade, IReadOnlyDictionary<string, CampfireMetric>? metrics = null, CampfireExtras? extras = null)
    {
        extras ??= CampfireExtras.None;
        var ids = options.Select(x => x.ToUpperInvariant()).Distinct().ToArray();
        var risk = segmentRisk ?? .2m;
        // The game heals 30% of max HP by default; relics change it, so the real amount wins when read.
        var heal = healAmount ?? Math.Round(context.MaxHp * .3m);
        var restBonus = RestBonus(context);
        var rest = ids.Contains(Heal) ? RestValue(context, heal, risk) + restBonus.Points : 0m;
        var smith = ids.Contains(Smith) && upgrade is { } best ? best.Value : 0m;
        var values = new Dictionary<string, (decimal Value, string Why)>();
        if (ids.Contains(Heal)) values[Heal] = (rest, restBonus.Note);
        if (ids.Contains(Smith)) values[Smith] = (smith, upgrade is { } u ? "улучшить " + u.Name : "нет карты для улучшения");
        foreach (var id in ids.Where(x => x is not Heal and not Smith)) values[id] = OptionValue(id, context, extras);

        var ranked = values.OrderByDescending(x => x.Value.Value).ThenBy(x => x.Key == Smith ? 0 : x.Key == Heal ? 1 : 2).ToArray();
        string? recommended = null;
        if (ranked.Length > 0 && ranked[0].Value.Value > 0 && !(ranked[0].Key == Smith && upgrade is null)) recommended = ranked[0].Key;
        else if (ids.Contains(Heal)) recommended = Heal;
        else if (ids.Contains(Smith) && upgrade is not null) recommended = Smith;
        var tent = HasRelic(context, "MINIATURETENT");

        var lines = new List<string>();
        var hpLine = context.MaxHp > 0 ? $"HP {context.Hp}/{context.MaxHp} · отдых восстановит {Math.Min(heal, Math.Max(0, context.MaxHp - context.Hp)):0}" : "HP не прочитано";
        lines.Add(hpLine + (segmentRisk is { } r ? $" · риск следующих узлов {r * 100m:0}%" : ""));
        var runnerUp = ranked.FirstOrDefault(x => x.Key != recommended);
        var against = runnerUp.Key is null ? "" : $" против {runnerUp.Value.Value:0.#} у варианта «{OptionName(runnerUp.Key)}»";
        if (recommended == Heal)
            lines.Add($"Совет: отдых ({rest:0.#} балла{against})" + (context.Hp * 3 <= context.MaxHp ? " — HP ниже трети, следующий тяжёлый бой опасен." : "."));
        else if (recommended == Smith && upgrade is { } chosen)
            lines.Add($"Совет: улучшить {chosen.Name} ({smith:0.#} балла{against}).");
        else if (recommended is not null)
            lines.Add($"Совет: {OptionName(recommended)} ({values[recommended].Value:0.#} балла{against}) — {values[recommended].Why}.");
        else
            lines.Add("Совет: отдых и улучшение недоступны — сравните особые варианты по описанию.");
        if (ids.Contains(Smith) && upgrade is null) lines.Add("Подходящей карты для улучшения не найдено.");
        else if (recommended == Heal && upgrade is { } skipped) lines.Add($"Лучшее улучшение, если HP хватает: {skipped.Name}.");
        if (restBonus.Points > 0 && ids.Contains(Heal)) lines.Add($"Отдых также даёт: {restBonus.Note} (+{restBonus.Points:0} балла).");
        var special = ids.Where(x => x is not Heal and not Smith).ToArray();
        if (special.Length > 0)
        {
            lines.Add("Варианты от реликвий:");
            lines.AddRange(special.Select(id => $"• {OptionName(id)}: {values[id].Value:0.#} балла — {values[id].Why}"));
        }
        if (tent && ranked.Length > 1)
            lines.Add("Миниатюрная палатка: после выбора остальные варианты не блокируются — используйте все по порядку: " +
                      string.Join(" → ", ranked.Where(x => x.Value.Value > 0 || x.Key == Heal).Select(x => OptionName(x.Key))) + ".");

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

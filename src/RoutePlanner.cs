using System.Text.Json;
using BuildAdvisor.Data;

namespace BuildAdvisor.Integration;

// H1 (the Spire Codex mod and Winrate Preview mark the map; we add HP simulation): every path
// from the current map point to the boss is walked with the expected damage of each fight
// (Spire Codex encounter statistics for this act, room type and character), rests are taken
// when the campfire model would rest, and paths are compared on one point scale:
// rewards (card ≈ 5, relic ≈ 12, upgrade ≈ 8) minus a danger penalty that grows with the share
// of current HP a fight is expected to take, plus the HP left for the boss.
internal sealed record RouteNode(string Id, string Type, IReadOnlyList<string> Children);
internal sealed record RouteGraph(IReadOnlyList<string> Starts, IReadOnlyDictionary<string, RouteNode> Nodes);
internal sealed record RouteDamage(decimal Monster, decimal Elite, decimal Boss, decimal Fatal = 0m);
internal sealed record NodeForecast(string Id, string Type, decimal Damage, decimal Heal, decimal HpBefore, decimal HpAfter, decimal Danger, bool Rest, bool OnBest);
internal sealed record RoutePath(IReadOnlyList<NodeForecast> Nodes, decimal Score, decimal Rewards, decimal Penalty, decimal HpAtBoss, bool Lethal);
internal sealed record RoutePlan(RoutePath? Best, IReadOnlyList<RoutePath> Top, IReadOnlyDictionary<string, NodeForecast> ByNode, int PathCount);

internal static class RoutePlanner
{
    internal const int PathLimit = 40_000;
    // Damage shares above this start to cost points; 40% of current HP in one fight is a warning.
    private const decimal DangerFree = .15m;
    private const decimal DangerWeight = 30m;

    // A10 averages by act (encounter-stats, all characters) for offline use.
    internal static readonly IReadOnlyDictionary<int, RouteDamage> DefaultDamage = new Dictionary<int, RouteDamage>
    {
        [1] = new(9.7m, 27.9m, 46.0m), [2] = new(17.4m, 35.3m, 54.5m), [3] = new(14.3m, 28.5m, 62.5m),
    };

    public static decimal Reward(string type, int gold) => type switch
    {
        "monster" => 5m,
        "elite" => 16m,
        "unknown" => 4m,
        "treasure" => 10m,
        "shop" => Math.Min(gold, 250) / 25m,
        _ => 0m
    };

    public static decimal Damage(string type, RouteDamage damage) => type switch
    {
        "monster" => damage.Monster,
        "elite" => damage.Elite,
        "boss" => damage.Boss,
        // "?" resolves to an event most of the time and to a fight sometimes.
        "unknown" => decimal.Round(damage.Monster * .35m, 1),
        _ => 0m
    };

    public static RoutePlan Plan(RunContext context, RouteGraph graph, RouteDamage damage)
    {
        var paths = new List<IReadOnlyList<string>>();
        void Walk(string id, List<string> trail)
        {
            if (paths.Count >= PathLimit || !graph.Nodes.TryGetValue(id, out var node) || trail.Contains(id)) return;
            trail.Add(id);
            var children = node.Children.Where(x => graph.Nodes.ContainsKey(x) && !trail.Contains(x)).ToArray();
            if (children.Length == 0 || node.Type == "boss") paths.Add(trail.ToArray());
            else foreach (var child in children) Walk(child, trail);
            trail.RemoveAt(trail.Count - 1);
        }
        foreach (var start in graph.Starts) Walk(start, []);
        var evaluated = paths.Select(path => Evaluate(context, path.Select(id => graph.Nodes[id]).ToArray(), damage))
            .OrderByDescending(x => x.Score).ToList();
        var best = evaluated.FirstOrDefault();
        // Each node shows the forecast of the best path through it.
        var byNode = new Dictionary<string, NodeForecast>(StringComparer.Ordinal);
        foreach (var path in evaluated)
            foreach (var node in path.Nodes)
                if (!byNode.ContainsKey(node.Id)) byNode[node.Id] = node with { OnBest = best is not null && best.Nodes.Any(x => x.Id == node.Id) };
        if (best is not null) best = best with { Nodes = best.Nodes.Select(x => x with { OnBest = true }).ToArray() };
        // Distinct first steps make the alternatives worth showing.
        var top = evaluated.GroupBy(x => x.Nodes.FirstOrDefault()?.Id).Select(g => g.First()).Take(3).ToArray();
        return new RoutePlan(best, top, byNode, paths.Count);
    }

    internal static RoutePath Evaluate(RunContext context, IReadOnlyList<RouteNode> path, RouteDamage damage)
    {
        var maxHp = Math.Max(1, context.MaxHp);
        decimal hp = Math.Max(0, context.Hp);
        var gold = context.Gold;
        decimal rewards = 0m, penalty = 0m;
        var lethal = false;
        var forecasts = new List<NodeForecast>();
        for (var index = 0; index < path.Count; index++)
        {
            var node = path[index];
            var before = hp;
            decimal heal = 0m, danger = 0m;
            var rest = false;
            if (node.Type == "rest_site")
            {
                var simulated = context with { Hp = (int)Math.Round(hp), Route = new RouteSnapshot("rest_site", index + 1 < path.Count ? [new RouteOption([path[index + 1].Type])] : []) };
                var restValue = CampfireAdvisor.RestValue(simulated, Math.Round(maxHp * .3m), .3m);
                rest = restValue > 8m;
                if (rest) { heal = Math.Min(maxHp - hp, Math.Round(maxHp * .3m)); hp += heal; }
                else rewards += 8m;
            }
            else
            {
                rewards += Reward(node.Type, gold);
                if (node.Type == "shop") gold = Math.Max(0, gold - 150);
                gold += node.Type switch { "monster" => 15, "elite" => 30, "unknown" => 10, _ => 0 };
                var dmg = Damage(node.Type, damage);
                if (dmg > 0)
                {
                    danger = before <= 0 ? 1.5m : Math.Min(1.5m, dmg / before);
                    var excess = Math.Max(0m, danger - DangerFree);
                    penalty += DangerWeight * excess * excess * (node.Type == "boss" ? 2m : 1m);
                    hp -= dmg;
                    if (hp <= 0) { lethal = true; hp = 0; }
                }
            }
            forecasts.Add(new NodeForecast(node.Id, node.Type, Damage(node.Type, damage), heal, before, hp, danger, rest, false));
            if (lethal) break;
        }
        // HP that reaches the boss fight (or the end of the path).
        var bossIndex = forecasts.FindIndex(x => x.Type == "boss");
        var hpAtBoss = bossIndex >= 0 ? forecasts[bossIndex].HpBefore : hp;
        var score = rewards - penalty + 20m * hpAtBoss / maxHp - (lethal ? 1000m : 0m);
        return new RoutePath(forecasts, decimal.Round(score, 1), rewards, decimal.Round(penalty, 1), decimal.Round(hpAtBoss), lethal);
    }

    public static string Label(NodeForecast node) => node.Type switch
    {
        "rest_site" => node.Rest ? $"+{node.Heal:0} HP" : "улучш.",
        "shop" or "treasure" => "",
        _ when node.Damage > 0 => $"−{node.Damage:0} → {node.HpAfter:0}",
        _ => ""
    };

    // Green below a quarter of current HP, yellow below a half, red above.
    public static string Severity(NodeForecast node) =>
        node.Type == "rest_site" ? "rest" : node.Danger >= .5m || node.HpAfter <= 0 ? "high" : node.Danger >= .25m ? "medium" : "low";

    public static string Format(RunContext context, RoutePlan plan)
    {
        if (plan.Best is null) return "Маршрут не прочитан.";
        string Name(string type) => type switch
        {
            "monster" => "бой", "elite" => "элита", "rest_site" => "костёр", "shop" => "магазин", "treasure" => "сундук",
            "unknown" => "?", "boss" => "босс", "ancient" => "Древний", _ => type
        };
        string Describe(RoutePath path)
        {
            var counts = path.Nodes.GroupBy(x => x.Type).ToDictionary(x => x.Key, x => x.Count());
            var parts = new[] { "elite", "monster", "unknown", "rest_site", "shop", "treasure" }
                .Where(counts.ContainsKey).Select(t => $"{Name(t)} ×{counts[t]}");
            return $"{string.Join(", ", parts)} · HP к боссу ≈{path.HpAtBoss:0}/{context.MaxHp} · ценность {path.Score:0.#}" + (path.Lethal ? " · ⚠ по средним потерям HP не хватает" : "");
        }
        var lines = new List<string>
        {
            $"Маршрут до босса · HP {context.Hp}/{context.MaxHp} · проверено путей: {plan.PathCount}",
            "★ Рекомендуемый: " + string.Join(" → ", plan.Best.Nodes.Select(x => Name(x.Type))),
            "  " + Describe(plan.Best)
        };
        var alternatives = plan.Top.Where(x => !ReferenceEquals(x, plan.Top[0])).ToArray();
        if (alternatives.Length > 0)
        {
            lines.Add("Другие первые шаги:");
            lines.AddRange(alternatives.Select(x => $"{Name(x.Nodes[0].Type)}: {Describe(x)} (ценность ниже на {plan.Best.Score - x.Score:0.#})"));
        }
        var risky = plan.Best.Nodes.Where(x => x.Danger >= .4m).ToArray();
        if (risky.Length > 0) lines.Add($"Опасные бои на пути: {string.Join(", ", risky.Select(x => $"{Name(x.Type)} (−{x.Damage:0}, HP {x.HpBefore:0})"))}.");
        lines.Add("");
        lines.Add("Метки на карте: ожидаемый урон и HP после узла по средним данным Spire Codex для этого акта; у костров — отдых или улучшение. Это средние значения: сильная колода теряет меньше.");
        return string.Join("\n", lines);
    }

    internal static RouteDamage ParseDamage(string json, string character)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("encounters", out var rows) || rows.ValueKind != JsonValueKind.Array) return new(0, 0, 0);
        var key = character.Replace("CHARACTER.", "", StringComparison.OrdinalIgnoreCase).ToUpperInvariant();
        var totals = new Dictionary<string, (decimal Weight, decimal Damage, decimal Fatal)>();
        foreach (var row in rows.EnumerateArray())
        {
            var room = row.TryGetProperty("room_type", out var r) ? r.GetString() ?? "" : "";
            decimal total = row.TryGetProperty("total", out var t) ? t.GetDecimal() : 0m;
            decimal avg = row.TryGetProperty("avg_damage", out var a) && a.ValueKind == JsonValueKind.Number ? a.GetDecimal() : 0m;
            decimal fatal = row.TryGetProperty("fatal", out var f) ? f.GetDecimal() : 0m;
            // Per-character rows replace the global ones when they hold enough fights.
            if (row.TryGetProperty("characters", out var characters) && characters.ValueKind == JsonValueKind.Array)
                foreach (var c in characters.EnumerateArray())
                    if (string.Equals(c.GetProperty("character").GetString(), key, StringComparison.OrdinalIgnoreCase) && c.GetProperty("total").GetDecimal() >= 500m)
                    {
                        total = c.GetProperty("total").GetDecimal();
                        avg = c.GetProperty("avg_damage").GetDecimal();
                        fatal = c.TryGetProperty("fatal", out var cf) ? cf.GetDecimal() : fatal;
                    }
            if (total <= 0) continue;
            var old = totals.GetValueOrDefault(room);
            totals[room] = (old.Weight + total, old.Damage + avg * total, old.Fatal + fatal);
        }
        decimal Avg(string room) => totals.TryGetValue(room, out var v) && v.Weight > 0 ? decimal.Round(v.Damage / v.Weight, 1) : 0m;
        var fights = totals.Values.Sum(x => x.Weight);
        return new RouteDamage(Avg("monster"), Avg("elite"), Avg("boss"), fights > 0 ? totals.Values.Sum(x => x.Fatal) / fights : 0m);
    }
}

internal sealed class EncounterDamageClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly Dictionary<string, RouteDamage> Cache = new(StringComparer.OrdinalIgnoreCase);

    // What the route can use right now: this session's data, else the A10 averages.
    public static RouteDamage Immediate(int act, string character) =>
        Cache.TryGetValue($"{act}:{character}", out var cached) ? cached : RoutePlanner.DefaultDamage.GetValueOrDefault(Math.Clamp(act, 1, 3)) ?? RoutePlanner.DefaultDamage[1];

    // encounter-stats accepts only the A10 cohorts; "a10" is the closest to every ascension.
    public async Task<RouteDamage> GetAsync(int act, string character, CancellationToken token = default)
    {
        var fallback = RoutePlanner.DefaultDamage.GetValueOrDefault(Math.Clamp(act, 1, 3)) ?? RoutePlanner.DefaultDamage[1];
        var key = $"{act}:{character}";
        if (Cache.TryGetValue(key, out var cached)) return cached;
        await Gate.WaitAsync(token);
        try
        {
            if (Cache.TryGetValue(key, out cached)) return cached;
            var disk = ModPaths.UserData($"encounters-a10-act{act}-v1.json");
            string? json = null;
            if (File.Exists(disk) && (NetworkSettings.Offline || DateTime.UtcNow - File.GetLastWriteTimeUtc(disk) < TimeSpan.FromHours(24)))
                json = await File.ReadAllTextAsync(disk, token);
            if (json is null && !NetworkSettings.Offline)
            {
                try
                {
                    var fresh = await Http.GetStringAsync($"https://spire-codex.com/api/runs/encounter-stats?act={act}&bracket=a10&limit=200", token);
                    var probe = await Task.Run(() => RoutePlanner.ParseDamage(fresh, character), token);
                    if (probe.Monster <= 0) throw new InvalidDataException("Spire Codex returned no encounter statistics.");
                    Directory.CreateDirectory(Path.GetDirectoryName(disk)!);
                    await File.WriteAllTextAsync(disk + ".tmp", fresh, token);
                    File.Move(disk + ".tmp", disk, true);
                    json = fresh;
                }
                catch (Exception error) when (error is not OperationCanceledException || !token.IsCancellationRequested)
                {
                    // A slow or failed request must not cost the route: an old copy or the averages do.
                    Console.WriteLine("Build Advisor encounter stats: " + error.Message);
                    if (File.Exists(disk)) json = await File.ReadAllTextAsync(disk, token);
                }
            }
            if (json is null) return fallback;
            var parsed = await Task.Run(() => RoutePlanner.ParseDamage(json, character), token);
            var result = new RouteDamage(parsed.Monster > 0 ? parsed.Monster : fallback.Monster, parsed.Elite > 0 ? parsed.Elite : fallback.Elite,
                parsed.Boss > 0 ? parsed.Boss : fallback.Boss, parsed.Fatal);
            Cache[key] = result;
            return result;
        }
        finally { Gate.Release(); }
    }
}

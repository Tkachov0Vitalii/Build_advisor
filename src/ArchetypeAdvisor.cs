using System.Text.Json;
using BuildAdvisor.Data;

namespace BuildAdvisor.Integration;

// H2 (Deck Compass names 21 archetypes; we use the community's own clusters): Spire Codex groups
// finished decks into 12–13 clusters per character, each with three defining cards, two defining
// relics, its share of runs and the win rate of runs that ended with such a deck. Pick Coach
// reports how similar the current deck is to the nearest clusters; that similarity is the build
// readiness. Clusters with a very low win rate are decks that rarely develop (runs that die
// early), so the advice steers towards developed clusters, and two close developed clusters
// form a hybrid.
internal sealed record ArchetypeEntity(string Id, string Name);
internal sealed record ArchetypeCluster(string Key, string Name, decimal Similarity, decimal Share, decimal WinRate,
    IReadOnlyList<ArchetypeEntity> Cards, IReadOnlyList<ArchetypeEntity> Relics);
// Primary is the closest cluster; Target is the developed cluster to build towards (the primary
// itself when it is developed, otherwise the closest developed one from 50% readiness).
internal sealed record ArchetypeDirection(ArchetypeCluster Primary, ArchetypeCluster? Secondary, IReadOnlyList<ArchetypeEntity> Missing, IReadOnlySet<string> KeyCards, bool Community)
{
    public ArchetypeCluster? Target { get; init; }
    public string Name => Secondary is null ? (Target ?? Primary).Name : Primary.Name + " × " + Secondary.Name;
}

internal static class ArchetypeAdvisor
{
    // Win rate (percent) that separates developed clusters from decks that rarely survive.
    internal const decimal Developed = 20m;
    // Similarity a hybrid partner must reach, and how far it may trail the primary cluster.
    internal const decimal HybridMinimum = 60m;
    internal const decimal HybridGap = 6m;
    internal const decimal KeyCardBonus = 6m;

    public static ArchetypeDirection? Choose(RunContext context, IReadOnlyList<ArchetypeCluster> candidates, bool community)
    {
        var ranked = candidates.Where(x => x.Similarity > 0).OrderByDescending(x => x.Similarity).ToArray();
        if (ranked.Length == 0) return null;
        var top = ranked[0];
        // A developed cluster within 10 points of the closest one is the better target.
        var primary = ranked.FirstOrDefault(x => x.WinRate >= Developed && x.Similarity >= top.Similarity - 10m) ?? top;
        var secondary = ranked.FirstOrDefault(x => !ReferenceEquals(x, primary) && x.WinRate >= Developed && primary.WinRate >= Developed &&
            x.Similarity >= HybridMinimum && x.Similarity >= primary.Similarity - HybridGap);
        var target = primary.WinRate >= Developed ? primary
            : ranked.Where(x => x.WinRate >= Developed && x.Similarity >= 50m).OrderByDescending(x => x.Similarity).FirstOrDefault();
        var owned = context.Cards.Select(SpireCodexMetricsClient.NormalizeId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var aim = target is null ? [] : target.Cards.Concat(secondary?.Cards ?? []).ToArray();
        var keys = aim.Select(x => SpireCodexMetricsClient.NormalizeId(x.Id)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = aim.Where(x => !owned.Contains(SpireCodexMetricsClient.NormalizeId(x.Id)))
            .DistinctBy(x => SpireCodexMetricsClient.NormalizeId(x.Id)).ToArray();
        return new ArchetypeDirection(primary, secondary, missing, keys, community) { Target = target };
    }

    // Pick Coach candidates when they arrived, otherwise the cached cluster catalogue.
    public static (ArchetypeDirection? Direction, IReadOnlyList<ArchetypeCluster> Candidates) Current(RunContext context, CoachAdvice? coach)
    {
        if (coach is { Candidates.Count: > 0 }) return (Choose(context, coach.Candidates, true), coach.Candidates);
        var local = LocalCandidates(context, ArchetypeCatalogClient.TryGetCached(context.Character));
        return (Choose(context, local, false), local);
    }

    // Offline: similarity from owned defining cards (80%) and relics (20%).
    public static IReadOnlyList<ArchetypeCluster> LocalCandidates(RunContext context, IReadOnlyList<ArchetypeCluster> catalog)
    {
        var cards = context.Cards.Select(SpireCodexMetricsClient.NormalizeId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var relics = context.Relics.Select(SpireCodexMetricsClient.NormalizeId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        decimal Share(IReadOnlyList<ArchetypeEntity> items, HashSet<string> have) =>
            items.Count == 0 ? 0m : (decimal)items.Count(x => have.Contains(SpireCodexMetricsClient.NormalizeId(x.Id))) / items.Count;
        return catalog.Select(x => x with { Similarity = decimal.Round(100m * (.8m * Share(x.Cards, cards) + .2m * Share(x.Relics, relics)), 1) })
            .Where(x => x.Similarity > 0).OrderByDescending(x => x.Similarity).Take(5).ToArray();
    }

    // Bonus for an offered card that completes the chosen direction.
    public static decimal Bonus(ArchetypeDirection? direction, string id, IEnumerable<string> deck)
    {
        if (direction?.Target is not { } target || target.WinRate < Developed || target.Similarity < 50m) return 0m;
        var key = SpireCodexMetricsClient.NormalizeId(id);
        if (!direction.KeyCards.Contains(key)) return 0m;
        // A second copy of a defining card adds much less than the first.
        return deck.Any(x => SpireCodexMetricsClient.NormalizeId(x) == key) ? 0m : KeyCardBonus;
    }

    public static string Format(ArchetypeDirection? direction, IReadOnlyList<ArchetypeCluster> candidates)
    {
        if (direction is null) return "Архетип сообщества: колода пока не похожа ни на один кластер.";
        string Cluster(ArchetypeCluster x) =>
            $"{x.Name} — готовность {x.Similarity:0}% · доля {x.Share:0.#}% · винрейт {x.WinRate:0.#}%" + (x.WinRate < Developed ? " (такие колоды редко доживают до победы)" : "");
        var lines = new List<string>
        {
            (direction.Community ? "Архетип сообщества: " : "Архетип сообщества (офлайн, по ключевым картам): ") +
            (direction.Secondary is null ? direction.Primary.Name : "гибрид " + direction.Name),
            "  " + Cluster(direction.Primary)
        };
        if (direction.Secondary is { } second) lines.Add("  " + Cluster(second));
        var target = direction.Target;
        if (target is null) lines.Add("  Развитого направления рядом нет: берите карты по потребностям колоды.");
        else
        {
            if (!ReferenceEquals(target, direction.Primary)) lines.Add("  Цель — ближайшее развитое направление: " + Cluster(target));
            var aim = target.Cards.Concat(direction.Secondary?.Cards ?? []).DistinctBy(x => x.Id).ToArray();
            var owned = aim.Where(x => direction.Missing.All(m => m.Id != x.Id)).ToArray();
            if (owned.Length > 0) lines.Add("  Уже есть: " + string.Join(", ", owned.Select(x => x.Name)));
            if (direction.Missing.Count > 0) lines.Add("  Ищите: " + string.Join(", ", direction.Missing.Select(x => x.Name)) +
                (target.Relics.Count > 0 ? " · реликвии: " + string.Join(", ", target.Relics.Select(x => x.Name)) : ""));
        }
        var others = candidates.Where(x => x.Key != direction.Primary.Key && x.Key != direction.Secondary?.Key && x.Key != target?.Key).Take(3).ToArray();
        if (others.Length > 0) lines.Add("  Другие направления: " + string.Join("; ", others.Select(x => $"{x.Name} {x.Similarity:0}%")));
        return string.Join("\n", lines);
    }

    // Both /api/runs/archetypes (characters → list) and Pick Coach "candidates" use this shape.
    internal static IReadOnlyList<ArchetypeCluster> ParseClusters(JsonElement rows)
    {
        if (rows.ValueKind != JsonValueKind.Array) return [];
        static IReadOnlyList<ArchetypeEntity> Entities(JsonElement row, string name) =>
            row.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().Select(x => new ArchetypeEntity(x.GetProperty("id").GetString() ?? "", x.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "")).Where(x => x.Id.Length > 0).ToArray()
                : [];
        static decimal Number(JsonElement row, string name) => row.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : 0m;
        return rows.EnumerateArray().Select(row =>
        {
            var cards = Entities(row, "defining_cards");
            var key = row.TryGetProperty("key", out var k) && k.GetString() is { } text ? text : string.Join("+", cards.Select(x => x.Id));
            var name = row.TryGetProperty("name", out var n) && n.GetString() is { } label ? label : key;
            return new ArchetypeCluster(key, name, Number(row, "similarity"), Number(row, "share"), Number(row, "win_rate"), cards, Entities(row, "defining_relics"));
        }).Where(x => x.Cards.Count > 0).ToArray();
    }

    internal static IReadOnlyList<ArchetypeCluster> ParseCatalog(string json, string character)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("characters", out var characters)) return [];
        foreach (var entry in characters.EnumerateObject())
            if (string.Equals(entry.Name, character.Replace("CHARACTER.", "", StringComparison.OrdinalIgnoreCase), StringComparison.OrdinalIgnoreCase))
                return ParseClusters(entry.Value);
        return [];
    }
}

internal sealed class ArchetypeCatalogClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static string? json;

    public static IReadOnlyList<ArchetypeCluster> TryGetCached(string character)
    {
        try { return json is null ? [] : ArchetypeAdvisor.ParseCatalog(json, character); } catch { return []; }
    }

    public async Task<IReadOnlyList<ArchetypeCluster>> GetAsync(string character, CancellationToken token = default)
    {
        if (json is not null) return TryGetCached(character);
        await Gate.WaitAsync(token);
        try
        {
            if (json is not null) return TryGetCached(character);
            var language = ModLocalization.ApiLanguage;
            var disk = ModPaths.UserData($"archetypes-{language}-v1.json");
            string? text = null;
            if (File.Exists(disk) && (NetworkSettings.Offline || DateTime.UtcNow - File.GetLastWriteTimeUtc(disk) < TimeSpan.FromHours(24)))
                text = await File.ReadAllTextAsync(disk, token);
            if (text is null && !NetworkSettings.Offline)
            {
                text = await Http.GetStringAsync("https://spire-codex.com/api/runs/archetypes?lang=" + language, token);
                var probe = text;
                if ((await Task.Run(() => ArchetypeAdvisor.ParseCatalog(probe, "IRONCLAD"), token)).Count == 0) throw new InvalidDataException("Spire Codex returned no archetypes.");
                Directory.CreateDirectory(Path.GetDirectoryName(disk)!);
                await File.WriteAllTextAsync(disk + ".tmp", text, token);
                File.Move(disk + ".tmp", disk, true);
            }
            json = text;
            return TryGetCached(character);
        }
        finally { Gate.Release(); }
    }
}

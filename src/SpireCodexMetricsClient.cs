using System.Text.Json;
using System.Collections.Concurrent;
using Godot;

namespace BuildAdvisor.Data;

public sealed record EntityMetric(
    string Id,
    string Name,
    decimal? PickRate,
    decimal WinRate,
    int Samples,
    decimal? CodexScore = null,
    string? Tier = null,
    decimal? Elo = null,
    int Offered = 0,
    int Picked = 0,
    IReadOnlyList<decimal?>? PickRateByAct = null);

public sealed class SpireCodexMetricsClient
{
    private static readonly System.Net.Http.HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, (DateTimeOffset until, IReadOnlyDictionary<string, EntityMetric> data)> Cache = [];

    public async Task<IReadOnlyDictionary<string, EntityMetric>> GetMetricsAsync(string entityType, string bracket = "a10", CancellationToken cancellationToken = default)
    {
        var cacheKey = entityType + ":" + bracket;
        lock (Cache) if (Cache.TryGetValue(cacheKey, out var cached) && cached.until > DateTimeOffset.UtcNow) return cached.data;
        var gate = Gates.GetOrAdd(cacheKey, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            lock (Cache) if (Cache.TryGetValue(cacheKey, out var cached) && cached.until > DateTimeOffset.UtcNow) return cached.data;
            var disk = DiskPath(cacheKey);
            if (File.Exists(disk) && DateTime.UtcNow - File.GetLastWriteTimeUtc(disk) < TimeSpan.FromMinutes(30))
            {
                var saved = await File.ReadAllTextAsync(disk, cancellationToken);
                var fromDisk = await Task.Run(() => ParseJson(saved, entityType), cancellationToken);
                if (fromDisk.Count > 0) { lock (Cache) Cache[cacheKey] = (DateTimeOffset.UtcNow.AddMinutes(30), fromDisk); return fromDisk; }
            }
            try
            {
                using var response = await Http.GetAsync($"https://spire-codex.com/api/runs/metrics/{entityType}?bracket={Uri.EscapeDataString(bracket)}", cancellationToken);
                response.EnsureSuccessStatusCode();
                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                var data = await Task.Run(() => ParseJson(json, entityType), cancellationToken);
                if (data.Count == 0) throw new InvalidDataException($"Spire Codex returned no {entityType} metrics.");
                Directory.CreateDirectory(Path.GetDirectoryName(disk)!);
                await File.WriteAllTextAsync(disk + ".tmp", json, cancellationToken);
                File.Move(disk + ".tmp", disk, true);
                lock (Cache) Cache[cacheKey] = (DateTimeOffset.UtcNow.AddMinutes(15), data);
                return data;
            }
            catch when (!cancellationToken.IsCancellationRequested && File.Exists(disk))
            {
                var staleJson = await File.ReadAllTextAsync(disk, cancellationToken);
                var stale = await Task.Run(() => ParseJson(staleJson, entityType), cancellationToken);
                if (stale.Count == 0) throw;
                lock (Cache) Cache[cacheKey] = (DateTimeOffset.UtcNow.AddMinutes(10), stale);
                GD.Print($"Build Advisor metrics: using stale offline cache for {cacheKey}");
                return stale;
            }
        }
        finally { gate.Release(); }
    }

    public static string NormalizeId(string id) => id.Trim().Replace(' ', '_').Replace('-', '_').ToUpperInvariant();

    private static string DiskPath(string cacheKey)
    {
        var safe = cacheKey.Replace(':', '_').Replace('/', '_');
        return Path.Combine(ProjectSettings.GlobalizePath("user://"), "build-advisor", "metrics-" + safe + ".json");
    }

    private static Dictionary<string, EntityMetric> ParseJson(string json, string entityType)
    {
        using var document = JsonDocument.Parse(json);
        return ParseMetrics(document.RootElement, entityType);
    }

    internal static Dictionary<string, EntityMetric> ParseMetrics(JsonElement root, string entityType)
    {
        var result = new Dictionary<string, EntityMetric>(StringComparer.OrdinalIgnoreCase);
        var rows = Rows(root).ToArray();
        // Spire Codex reports rates in percent (43.0), older payloads used fractions (.43).
        // The unit is decided once per response: a per-value guess turned a 0.8% pick rate
        // into 80% because 0.8 looks like a fraction.
        var pickPercent = UsesPercent(rows, "pick_rate", "take_rate");
        var winPercent = UsesPercent(rows, "win_rate", "winrate");
        var actPercent = UsesPercentArray(rows, "pick_rate_by_act") ?? pickPercent;
        foreach (var row in rows)
        {
            // Card metrics contain separate base and upgraded rows with the same id.
            // Rewards are offered in their base form, so allowing the upgraded row to
            // overwrite the base row can turn a weak card into an apparent S/100 card.
            if (row.TryGetProperty("upgraded", out var upgraded) &&
                upgraded.ValueKind is JsonValueKind.True) continue;
            var id = Text(row, $"{entityType.TrimEnd('s')}_id", "entity_id", "id");
            if (string.IsNullOrWhiteSpace(id)) continue;
            var picks = (int)Number(row, "samples", "picks", "pick_count");
            var offered = (int)Number(row, "offered");
            var picked = (int)Number(row, "picked");
            result[NormalizeId(id)] = new EntityMetric(
                id,
                Text(row, "name", $"{entityType.TrimEnd('s')}_name") ?? id,
                RatioOrNull(row, pickPercent, "pick_rate", "take_rate"),
                Ratio(Number(row, "win_rate", "winrate"), winPercent),
                picks > 0 ? picks : Math.Max(picked, offered),
                NullableNumber(row, "score", "codex_score"),
                Text(row, "tier"),
                NullableNumber(row, "elo", "codex_elo"),
                offered,
                picked,
                DecimalArray(row, "pick_rate_by_act", actPercent));
        }
        return result;
    }

    private static IEnumerable<JsonElement> Rows(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array) return root.EnumerateArray().ToArray();
        if (root.ValueKind != JsonValueKind.Object) return [];
        foreach (var key in new[] { "rows", "data", "items", "metrics", "results" }) if (root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Array) return value.EnumerateArray().ToArray();
        return root.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.Object).Select(p => p.Value).ToArray();
    }
    private static string? Text(JsonElement row, params string[] keys) { foreach (var key in keys) if (row.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String) return value.GetString(); return null; }
    private static decimal Number(JsonElement row, params string[] keys)
    {
        foreach (var key in keys)
            if (row.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number)) return number;
        return 0;
    }
    private static decimal? RatioOrNull(JsonElement row, bool percent, params string[] keys)
    {
        foreach (var key in keys)
            if (row.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number)) return Ratio(number, percent);
        return null;
    }
    private static bool UsesPercent(IEnumerable<JsonElement> rows, params string[] keys) =>
        rows.Any(row => keys.Any(key => row.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number &&
                                         value.TryGetDecimal(out var number) && number > 1m));
    private static bool? UsesPercentArray(IEnumerable<JsonElement> rows, string key)
    {
        var values = rows.Where(row => row.TryGetProperty(key, out var array) && array.ValueKind == JsonValueKind.Array)
            .SelectMany(row => row.GetProperty(key).EnumerateArray())
            .Where(item => item.ValueKind == JsonValueKind.Number && item.TryGetDecimal(out _))
            .Select(item => item.GetDecimal()).ToArray();
        return values.Length == 0 ? null : values.Any(x => x > 1m);
    }
    private static decimal? NullableNumber(JsonElement row, params string[] keys)
    {
        foreach (var key in keys)
            if (row.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number)) return number;
        return null;
    }
    private static IReadOnlyList<decimal?>? DecimalArray(JsonElement row, string key, bool percent)
    {
        if (!row.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.Array) return null;
        return value.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.Number && item.TryGetDecimal(out var number) ? (decimal?)Ratio(number, percent) : null).ToArray();
    }
    private static decimal Ratio(decimal value, bool percent) => percent ? value / 100m : value;
}

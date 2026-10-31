using System.Text.Json;
using Godot;

namespace BuildAdvisor.Data;

internal sealed record CardDefinition(
    string Id, string Name, string Character, string Type, int Cost, int Damage, int Block,
    int Draw, int Energy, string Rarity, string Description, IReadOnlySet<string> Tags, string? ImageUrl);

internal static class CardTaxonomy
{
    public static string RarityName(string? value)
    {
        var rarity = (value ?? "").Trim().ToLowerInvariant();
        if (rarity.Contains("uncommon") || rarity.Contains("необыч")) return "Необычная";
        if (rarity.Contains("rare") || rarity.Contains("редк")) return "Редкая";
        if (rarity.Contains("common") || rarity.Contains("обыч")) return "Обычная";
        if (rarity.Contains("basic") || rarity.Contains("базов")) return "Базовая";
        if (rarity.Contains("special") || rarity.Contains("особ")) return "Особая";
        return string.IsNullOrWhiteSpace(rarity) ? "Неизвестная" : value!.Trim();
    }

    public static bool IsBasic(CardDefinition card) => RarityName(card.Rarity) == "Базовая";

    // Starter Strike/Defend copies. Rarity is authoritative: TWIN_STRIKE, POMMEL_STRIKE and
    // PERFECTED_STRIKE contain the token but are real reward cards, not starter slots. When an
    // offline fixture has no rarity, only the exact starter IDs (STRIKE, STRIKE_<CHARACTER>) count.
    public static bool IsStarterCopy(CardDefinition card, string token)
    {
        var id = SpireCodexMetricsClient.NormalizeId(card.Id);
        var exact = id == token || id.StartsWith(token + "_", StringComparison.Ordinal);
        if (string.IsNullOrWhiteSpace(card.Rarity)) return exact;
        return IsBasic(card) && (exact || id.Contains(token, StringComparison.Ordinal));
    }

    public static bool IsStarterStrikeOrDefend(CardDefinition card) => IsStarterCopy(card, "STRIKE") || IsStarterCopy(card, "DEFEND");

    public static bool MatchesRarity(CardDefinition card, string filter) =>
        filter == "Любая редкость" || string.Equals(RarityName(card.Rarity), filter, StringComparison.OrdinalIgnoreCase);
}

internal sealed class CardCatalogClient
{
    private static readonly System.Net.Http.HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly Dictionary<string, (DateTimeOffset Until, IReadOnlyDictionary<string, CardDefinition> Data)> Caches = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan DiskLifetime = TimeSpan.FromHours(24);

    public IReadOnlyDictionary<string, CardDefinition> TryGetCached()
    {
        var language = ModLocalization.ApiLanguage;
        if (Caches.TryGetValue(language, out var current) && current.Data.Count > 0) return current.Data;
        try
        {
            var disk = DiskPath();
            if (!File.Exists(disk)) return new Dictionary<string, CardDefinition>();
            var data = Parse(File.ReadAllText(disk));
            if (data.Count > 0) Caches[language] = (DateTimeOffset.UtcNow.Add(DiskLifetime), data);
            return data;
        }
        catch (Exception error)
        {
            GD.PrintErr("Build Advisor catalog cache: " + error);
            return new Dictionary<string, CardDefinition>();
        }
    }

    public async Task<IReadOnlyDictionary<string, CardDefinition>> GetAsync(CancellationToken token = default)
    {
        var language = ModLocalization.ApiLanguage;
        if (Caches.TryGetValue(language, out var current) && current.Until > DateTimeOffset.UtcNow) return current.Data;
        await Gate.WaitAsync(token);
        try
        {
            if (Caches.TryGetValue(language, out var again) && again.Until > DateTimeOffset.UtcNow) return again.Data;
            var disk = DiskPath();
            if (File.Exists(disk) && DateTime.UtcNow - File.GetLastWriteTimeUtc(disk) < DiskLifetime)
            {
                var cachedJson = await File.ReadAllTextAsync(disk, token);
                var cachedData = await Task.Run(() => Parse(cachedJson), token);
                if (cachedData.Count > 0) { Caches[language] = (DateTimeOffset.UtcNow.Add(DiskLifetime), cachedData); return cachedData; }
            }
            try
            {
                NetworkSettings.ThrowIfOffline();
                using var response = await Http.GetAsync("https://spire-codex.com/api/cards?lang=" + language, token);
                response.EnsureSuccessStatusCode();
                var json = await response.Content.ReadAsStringAsync(token);
                // Parsing a full catalogue is the heaviest step; keep it off the Godot frame.
                // The continuation returns to the main thread through Godot's sync context.
                var data = await Task.Run(() => Parse(json), token);
                // A changed API format or a 200 OK placeholder page must not replace a good
                // local copy or be cached as an empty catalogue for 12 hours.
                if (data.Count == 0) throw new InvalidDataException("Spire Codex returned an empty card catalogue.");
                Directory.CreateDirectory(Path.GetDirectoryName(disk)!);
                await File.WriteAllTextAsync(disk + ".tmp", json, token);
                File.Move(disk + ".tmp", disk, true);
                Caches[language] = (DateTimeOffset.UtcNow.AddHours(12), data);
                return data;
            }
            catch when (!token.IsCancellationRequested && File.Exists(disk))
            {
                // A catalogue changes slowly. An expired local copy is much more useful than
                // an empty UI when the service, DNS or an antivirus web shield is unavailable.
                var staleJson = await File.ReadAllTextAsync(disk, token);
                var stale = await Task.Run(() => Parse(staleJson), token);
                if (stale.Count == 0) throw;
                Caches[language] = (DateTimeOffset.UtcNow.AddMinutes(30), stale);
                GD.Print($"Build Advisor catalog: using stale offline cache ({stale.Count} keys)");
                return stale;
            }
        }
        finally { Gate.Release(); }
    }

    private static IReadOnlyDictionary<string, CardDefinition> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
            var map = new Dictionary<string, CardDefinition>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in Rows(doc.RootElement))
            {
                var id = Text(row, "id");
                if (string.IsNullOrWhiteSpace(id)) continue;
                var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                AddTags(row, "tags", tags); AddTags(row, "keywords_key", tags);
                var description = Text(row, "description", "description_raw") ?? "";
                // The full-card render can be animated WebP; this Godot build cannot decode
                // those files.  The artwork endpoint is static and works for every card.
                var imageUrl = Text(row, "image_url", "image_url_card");
                if (!string.IsNullOrWhiteSpace(imageUrl) && imageUrl.StartsWith('/')) imageUrl = "https://spire-codex.com" + imageUrl;
                // Prefer the stable API key. Older cached catalogues may only contain the
                // localized display value, which CardTaxonomy also understands.
                var definition = new CardDefinition(id, Text(row, "name") ?? id, Text(row, "color") ?? "", Text(row, "type_key", "type") ?? "", Number(row, "cost"), Number(row, "damage"), Number(row, "block"), Number(row, "cards_draw"), Number(row, "energy_gain"), Text(row, "rarity_key", "rarity") ?? "", description, tags, imageUrl);
                map[SpireCodexMetricsClient.NormalizeId(id)] = definition;
                map[SpireCodexMetricsClient.NormalizeId(definition.Name)] = definition;
            }
        return map;
    }

    private static string DiskPath() => ModPaths.UserData($"cards-{ModLocalization.ApiLanguage}-v1.json");

    private static IEnumerable<JsonElement> Rows(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array) return root.EnumerateArray().ToArray();
        foreach (var key in new[] { "rows", "data", "items" }) if (root.TryGetProperty(key, out var rows) && rows.ValueKind == JsonValueKind.Array) return rows.EnumerateArray().ToArray();
        return [];
    }
    private static string? Text(JsonElement row, params string[] names) { foreach (var name in names) if (row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String) return value.GetString(); return null; }
    private static int Number(JsonElement row, string name) => row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : 0;
    private static void AddTags(JsonElement row, string name, ISet<string> target)
    {
        if (!row.TryGetProperty(name, out var value)) return;
        if (value.ValueKind == JsonValueKind.String) target.Add(value.GetString() ?? "");
        if (value.ValueKind == JsonValueKind.Array) foreach (var tag in value.EnumerateArray()) if (tag.ValueKind == JsonValueKind.String) target.Add(tag.GetString() ?? "");
    }
}

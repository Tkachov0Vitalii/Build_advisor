using System.Text.Json;
using Godot;

namespace BuildAdvisor.Data;

// Localized display names for relics or potions (ID -> name). The same endpoint shape serves
// both: /api/relics?lang=… and /api/potions?lang=….
internal sealed class RelicCatalogClient(string entity = "relics")
{
    private static readonly System.Net.Http.HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly Dictionary<string, IReadOnlyDictionary<string, string>> Caches = new(StringComparer.OrdinalIgnoreCase);

    public async Task<IReadOnlyDictionary<string, string>> GetAsync()
    {
        var language = ModLocalization.ApiLanguage;
        var cacheKey = entity + ":" + language;
        if (Caches.TryGetValue(cacheKey, out var cached) && cached.Count > 0) return cached;
        await Gate.WaitAsync();
        try
        {
            if (Caches.TryGetValue(cacheKey, out cached) && cached.Count > 0) return cached;
            var path = ModPaths.UserData($"{entity}-{language}-v1.json");
            string json;
            if (File.Exists(path)) json = await File.ReadAllTextAsync(path);
            else
            {
                NetworkSettings.ThrowIfOffline();
                json = await Http.GetStringAsync($"https://spire-codex.com/api/{entity}?lang=" + language);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(path, json);
            }
            using var doc = JsonDocument.Parse(json);
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
                foreach (var row in doc.RootElement.EnumerateArray())
                    if (row.TryGetProperty("id", out var id) && row.TryGetProperty("name", out var name) && id.GetString() is { } key && name.GetString() is { } value)
                        map[SpireCodexMetricsClient.NormalizeId(key)] = value;
            Caches[cacheKey] = map;
            return map;
        }
        catch (Exception error)
        {
            GD.PrintErr($"Build Advisor {entity} catalog: " + error.GetType().Name + " " + error.Message);
            return new Dictionary<string, string>();
        }
        finally { Gate.Release(); }
    }
}

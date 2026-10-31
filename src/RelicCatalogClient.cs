using System.Text.Json;
using Godot;

namespace BuildAdvisor.Data;

internal sealed class RelicCatalogClient
{
    private static readonly System.Net.Http.HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly Dictionary<string, IReadOnlyDictionary<string, string>> Caches = new(StringComparer.OrdinalIgnoreCase);

    public async Task<IReadOnlyDictionary<string, string>> GetAsync()
    {
        var language = ModLocalization.ApiLanguage;
        if (Caches.TryGetValue(language, out var cached) && cached.Count > 0) return cached;
        await Gate.WaitAsync();
        try
        {
            if (Caches.TryGetValue(language, out cached) && cached.Count > 0) return cached;
            var path = ModPaths.UserData($"relics-{language}-v1.json");
            string json;
            if (File.Exists(path)) json = await File.ReadAllTextAsync(path);
            else
            {
                NetworkSettings.ThrowIfOffline();
                json = await Http.GetStringAsync("https://spire-codex.com/api/relics?lang=" + language);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(path, json);
            }
            using var doc = JsonDocument.Parse(json);
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
                foreach (var row in doc.RootElement.EnumerateArray())
                    if (row.TryGetProperty("id", out var id) && row.TryGetProperty("name", out var name) && id.GetString() is { } key && name.GetString() is { } value)
                        map[SpireCodexMetricsClient.NormalizeId(key)] = value;
            Caches[language] = map;
            return map;
        }
        catch (Exception error)
        {
            GD.PrintErr("Build Advisor relic catalog: " + error.GetType().Name + " " + error.Message);
            return new Dictionary<string, string>();
        }
        finally { Gate.Release(); }
    }
}

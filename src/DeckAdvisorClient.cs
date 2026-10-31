using System.Text.Json;

namespace BuildAdvisor.Integration;

internal sealed class DeckAdvisorClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };
    public async Task<IReadOnlyDictionary<string, decimal>> GetRelicSupportAsync(RunContext context, CancellationToken token = default)
    {
        var url = $"https://spire-codex.com/api/runs/deck-advisor?character={Uri.EscapeDataString(context.Character)}&cards={Uri.EscapeDataString(string.Join(',', context.Cards))}&relics={Uri.EscapeDataString(string.Join(',', context.Relics))}";
        using var response = await Http.GetAsync(url, token); response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(token));
        var result = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        if (doc.RootElement.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            foreach (var item in items.EnumerateArray())
                if (item.TryGetProperty("etype", out var type) && type.GetString() == "relics" && item.TryGetProperty("id", out var id) && item.TryGetProperty("support", out var support) && support.TryGetDecimal(out var score)) result[id.GetString() ?? ""] = score;
        return result;
    }
}

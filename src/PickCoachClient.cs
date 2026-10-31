using System.Text.Json;
using BuildAdvisor.Data;

namespace BuildAdvisor.Integration;

internal sealed record CoachOffer(string Id, string Name, decimal Score, decimal Support);
internal sealed record CoachAdvice(string Archetype, decimal WinRate, IReadOnlyList<CoachOffer> Offers);

internal sealed class PickCoachClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(6) };
    private static readonly object Gate = new();
    private static readonly Dictionary<string, (DateTimeOffset Until, CoachAdvice? Advice)> Cache = new(StringComparer.Ordinal);
    public async Task<CoachAdvice?> GetAsync(RunContext context, IEnumerable<string> offer, CancellationToken token = default)
    {
        if (NetworkSettings.Offline) return null;
        var offered = offer.ToArray();
        var language = ModLocalization.ApiLanguage;
        var key = language + "|" + context.Character + "|" + string.Join(',', context.Cards.OrderBy(x => x)) + "|" + string.Join(',', context.Relics.OrderBy(x => x)) + "|" + string.Join(',', offered.OrderBy(x => x));
        lock (Gate)
        {
            if (Cache.TryGetValue(key, out var cached) && cached.Until > DateTimeOffset.UtcNow) return cached.Advice;
            // Every deck change creates a new key, so expired entries are pruned instead of
            // accumulating for the whole game session.
            if (Cache.Count > 64)
                foreach (var stale in Cache.Where(x => x.Value.Until <= DateTimeOffset.UtcNow).Select(x => x.Key).ToArray()) Cache.Remove(stale);
        }
        var query = $"character={Uri.EscapeDataString(context.Character.ToUpperInvariant())}&cards={Uri.EscapeDataString(string.Join(',', context.Cards))}&relics={Uri.EscapeDataString(string.Join(',', context.Relics))}&offer={Uri.EscapeDataString(string.Join(',', offered))}&lang={language}";
        using var response = await Http.GetAsync("https://spire-codex.com/api/runs/pick-coach?" + query, token);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(token));
        var root = doc.RootElement;
        if (!root.TryGetProperty("available", out var available) || !available.GetBoolean())
        {
            lock (Gate) Cache[key] = (DateTimeOffset.UtcNow.AddMinutes(3), null);
            return null;
        }
        var target = root.TryGetProperty("target", out var targetElement) ? targetElement : default;
        var archetype = Text(target, "name") ?? (language == "eng" ? "mixed build" : "смешанный билд");
        var winRate = Number(target, "win_rate") / 100m;
        var offers = root.TryGetProperty("offers", out var rows) && rows.ValueKind == JsonValueKind.Array
            ? rows.EnumerateArray().Select(row => new CoachOffer(Text(row, "id") ?? "?", Text(row, "name") ?? Text(row, "id") ?? "?", Number(row, "coach_score"), Number(row, "winner_support"))).OrderByDescending(x => x.Score).ToArray()
            : [];
        var result = new CoachAdvice(archetype, winRate, offers);
        lock (Gate) Cache[key] = (DateTimeOffset.UtcNow.AddMinutes(10), result);
        return result;
    }
    private static string? Text(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    private static decimal Number(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) && property.TryGetDecimal(out var number) ? number : 0;
}

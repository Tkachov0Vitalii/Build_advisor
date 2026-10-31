using System.Text.Json;

namespace BuildAdvisor.Integration;

// A player may commit early, before the deck has enough cards for an automatic signal.
// The setting is per character and belongs to one run. When the run seed is known it is
// persisted, so "Save and quit", a long loading screen or an act transition no longer reset
// a manual choice; a different seed (a new run) still starts from "Авто".
internal static class ArchetypePreferences
{
    private static readonly Dictionary<string, string> Selected = new(StringComparer.OrdinalIgnoreCase);
    private static readonly string SavePath = ModPaths.UserData("archetype-selection.json");
    private static string? activeCharacter;
    private static string? activeSeed;
    private static int lastFloor;
    private static int lastDeckSize;

    private sealed record SavedSelection(string Seed, Dictionary<string, string> Selected);

    public static IReadOnlyList<string> Options(string character) => character.ToLowerInvariant() switch
    {
        "ironclad" => ["Авто", "Сила", "Истощение", "Блок", "Саморанение", "Уязвимость"],
        "silent" => ["Авто", "Яд", "Заточки", "Сброс"],
        "defect" => ["Авто", "Молнии", "Мороз", "Фокус", "Нулевая стоимость"],
        "regent" => ["Авто", "Звёзды", "Кузня", "Бесцветные карты"],
        "necrobinder" => ["Авто", "Души", "Гибель", "Призыв"],
        _ => ["Авто"]
    };

    public static string Get(string character) => Selected.TryGetValue(character, out var value) ? Normalize(value) : "Авто";
    public static void Set(string character, string value)
    {
        Selected[character] = Normalize(value);
        Persist();
    }
    public static IReadOnlyList<string> Apply(string character, IReadOnlyList<string> detected) => Get(character) == "Авто" ? detected : [Get(character)];

    public static void ObserveRun(string character, int floor, int deckSize, string? seed = null)
    {
        if (!string.IsNullOrWhiteSpace(seed))
        {
            if (!string.Equals(activeSeed, seed, StringComparison.Ordinal))
            {
                Selected.Clear();
                // First sight of this seed in this game session: restore a saved choice for the
                // same run (continue after restart); any other seed is a new run.
                if (activeSeed is null && LoadSaved() is { } saved && string.Equals(saved.Seed, seed, StringComparison.Ordinal))
                    foreach (var pair in saved.Selected ?? new Dictionary<string, string>()) Selected[pair.Key] = Normalize(pair.Value);
                activeSeed = seed;
                Persist();
            }
            activeCharacter = character;
            lastFloor = floor;
            lastDeckSize = deckSize;
            return;
        }

        // Fallback for contexts without a seed (offline fixtures, older game builds).
        var changedCharacter = activeCharacter is not null && !string.Equals(activeCharacter, character, StringComparison.OrdinalIgnoreCase);
        var restarted = activeCharacter is not null && string.Equals(activeCharacter, character, StringComparison.OrdinalIgnoreCase) &&
                        (floor < lastFloor || deckSize + 3 < lastDeckSize);
        if (changedCharacter || restarted) Selected.Remove(character);
        activeCharacter = character;
        lastFloor = floor;
        lastDeckSize = deckSize;
    }

    // Called when no run has been readable for a few seconds (main menu). The in-memory choice
    // is dropped, but the saved file is kept: continuing the same run restores it by seed.
    public static void EndRun()
    {
        Selected.Clear();
        activeCharacter = null;
        activeSeed = null;
        lastFloor = 0;
        lastDeckSize = 0;
    }

    private static void Persist()
    {
        if (activeSeed is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SavePath)!);
            var temporary = SavePath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(new SavedSelection(activeSeed, new Dictionary<string, string>(Selected, StringComparer.OrdinalIgnoreCase))));
            File.Move(temporary, SavePath, true);
        }
        catch { /* the choice still works for this session */ }
    }

    private static SavedSelection? LoadSaved()
    {
        try { return File.Exists(SavePath) ? JsonSerializer.Deserialize<SavedSelection>(File.ReadAllText(SavePath)) : null; }
        catch { return null; }
    }

    private static string Normalize(string value) => value == "Шивы" ? "Заточки" : value;
}

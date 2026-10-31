namespace BuildAdvisor.Integration;

// A player may commit early, before the deck has enough cards for an automatic signal.
// The setting is deliberately per character and stays local to this game session.
internal static class ArchetypePreferences
{
    private static readonly Dictionary<string, string> Selected = new(StringComparer.OrdinalIgnoreCase);
    private static string? activeCharacter;
    private static int lastFloor;
    private static int lastDeckSize;

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
    public static void Set(string character, string value) => Selected[character] = Normalize(value);
    public static IReadOnlyList<string> Apply(string character, IReadOnlyList<string> detected) => Get(character) == "Авто" ? detected : [Get(character)];
    public static void ObserveRun(string character, int floor, int deckSize)
    {
        var changedCharacter = activeCharacter is not null && !string.Equals(activeCharacter, character, StringComparison.OrdinalIgnoreCase);
        var restarted = activeCharacter is not null && string.Equals(activeCharacter, character, StringComparison.OrdinalIgnoreCase) &&
                        (floor < lastFloor || deckSize + 3 < lastDeckSize);
        if (changedCharacter || restarted) Selected.Remove(character);
        activeCharacter = character;
        lastFloor = floor;
        lastDeckSize = deckSize;
    }
    public static void EndRun()
    {
        Selected.Clear();
        activeCharacter = null;
        lastFloor = 0;
        lastDeckSize = 0;
    }
    private static string Normalize(string value) => value == "Шивы" ? "Заточки" : value;
}

using System.Reflection;
using Godot;

namespace BuildAdvisor.Integration;

internal static class ReflectionIds
{
    private static readonly string[] IdNames = ["Id", "ModelId", "CardId", "RelicId", "id"];
    private static readonly string[] ModelNames = ["Card", "CardModel", "Model", "Relic", "RelicModel", "Item", "Entry", "Visual", "SaleVisual", "_saleVisual", "_cardNode", "_relic", "_relicNode", "Data", "Result", "Value"];
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    public static IReadOnlyList<string> FindIds(IEnumerable<object> roots) => roots.Select(FindId).Where(id => id is not null).Cast<string>().Distinct().ToArray();
    public static IReadOnlyList<string> FindIdsWithDuplicates(IEnumerable<object> roots) => roots.Select(FindId).Where(id => id is not null).Cast<string>().ToArray();
    public static IReadOnlyList<string> FindIds(Node root) => FindIds(Descendants(root).Prepend(root).Cast<object>());

    public static string? FindId(object root)
    {
        var queue = new Queue<(object value, int depth)>();
        queue.Enqueue((root, 0));
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        while (queue.Count > 0)
        {
            var (value, depth) = queue.Dequeue();
            if (!seen.Add(value)) continue;
            foreach (var name in IdNames)
                if (IdText(Member(value, name)) is { Length: > 0 } id && LooksLikeGameId(id)) return id;

            // CardCreationResult is intentionally undocumented; inspect its card/model-shaped members
            // so this continues to work across game updates that rename its backing field.
            foreach (var member in Members(value))
            {
                if (IsIdMember(member.name) && member.value is not bool && IdText(member.value) is { Length: > 0 } candidate && LooksLikeGameId(candidate)) return candidate;
                if (depth < 5 && member.value is { } child && child is not string && IsGameValue(child) &&
                    (member.name.Contains("card", StringComparison.OrdinalIgnoreCase) || member.name.Contains("model", StringComparison.OrdinalIgnoreCase) || member.name.Contains("relic", StringComparison.OrdinalIgnoreCase) || member.name.Contains("data", StringComparison.OrdinalIgnoreCase) || member.name.Contains("result", StringComparison.OrdinalIgnoreCase) || member.name.Contains("value", StringComparison.OrdinalIgnoreCase)))
                    queue.Enqueue((child, depth + 1));
            }
            if (depth < 4)
                foreach (var name in ModelNames)
                    if (Member(value, name) is { } child && child is not string) queue.Enqueue((child, depth + 1));
        }
        return null;
    }

    // Shop containers expose the first child through their model field.  Unlike FindId this
    // deliberately never walks descendants, so each sale slot keeps its own ID and price.
    public static string? FindDirectId(object root)
    {
        foreach (var name in IdNames)
            if (IdText(Member(root, name)) is { Length: > 0 } id && LooksLikeGameId(id)) return id;
        foreach (var name in ModelNames)
        {
            var model = Member(root, name);
            if (model is null || model is string) continue;
            foreach (var idName in IdNames)
                if (IdText(Member(model, idName)) is { Length: > 0 } id && LooksLikeGameId(id)) return id;
        }
        return null;
    }

    public static int? FindDirectInt(object root, params string[] names)
    {
        foreach (var name in names)
        {
            var value = Member(root, name);
            try { if (value is not null) return Convert.ToInt32(value); } catch { }
        }
        return null;
    }

    public static string? Text(object value) => Member(value, "Text")?.ToString();
    public static object? Member(object source, string name) => source.GetType().GetProperty(name, Flags)?.GetValue(source) ?? source.GetType().GetField(name, Flags)?.GetValue(source);
    public static string? FindText(object root, params string[] names) => FindValue(root, names)?.ToString();
    public static int? FindInt(object root, params string[] names)
    {
        var value = FindValue(root, names);
        try { return value is null ? null : Convert.ToInt32(value); } catch { return null; }
    }
    public static bool? FindBool(object root, params string[] names)
    {
        var value = FindValue(root, names);
        try { return value is null ? null : Convert.ToBoolean(value); } catch { return null; }
    }

    private static object? FindValue(object root, IReadOnlyCollection<string> names)
    {
        var queue = new Queue<(object value, int depth)>(); queue.Enqueue((root, 0));
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        while (queue.Count > 0)
        {
            var (value, depth) = queue.Dequeue(); if (!seen.Add(value)) continue;
            foreach (var name in names) if (Member(value, name) is { } found) return found;
            if (depth >= 4) continue;
            foreach (var member in Members(value))
                if (member.value is { } child && child is not string && IsGameValue(child) &&
                    (member.name.Contains("card", StringComparison.OrdinalIgnoreCase) || member.name.Contains("model", StringComparison.OrdinalIgnoreCase) || member.name.Contains("data", StringComparison.OrdinalIgnoreCase) || member.name.Contains("result", StringComparison.OrdinalIgnoreCase)))
                    queue.Enqueue((child, depth + 1));
        }
        return null;
    }
    private static string? IdText(object? value) => value switch
    {
        null => null,
        string text => text,
        _ => Member(value, "Entry")?.ToString() ?? value.ToString()
    };
    private static IEnumerable<(string name, object? value)> Members(object value)
    {
        foreach (var property in value.GetType().GetProperties(Flags).Where(p => p.GetIndexParameters().Length == 0))
        {
            object? memberValue = null; try { memberValue = property.GetValue(value); } catch { }
            yield return (property.Name, memberValue);
        }
        foreach (var field in value.GetType().GetFields(Flags)) yield return (field.Name, field.GetValue(value));
    }
    private static bool IsGameValue(object value) => value.GetType().Namespace?.StartsWith("MegaCrit.Sts2", StringComparison.Ordinal) == true;
    private static IEnumerable<Node> Descendants(Node root) { foreach (var child in root.GetChildren()) { yield return child; foreach (var nested in Descendants(child)) yield return nested; } }
    private static bool IsIdMember(string name) =>
        name is "Id" or "ModelId" or "CardId" or "RelicId" or "id" ||
        (name.EndsWith("Id", StringComparison.Ordinal) && !name.StartsWith("Is", StringComparison.Ordinal) && !name.Contains("Guid", StringComparison.OrdinalIgnoreCase));
    private static bool LooksLikeGameId(string value) =>
        !value.Equals("true", StringComparison.OrdinalIgnoreCase) && !value.Equals("false", StringComparison.OrdinalIgnoreCase) &&
        value.Length is > 1 and < 100 && value.Any(char.IsLetter) && value.All(c => char.IsLetterOrDigit(c) || c is '_' or '-' or ' ');
}

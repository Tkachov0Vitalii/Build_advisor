namespace BuildAdvisor.UI;

// Which badges a recycled card grid needs: a holder that now shows another card loses its badge
// and gets a new one, a holder that left the grid loses it, a new holder gets one if its card is
// scored. Pure, so the rule is covered by the regression matrix.
internal static class BadgeTracking
{
    public static (IReadOnlyList<(TKey Anchor, string Id)> Create, IReadOnlyList<TKey> Remove) Changes<TKey>(
        IReadOnlyDictionary<TKey, string> shown, IEnumerable<(TKey Anchor, string Id)> current, Func<string, bool> scored) where TKey : notnull
    {
        var create = new List<(TKey, string)>();
        var remove = new List<TKey>();
        var present = new HashSet<TKey>();
        foreach (var (anchor, id) in current)
        {
            if (!present.Add(anchor)) continue;
            if (shown.TryGetValue(anchor, out var old))
            {
                if (string.Equals(old, id, StringComparison.OrdinalIgnoreCase)) continue;
                remove.Add(anchor);
            }
            if (scored(id)) create.Add((anchor, id));
        }
        remove.AddRange(shown.Keys.Where(x => !present.Contains(x)));
        return (create, remove);
    }
}

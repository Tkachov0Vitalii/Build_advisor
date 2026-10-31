using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;

namespace BuildAdvisor.UI;

// 6.22.6: badges that live inside the game's own nodes (card holders, shop slots).
//
// The game pools NGridCardHolder: the holders of a closed upgrade, enchant or event screen are
// handed to the next card choice, including combat ones (potions, Stratagem, "choose a card to
// take"). Badges attached to those holders travelled with them and showed old "UPGRADE 74"
// scores on unrelated cards. An attached badge therefore checks every frame that it still
// belongs to the screen that created it (and, when given, still shows the expected card) and
// removes itself otherwise; pooled holders are also stripped when they enter or leave the pool.
internal sealed class AttachedBadge : Label
{
    internal const string NamePrefix = "BuildAdvisorAttached";

    public Node? OwnerScreen { get; init; }
    public object? ExpectedModel { get; init; }
    public Func<object?>? CurrentModel { get; init; }
    public Action<AttachedBadge>? Place { get; init; }

    public override void _Process(double delta)
    {
        _ = delta;
        var ownerValid = OwnerScreen is not null && GodotObject.IsInstanceValid(OwnerScreen) && OwnerScreen.IsInsideTree();
        var model = ExpectedModel is null ? null : CurrentModel?.Invoke();
        if (!AttachedBadges.StillBelongs(ownerValid, ownerValid && OwnerScreen!.IsAncestorOf(this), ExpectedModel, model))
        {
            Visible = false;
            QueueFree();
            return;
        }
        Place?.Invoke(this);
    }
}

internal static class AttachedBadges
{
    // Pure rule (regression-tested): a badge is kept only while its screen is alive, the badge is
    // still inside that screen, and the anchor shows the card the badge was made for.
    internal static bool StillBelongs(bool ownerValid, bool insideOwner, object? expectedModel, object? currentModel) =>
        ownerValid && insideOwner && (expectedModel is null || ReferenceEquals(expectedModel, currentModel));

    // Top-left corner of a badge centred above an anchor point, in the parent's coordinates.
    internal static Vector2 Above(Vector2 anchorTopCentre, Vector2 badgeSize, float gap) =>
        new(anchorTopCentre.X - badgeSize.X / 2f, anchorTopCentre.Y - badgeSize.Y - gap);

    // Card nodes are centred on their holder: the top edge is half the scaled height above.
    internal static Vector2 CardTopCentre(Vector2 cardCentre, Vector2 cardSize, Vector2 cardScale) =>
        new(cardCentre.X, cardCentre.Y - cardSize.Y * cardScale.Y / 2f);

    // Models listed by the screen plus models its grid actually draws, each once (by reference).
    // An enchant event drew three cards while its _cards list held two: the third had no badge.
    internal static IReadOnlyList<T> Merge<T>(IEnumerable<T?> listed, IEnumerable<T?> drawn) where T : class
    {
        var seen = new HashSet<T>(ReferenceEqualityComparer.Instance);
        var result = new List<T>();
        foreach (var item in listed.Concat(drawn))
            if (item is not null && seen.Add(item)) result.Add(item);
        return result;
    }

    internal static void Strip(Node? node)
    {
        if (node is null || !GodotObject.IsInstanceValid(node)) return;
        foreach (var child in node.GetChildren().Where(IsOurs).ToArray())
        {
            node.RemoveChild(child);
            child.QueueFree();
        }
    }

    internal static void StripDescendants(Node? root, string prefix)
    {
        if (root is null || !GodotObject.IsInstanceValid(root)) return;
        foreach (var child in Descendants(root).Where(x => x.Name.ToString().StartsWith(prefix, StringComparison.Ordinal)).ToArray())
        {
            child.GetParent()?.RemoveChild(child);
            child.QueueFree();
        }
    }

    private static bool IsOurs(Node node) => node is AttachedBadge || node.Name.ToString().StartsWith("BuildAdvisor", StringComparison.Ordinal);

    private static IEnumerable<Node> Descendants(Node root)
    {
        foreach (var child in root.GetChildren())
        {
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
}

// Pooled holders lose our badges on the way into and out of the pool.
[HarmonyPatch]
internal static class PooledHolderBadgeCleanup
{
    private static IEnumerable<System.Reflection.MethodBase> TargetMethods() =>
        AccessTools.GetDeclaredMethods(typeof(NGridCardHolder)).Where(x => x.Name is "OnFreedToPool" or "OnReturnedFromPool");

    [HarmonyPostfix] private static void After(Node __instance) => AttachedBadges.Strip(__instance);
}

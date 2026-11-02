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
    // 6.22.7: the full breakdown opens next to the badge on hover, as the reward badges did
    // before 6.22.6. Godot tooltips do not show in this game, so a hover card is drawn instead.
    public string ExpandedText { get; set; } = "";
    public Action<Label>? StylePopup { get; set; }

    // Called once the badge is in the tree: hover card and the per-frame ownership check.
    public void Activate()
    {
        AttachedBadges.EnablePopup(this, () => ExpandedText, popup => StylePopup?.Invoke(popup));
        FrameTicker.Add(Tick);
    }

    private bool Tick()
    {
        if (!GodotObject.IsInstanceValid(this) || IsQueuedForDeletion()) return false;
        var ownerValid = OwnerScreen is not null && GodotObject.IsInstanceValid(OwnerScreen) && OwnerScreen.IsInsideTree();
        var model = ExpectedModel is null ? null : CurrentModel?.Invoke();
        if (!AttachedBadges.StillBelongs(ownerValid, ownerValid && OwnerScreen!.IsAncestorOf(this), ExpectedModel, model))
        {
            Visible = false;
            QueueFree();
            return false;
        }
        Place?.Invoke(this);
        return true;
    }
}

// 6.22.7: per-frame work without Godot virtual methods. The mod is compiled without the Godot C#
// source generators, so the engine never calls _Process/_Ready/_Input overrides of the mod's own
// node classes: the F8 hotkey poll, badge followers and grid re-sync never ran. Every frame task
// now hangs off the SceneTree.ProcessFrame signal, which reaches C# delegates without generators.
internal static class FrameTicker
{
    private static readonly TickList Tasks = new();
    private static bool hooked;

    internal static void Add(Func<bool> task)
    {
        Tasks.Add(task);
        if (hooked || Engine.GetMainLoop() is not SceneTree tree) return;
        tree.ProcessFrame += () => Tasks.Run();
        hooked = true;
    }
}

// Pure task list (regression-tested): a task returning false or throwing is dropped; tasks added
// while running start on the next frame.
internal sealed class TickList
{
    private readonly List<Func<bool>> tasks = new();
    private readonly List<Func<bool>> pending = new();

    internal int Count => tasks.Count + pending.Count;
    internal void Add(Func<bool> task) => pending.Add(task);

    internal void Run()
    {
        tasks.AddRange(pending);
        pending.Clear();
        for (var i = tasks.Count - 1; i >= 0; i--)
        {
            bool keep;
            try { keep = tasks[i](); } catch { keep = false; }
            if (!keep) tasks.RemoveAt(i);
        }
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

    internal const string PopupName = AttachedBadge.NamePrefix + "Popup";

    // 6.22.7: the full breakdown opens next to a badge on hover, as the reward badges did before
    // 6.22.6 (the 6.22.6 badges relied on Godot tooltips, which this game does not show). The hover
    // card is a top-level child: it is drawn in the badge's own layer, above everything in it, and
    // is not clipped or scaled by the card holder.
    internal static void EnablePopup(Control owner, Func<string> text, Action<Label>? style)
    {
        owner.TooltipText = "";
        owner.MouseEntered += () => ShowPopup(owner, text(), style);
        owner.MouseExited += () => HidePopup(owner);
        owner.TreeExiting += () => HidePopup(owner);
    }

    private static void ShowPopup(Control owner, string text, Action<Label>? style)
    {
        if (string.IsNullOrWhiteSpace(text) || !GodotObject.IsInstanceValid(owner) || !owner.IsInsideTree()) return;
        HidePopup(owner);
        var size = PopupSize(text);
        var popup = new Label
        {
            Name = PopupName, Text = text, TopLevel = true, Size = size, CustomMinimumSize = size,
            ZIndex = 4000, ZAsRelative = false, MouseFilter = Control.MouseFilterEnum.Ignore,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center
        };
        style?.Invoke(popup);
        if (popup.GetThemeStylebox("normal") is StyleBoxFlat flat)
        {
            var padded = (StyleBoxFlat)flat.Duplicate();
            padded.ContentMarginLeft = padded.ContentMarginRight = 14;
            padded.ContentMarginTop = padded.ContentMarginBottom = 8;
            popup.AddThemeStyleboxOverride("normal", padded);
        }
        owner.AddChild(popup);
        var rect = owner.GetGlobalRect();
        popup.GlobalPosition = PopupPosition(rect.Position, rect.Size, size, owner.GetViewportRect().Size);
    }

    private static void HidePopup(Control owner)
    {
        if (!GodotObject.IsInstanceValid(owner)) return;
        if (owner.GetNodeOrNull<Label>(PopupName) is { } popup) { owner.RemoveChild(popup); popup.QueueFree(); }
    }

    // Hover card: about 22 px per wrapped line at font 15, 68 characters per line, 660 px wide.
    internal static Vector2 PopupSize(string text)
    {
        var lines = text.Split('\n').Sum(line => Math.Max(1, (int)Math.Ceiling(line.Length / 68d)));
        return new Vector2(660, Math.Clamp(lines * 22 + 20, 90, 460));
    }

    // Centred above the badge, below it when there is no room above, always inside the viewport.
    internal static Vector2 PopupPosition(Vector2 badgeOrigin, Vector2 badgeSize, Vector2 popupSize, Vector2 viewport)
    {
        var x = badgeOrigin.X + badgeSize.X / 2f - popupSize.X / 2f;
        var y = badgeOrigin.Y - popupSize.Y - 8f;
        if (y < 8f) y = badgeOrigin.Y + badgeSize.Y + 8f;
        return new Vector2(
            Math.Clamp(x, 8f, Math.Max(8f, viewport.X - popupSize.X - 8f)),
            Math.Clamp(y, 8f, Math.Max(8f, viewport.Y - popupSize.Y - 8f)));
    }

    // Top-left corner of a badge centred below an anchor point (6.24 ribbons under cards).
    internal static Vector2 Below(Vector2 anchorBottomCentre, Vector2 badgeSize, float gap) =>
        new(anchorBottomCentre.X - badgeSize.X / 2f, anchorBottomCentre.Y + gap);

    internal static Vector2 CardBottomCentre(Vector2 cardCentre, Vector2 cardSize, Vector2 cardScale) =>
        new(cardCentre.X, cardCentre.Y + cardSize.Y * cardScale.Y / 2f);

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

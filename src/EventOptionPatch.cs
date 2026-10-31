using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Events;
using BuildAdvisor.UI;

namespace BuildAdvisor.Integration;

// Used by ordinary events, Ancient events and Neow's opening choice.
[HarmonyPatch(typeof(NEventOptionButton), "_Ready")]
public static class EventOptionPatch
{
    // Every option button runs _Ready, so one event page used to be analysed and rendered once
    // per option. Buttons of the same page share one pending render.
    private static readonly HashSet<ulong> PendingScreens = new();

    [HarmonyPostfix]
    public static async void AfterReady(Node __instance)
    {
        var initialScreen = EventScreen(__instance);
        var screenId = initialScreen.GetInstanceId();
        if (!PendingScreens.Add(screenId)) return;
        try
        {
            await Task.Delay(150);
            if (!GodotObject.IsInstanceValid(__instance)) return;
            var screen = EventScreen(__instance);
            var options = Descendants(screen).Where(x => x is NEventOptionButton).ToArray();
            if (options.Length == 0) return;
            var context = RunContextReader.TryRead();
            var choices = options.Select(option => Clean(OptionText(option) ?? "Ветка события")).ToArray();
            var header = "BUILD ADVISOR · событие" + (context is null ? "" : $" · {context.Bracket.ToUpperInvariant()} · Act {context.Act}");
            AdviceOverlay.Render(screen, header + "\n" + LocalDeckAdvisor.ForEvent(context, choices));
        }
        catch (Exception error) { GD.PrintErr("Build Advisor event: " + error.Message); }
        finally { PendingScreens.Remove(screenId); }
    }
    private static string? OptionText(Node option) => ReflectionIds.Text(ReflectionIds.Member(option, "_label") ?? option);
    private static Node EventScreen(Node node)
    {
        var current = node;
        while (current.GetParent() is Node parent)
        {
            current = parent;
            if (current.GetType().Name.Contains("EventLayout", StringComparison.Ordinal)) return current;
        }
        return node;
    }
    private static IEnumerable<Node> Descendants(Node root) { foreach (var child in root.GetChildren()) { yield return child; foreach (var nested in Descendants(child)) yield return nested; } }
    private static string Clean(string value)
    {
        var withoutMarkup = System.Text.RegularExpressions.Regex.Replace(value, "\\[[^\\]]+\\]", "").Replace("[/]", "");
        return System.Text.RegularExpressions.Regex.Replace(withoutMarkup, "res://\\S+", "").Trim();
    }
}

[HarmonyPatch(typeof(NEventOptionButton), "_ExitTree")]
public static class EventOptionExitPatch
{
    [HarmonyPostfix]
    public static void AfterExit(Node __instance)
    {
        var current = __instance;
        while (current.GetParent() is Node parent)
        {
            current = parent;
            if (current.GetType().Name.Contains("EventLayout", StringComparison.Ordinal)) { AdviceOverlay.Remove(current); return; }
        }
    }
}

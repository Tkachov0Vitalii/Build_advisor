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
    private static readonly EventMetricsClient Events = new();
    private static readonly Data.SpireCodexMetricsClient Relics = new();
    private static readonly Data.RelicCatalogClient RelicNames = new();

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
            var player = RunContextReader.LocalPlayerModel();
            var choices = options.Select(option => Choice(option, player)).ToArray();
            var header = "BUILD ADVISOR · событие" + (context is null ? "" : $" · {context.Bracket.ToUpperInvariant()} · Act {context.Act}");
            var intro = context is null ? "Состояние забега не прочитано." : LocalDeckAdvisor.Snapshot(context);
            string Hint(string text) => LocalDeckAdvisor.EventHint(text, context);
            var empty = new Dictionary<string, EventOptionMetric>();
            var noRelics = new Dictionary<string, Data.EntityMetric>();
            // Local hints first, so the panel is never empty while statistics load.
            AdviceOverlay.Render(screen, header + "\n" + intro + "\n\n" + EventAdvisor.Format(EventAdvisor.Rate(choices, empty, noRelics, null, Hint)));
            var bracket = context?.Bracket ?? "all";
            var eventTask = Events.GetAsync(bracket);
            var relicTask = choices.Any(x => x.RelicId is not null) ? Relics.GetMetricsAsync("relics", bracket, context?.Character) : Task.FromResult<IReadOnlyDictionary<string, Data.EntityMetric>>(noRelics);
            var namesTask = choices.Any(x => x.RelicId is not null) ? RelicNames.GetAsync() : Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());
            IReadOnlyDictionary<string, EventOptionMetric> events = empty;
            IReadOnlyDictionary<string, Data.EntityMetric> relics = noRelics;
            try { events = await eventTask; } catch (Exception error) { GD.Print("Build Advisor event metrics: " + error.Message); }
            try { relics = await relicTask; } catch (Exception error) { GD.Print("Build Advisor relic metrics: " + error.Message); }
            var names = await namesTask;
            if (!GodotObject.IsInstanceValid(screen) || !GodotObject.IsInstanceValid(__instance)) return;
            var rated = EventAdvisor.Rate(choices, events, relics, names, Hint, context,
                context is not null && SegmentRiskModel.Assess(context) is { IsAvailable: true } risk ? risk.Probability : null);
            AdviceOverlay.Render(screen, header + "\n" + intro + "\n\n" + EventAdvisor.Format(rated));
            // The verdict also sits on each option button, so the panel does not have to be opened.
            var best = EventAdvisor.Best(rated);
            AdviceOverlay.RenderOptionBadges(screen, options.Zip(rated).Where(x => x.First is Control && GodotObject.IsInstanceValid(x.First))
                .Select(x => { var tag = EventAdvisor.Tag(x.Second, best); return ((Control)x.First, tag.Text, tag.Severity, tag.Best, x.Second.Line); }));
        }
        catch (Exception error) { GD.PrintErr("Build Advisor event: " + error.Message); }
        finally { PendingScreens.Remove(screenId); }
    }
    // Event and option IDs come from EventOption.TextKey; an Ancient option exposes its relic.
    private static EventChoice Choice(Node option, MegaCrit.Sts2.Core.Entities.Players.Player? player)
    {
        var text = Clean(OptionText(option) ?? "Ветка события");
        if (option is not NEventOptionButton button || button.Option is not { } model) return new EventChoice(text, null, null, null, false);
        var (eventId, optionId) = EventKeys.Parse(model.TextKey);
        string? relicId = null;
        try { relicId = model.Relic is { } relic ? ReflectionIds.FindId(relic) : null; } catch { }
        return new EventChoice(text, eventId, optionId, relicId, WillKill(option, player));
    }

    internal const string DeadlyWarning = "⚠ СМЕРТЕЛЬНО — этот вариант убьёт персонажа: ";

    // L8: the game itself knows when an option is lethal for the current HP (WillKillPlayer).
    private static bool WillKill(Node option, MegaCrit.Sts2.Core.Entities.Players.Player? player)
    {
        if (player is null || option is not NEventOptionButton button) return false;
        try { return button.Option?.WillKillPlayer?.Invoke(player) == true; }
        catch { return false; }
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

using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Events;
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
    private static readonly Data.CardCatalogClient Cards = new();

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
            var bridgeState = context is null ? null : ReadBridge(options.OfType<NEventOptionButton>().ToArray());
            BridgeAdvice? bridge = bridgeState is null ? null : BridgeAdvice.Loading;
            // Local hints first, so the panel is never empty while statistics load.
            AdviceOverlay.Render(screen, header + "\n" + intro + "\n\n" + EventAdvisor.Format(EventAdvisor.Rate(choices, empty, noRelics, null, Hint, bridge: bridge)));
            var bracket = context?.Bracket ?? "all";
            if (bridgeState is not null && context is not null)
            {
                bridge = null;
                try
                {
                    var catalogTask = Cards.GetAsync();
                    var cardsTask = Relics.GetMetricsAsync("cards", bracket, context.Character);
                    var catalog = await catalogTask;
                    var cardMetrics = await cardsTask;
                    bridge = await Task.Run(() => SlipperyBridgeAdvisor.Advise(context, bridgeState, catalog, cardMetrics));
                }
                catch (Exception error) { GD.Print("Build Advisor Slippery Bridge: " + error.Message); }
                if (!GodotObject.IsInstanceValid(screen) || !GodotObject.IsInstanceValid(__instance)) return;
            }
            var eventTask = Events.GetAsync(bracket);
            // Relic data is loaded for every event: Ancient options do not always expose their relic.
            var relicTask = Relics.GetMetricsAsync("relics", bracket, context?.Character);
            var namesTask = RelicNames.GetAsync();
            IReadOnlyDictionary<string, EventOptionMetric> events = empty;
            IReadOnlyDictionary<string, Data.EntityMetric> relics = noRelics;
            try { events = await eventTask; } catch (Exception error) { GD.Print("Build Advisor event metrics: " + error.Message); }
            try { relics = await relicTask; } catch (Exception error) { GD.Print("Build Advisor relic metrics: " + error.Message); }
            IReadOnlyDictionary<string, string> names = new Dictionary<string, string>();
            try { names = await namesTask; } catch (Exception error) { GD.Print("Build Advisor relic names: " + error.Message); }
            if (!GodotObject.IsInstanceValid(screen) || !GodotObject.IsInstanceValid(__instance)) return;
            var rated = EventAdvisor.Rate(choices, events, relics, names, Hint, context,
                context is not null && SegmentRiskModel.Assess(context) is { IsAvailable: true } risk ? risk.Probability : null, bridge);
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
        try { relicId = model.Relic is { } relic ? relic.Id?.Entry ?? ReflectionIds.FindId(relic) : null; } catch { }
        return new EventChoice(text, eventId, optionId, relicId, WillKill(option, player));
    }

    // 7.6: what Slippery Bridge puts at stake. The shown card is the OVERCOME option's card hover
    // tip; the price is the event's HpLoss variable; the next roll mirrors SlipperyBridge.GetNewRandomCard
    // after a hold: removable deck cards of another type, minus the cards already shown.
    private static BridgeState? ReadBridge(IReadOnlyList<NEventOptionButton> buttons)
    {
        try
        {
            if (buttons.FirstOrDefault()?.Event is not SlipperyBridge bridge || bridge.Owner is not { } owner) return null;
            var overcome = buttons.Select(x => x.Option).FirstOrDefault(x => x?.TextKey?.EndsWith(".OVERCOME", StringComparison.Ordinal) == true);
            var shown = overcome?.HoverTips?.OfType<CardHoverTip>().FirstOrDefault()?.Card;
            if (shown is null || ReflectionIds.FindId(shown) is not { } shownId) return null;
            if (!bridge.DynamicVars.TryGetValue("HpLoss", out var hpLoss)) return null;
            var skipped = (ReflectionIds.Member(bridge, "_skippedRemovals") as IEnumerable<CardModel>)?.ToHashSet<CardModel>(ReferenceEqualityComparer.Instance) ?? new HashSet<CardModel>(ReferenceEqualityComparer.Instance);
            skipped.Add(shown);
            var deck = owner.Deck.Cards.ToArray();
            var pool = deck.Where(c => c.GetType() != shown.GetType() && c.IsRemovable && !skipped.Contains(c)).ToArray();
            if (pool.Length == 0) pool = deck.Where(c => c.IsRemovable).ToArray();
            BridgeCard Of(CardModel card, string id) => new(id, card.CurrentUpgradeLevel > 0);
            var next = pool.Select(c => ReflectionIds.FindId(c) is { } id ? Of(c, id) : null).Where(x => x is not null).Cast<BridgeCard>().ToArray();
            return new BridgeState(Of(shown, shownId), (int)hpLoss.BaseValue, next);
        }
        catch (Exception error) { GD.Print("Build Advisor Slippery Bridge state: " + error.Message); return null; }
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

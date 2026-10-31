using System.Collections;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using BuildAdvisor.Data;
using BuildAdvisor.UI;

namespace BuildAdvisor.Integration;

internal enum EventCardPurpose { Gain, Upgrade, Remove, Transform, Enchant, Generic }
internal sealed record EventSelectionIntent(EventCardPurpose Purpose, int MinSelect, int MaxSelect, DateTimeOffset Created, bool Captured = false);

internal static class EventSelectionIntentRegistry
{
    private static readonly object Gate = new();
    private static EventSelectionIntent? pending;

    public static void Mark(EventCardPurpose purpose, object[] args)
    {
        var prefs = args.FirstOrDefault(x => x?.GetType().Name.Contains("CardSelectorPrefs", StringComparison.OrdinalIgnoreCase) == true);
        var min = prefs is null ? 1 : ReflectionIds.FindDirectInt(prefs, "MinSelect") ?? 1;
        var max = prefs is null ? min : ReflectionIds.FindDirectInt(prefs, "MaxSelect") ?? min;
        lock (Gate) pending = new EventSelectionIntent(purpose, Math.Max(0, min), Math.Max(min, max), DateTimeOffset.UtcNow, true);
    }

    public static EventSelectionIntent Consume(EventCardPurpose fallback)
    {
        lock (Gate)
        {
            var value = pending;
            pending = null;
            return value is not null && DateTimeOffset.UtcNow - value.Created < TimeSpan.FromSeconds(5)
                ? value
                : new EventSelectionIntent(fallback, 1, 1, DateTimeOffset.UtcNow);
        }
    }
}

internal static class EventCardAdvisor
{
    private static readonly CardCatalogClient Catalog = new();
    private static readonly SpireCodexMetricsClient Metrics = new();

    public static async void Analyze(Node screen, EventSelectionIntent intent, bool bundles = false)
    {
        try
        {
            // _Ready may run before the holders have received their models. One frame is not
            // sufficient on slower machines because the game animates these screens in.
            await Task.Delay(120);
            if (!Valid(screen)) return;
            var context = RunContextReader.TryRead();
            if (context is null) return;
            // These screens are also reused by combat cards (discard/exhaust/discovery). The
            // event advisor must never cover combat interaction unless a patched event command
            // explicitly registered the selection.
            if (!intent.Captured && IsCombatNode(context.Route.Current)) return;
            // Card choices inside a fight (potions, Strategem-like powers, discard/exhaust) are
            // not deck building: never advise them, whatever command opened the screen.
            if (InCombat() || screen is NCombatPileCardSelectScreen) return;

            if (bundles)
            {
                await AnalyzeBundles(screen, context);
                return;
            }

            var models = Values(ReflectionIds.Member(screen, "_cards")).ToArray();
            if (models.Length == 0) models = CardHolders(screen).Select(x => ReflectionIds.Member(x, "CardModel")).Where(x => x is not null).Cast<object>().ToArray();
            RunContextReader.ObserveCards(models);
            var ids = ReflectionIds.FindIds(models);
            if (ids.Count == 0) return;

            var purpose = DetectPurpose(screen, ids, context, intent.Purpose);
            var cached = Catalog.TryGetCached();
            if (cached.Count > 0)
            {
                var local = Evaluate(context, ids, cached, new Dictionary<string, EntityMetric>(StringComparer.OrdinalIgnoreCase), purpose);
                Draw(screen, local, purpose, intent);
            }

            var catalogTask = Catalog.GetAsync();
            var metricsTask = Metrics.GetMetricsAsync("cards", context.Bracket, context.Character);
            await Task.WhenAll(catalogTask, metricsTask);
            if (!Valid(screen)) return;
            var assessment = Evaluate(RunContextReader.TryRead() ?? context, ids, await catalogTask, await metricsTask, purpose);
            Draw(screen, assessment, purpose, intent);
        }
        catch (Exception error) { GD.PrintErr("Build Advisor event card selection: " + error); }
    }

    private static async Task AnalyzeBundles(Node screen, RunContext context)
    {
        var rawBundles = Values(ReflectionIds.Member(screen, "_bundles")).Select(Values).Select(x => x.ToArray()).Where(x => x.Length > 0).ToArray();
        if (rawBundles.Length == 0) return;
        var ids = rawBundles.SelectMany(x => ReflectionIds.FindIds(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var catalogTask = Catalog.GetAsync();
        var metricsTask = Metrics.GetMetricsAsync("cards", context.Bracket, context.Character);
        await Task.WhenAll(catalogTask, metricsTask);
        if (!Valid(screen)) return;
        var all = DeckBuildScoring.Evaluate(context, ids, await catalogTask, await metricsTask).Offers
            .ToDictionary(x => SpireCodexMetricsClient.NormalizeId(x.Id), StringComparer.OrdinalIgnoreCase);
        var scores = rawBundles.Select((bundle, index) =>
        {
            var members = ReflectionIds.FindIds(bundle).Select(x => all.GetValueOrDefault(SpireCodexMetricsClient.NormalizeId(x))).Where(x => x is not null).Cast<OfferScore>().ToArray();
            var value = Math.Clamp(members.Select(x => x.Final).DefaultIfEmpty(0m).Average() - Math.Max(0, members.Length - 1) * 3m, 0m, 100m);
            return new OfferScore("bundle-" + index, $"Набор {index + 1}", value, value, 0m, "набор",
                $"{members.Length} карт", Impact: string.Join(", ", members.Select(x => x.Name)), TagText: $"Набор · {members.Length} карт");
        }).ToArray();
        var anchors = Descendants(screen).OfType<Control>().Where(x => x.GetType().Name.Contains("CardBundle", StringComparison.OrdinalIgnoreCase)).ToArray();
        AdviceOverlay.RenderSelectionBadges(screen, anchors.Zip(scores, (anchor, score) => (anchor, score)));
        AdviceOverlay.Render(screen, Format(scores, EventCardPurpose.Gain, new EventSelectionIntent(EventCardPurpose.Gain, 1, 1, DateTimeOffset.UtcNow)), null, context);
    }

    internal static BuildAssessment Evaluate(RunContext context, IReadOnlyList<string> ids,
        IReadOnlyDictionary<string, CardDefinition> catalog, IReadOnlyDictionary<string, EntityMetric> metrics, EventCardPurpose purpose)
    {
        var baseAssessment = DeckBuildScoring.Evaluate(context, ids, catalog, metrics);
        if (purpose is EventCardPurpose.Gain or EventCardPurpose.Generic) return baseAssessment;
        var adjusted = baseAssessment.Offers.Select(score =>
        {
            catalog.TryGetValue(SpireCodexMetricsClient.NormalizeId(score.Id), out var card);
            var isCurse = card?.Type.Contains("curse", StringComparison.OrdinalIgnoreCase) == true || card?.Type.Contains("status", StringComparison.OrdinalIgnoreCase) == true;
            var isStarter = card is not null
                ? CardTaxonomy.IsStarterStrikeOrDefend(card)
                : SpireCodexMetricsClient.NormalizeId(score.Id) is "STRIKE" or "DEFEND";
            decimal value;
            string reason;
            switch (purpose)
            {
                case EventCardPurpose.Remove:
                    value = 100m - score.Final + (isCurse ? 28m : 0m) + (isStarter ? 12m : 0m);
                    reason = "чем выше оценка, тем выгоднее удалить карту";
                    break;
                case EventCardPurpose.Transform:
                    value = 100m - score.Final + (isCurse ? 18m : 0m) + (isStarter ? 10m : 0m);
                    reason = "чем выше оценка, тем выгоднее заменить карту";
                    break;
                case EventCardPurpose.Upgrade:
                    value = score.Final + UpgradeValue(card);
                    reason = "приоритет улучшения без увеличения колоды";
                    break;
                default:
                    value = score.Final + (card?.Type.Contains("power", StringComparison.OrdinalIgnoreCase) == true ? 5m : 2m);
                    reason = "ценность усиления выбранной карты";
                    break;
            }
            value = Math.Clamp(value, 0m, 100m);
            return new OfferScore(score.Id, score.Name, value, value, 0m, PurposeName(purpose), reason,
                score.Confidence, score.Personal, reason, $"Событие · {PurposeName(purpose)}");
        }).OrderByDescending(x => x.Final).ToArray();
        return baseAssessment with { Offers = adjusted };
    }

    private static decimal UpgradeValue(CardDefinition? card) => card is null ? 0m :
        CardUpgrades.Value(card) +
        (card.Type.Contains("power", StringComparison.OrdinalIgnoreCase) ? 4m : 0m) +
        (CardTaxonomy.RarityName(card.Rarity) == "Редкая" ? 3m : 0m);

    private static void Draw(Node screen, BuildAssessment assessment, EventCardPurpose purpose, EventSelectionIntent intent)
    {
        // The deck grid keeps holders only for visible rows and reassigns them while scrolling;
        // a holder's own fields can name a card it no longer shows (6.21–6.22 badges showed the
        // first rows' Strike/Defend scores on other cards and on empty slots). The grid itself
        // answers which holder shows a card now: NCardGrid.GetCardHolder(model). The badge sits
        // on the holder's hitbox, the clickable card rectangle.
        var grid = Descendants(screen).OfType<NCardGrid>().FirstOrDefault();
        var models = Values(ReflectionIds.Member(screen, "_cards")).OfType<CardModel>().ToArray();
        var ids = new Dictionary<CardModel, string?>(ReferenceEqualityComparer.Instance);
        foreach (var model in models) ids[model] = ReflectionIds.FindId(model);
        if (grid is not null && models.Length > 0)
        {
            IEnumerable<(Control Holder, Control? Card, string Id)> GridHolders()
            {
                if (!GodotObject.IsInstanceValid(grid)) return [];
                return models.Select(model =>
                {
                    NGridCardHolder? holder = null;
                    try { holder = grid.GetCardHolder(model); } catch { }
                    return (Holder: holder, Id: ids[model]);
                })
                .Where(x => x.Holder is not null && GodotObject.IsInstanceValid(x.Holder) && x.Id is not null)
                .Select(x => ((Control)x.Holder!, (Control?)x.Holder!.CardNode, x.Id!)).ToArray();
            }
            AdviceOverlay.RenderGridBadges(screen, GridHolders, assessment.Offers, ActionLabel(purpose));
        }
        else
        {
            // Screens without a grid (a few fixed-layout choices) keep their holders.
            IEnumerable<(Control Anchor, string Id)> Holders() => CardHolders(screen).Select(holder =>
            {
                var cardNode = ReflectionIds.Member(holder, "CardNode") as Control;
                var model = (cardNode is null ? null : ReflectionIds.Member(cardNode, "Model")) ?? ReflectionIds.Member(holder, "CardModel");
                return (Anchor: (Control)holder, Id: model is null ? null : ReflectionIds.FindId(model));
            }).Where(x => x.Id is not null).Select(x => (x.Anchor, x.Id!)).ToArray();
            AdviceOverlay.RenderTrackedSelectionBadges(screen, Holders, assessment.Offers, ActionLabel(purpose));
        }
        AdviceOverlay.Render(screen, Format(assessment.Offers, purpose, intent), null, RunContextReader.TryRead());
    }

    private static string Format(IEnumerable<OfferScore> offers, EventCardPurpose purpose, EventSelectionIntent intent)
    {
        var count = intent.MaxSelect > 1 ? $"Выберите от {intent.MinSelect} до {intent.MaxSelect} карт." : "Выберите одну карту.";
        var lines = offers.OrderByDescending(x => x.Final).Select(x => $"{x.Name}: {x.Final:F0}/100 — {x.Reason}");
        return $"BUILD ADVISOR · выбор в событии\nЦель: {PurposeName(purpose)}. {count}\nВысокая оценка означает лучший выбор именно для этой цели.\n\n" + string.Join("\n", lines) +
               "\n\nАвтовыбор на экранах событий отключён: мод не подтверждает выбор и не мешает выбрать несколько карт.";
    }

    private static EventCardPurpose DetectPurpose(Node screen, IReadOnlyList<string> ids, RunContext context, EventCardPurpose fallback)
    {
        // The screen type is authoritative; the opening command comes next. Text is the last
        // resort and only the prompt counts: card descriptions ("Улучшает выбранную карту" on
        // Armaments) made a transform screen look like an upgrade screen.
        switch (screen)
        {
            case NDeckUpgradeSelectScreen: return EventCardPurpose.Upgrade;
            case NDeckTransformSelectScreen: return EventCardPurpose.Transform;
            case NDeckEnchantSelectScreen: return EventCardPurpose.Enchant;
        }
        if (fallback != EventCardPurpose.Generic) return fallback;
        var text = string.Join(" ", Descendants(screen).Where(x => x is Label or RichTextLabel && !InsideCard(x))
            .Select(x => x is Label label ? label.Text : ((RichTextLabel)x).Text)).ToLowerInvariant();
        if (Has(text, "remove", "delete", "удал")) return EventCardPurpose.Remove;
        if (Has(text, "transform", "replace", "преобраз", "трансформ", "замен")) return EventCardPurpose.Transform;
        if (Has(text, "upgrade", "улучш")) return EventCardPurpose.Upgrade;
        if (Has(text, "enchant", "зачаров")) return EventCardPurpose.Enchant;
        if (fallback != EventCardPurpose.Generic) return fallback;
        var deck = context.Cards.Select(SpireCodexMetricsClient.NormalizeId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return ids.Count(id => deck.Contains(SpireCodexMetricsClient.NormalizeId(id))) * 2 >= ids.Count ? EventCardPurpose.Generic : EventCardPurpose.Gain;
    }

    private static string PurposeName(EventCardPurpose purpose) => purpose switch
    {
        EventCardPurpose.Remove => "удаление",
        EventCardPurpose.Transform => "преобразование",
        EventCardPurpose.Upgrade => "улучшение",
        EventCardPurpose.Enchant => "зачарование",
        EventCardPurpose.Gain => "получение карт",
        _ => "выбор карты из колоды"
    };

    private static string ActionLabel(EventCardPurpose purpose)
    {
        var english = ModLocalization.Current == AdvisorLanguage.English;
        return purpose switch
        {
            EventCardPurpose.Remove => english ? "REMOVE" : "УДАЛ.",
            EventCardPurpose.Transform => english ? "CHANGE" : "ЗАМ.",
            EventCardPurpose.Upgrade => english ? "UPGRADE" : "УЛУЧШ.",
            EventCardPurpose.Enchant => english ? "ENCHANT" : "ЗАЧАР.",
            EventCardPurpose.Generic => english ? "SELECT" : "ВЫБОР",
            _ => ""
        };
    }

    private static bool Has(string text, params string[] tokens) => tokens.Any(text.Contains);
    private static bool InCombat()
    {
        try { return MegaCrit.Sts2.Core.Combat.CombatManager.Instance?.IsInProgress == true; } catch { return false; }
    }

    private static bool InsideCard(Node node)
    {
        for (var parent = node.GetParent(); parent is not null; parent = parent.GetParent())
            if (parent is NCard) return true;
        return false;
    }

    private static bool IsCombatNode(string value) => Has(value.ToLowerInvariant(), "monster", "elite", "boss", "combat");
    private static bool Valid(Node node) => GodotObject.IsInstanceValid(node) && node.IsInsideTree();
    private static IEnumerable<object> Values(object? value) => value is IEnumerable values && value is not string ? values.Cast<object>() : [];
    private static IEnumerable<Node> Descendants(Node root) { foreach (var child in root.GetChildren()) { yield return child; foreach (var nested in Descendants(child)) yield return nested; } }
    private static IEnumerable<Control> CardHolders(Node root) => Descendants(root).OfType<Control>()
        .Where(x => x.GetType().Name.Contains("CardHolder", StringComparison.OrdinalIgnoreCase) && ReflectionIds.Member(x, "CardModel") is not null);
}

[HarmonyPatch(typeof(NChooseACardSelectionScreen), "_Ready")]
internal static class EventSingleCardScreenPatch
{
    [HarmonyPostfix] private static void AfterReady(Node __instance) => EventCardAdvisor.Analyze(__instance, new EventSelectionIntent(EventCardPurpose.Gain, 1, 1, DateTimeOffset.UtcNow));
}

[HarmonyPatch(typeof(NCardGridSelectionScreen), "ConnectSignalsAndInitGrid")]
internal static class EventGridCardScreenPatch
{
    [HarmonyPostfix] private static void AfterInit(Node __instance) => EventCardAdvisor.Analyze(__instance, EventSelectionIntentRegistry.Consume(EventCardPurpose.Generic));
}

[HarmonyPatch(typeof(NChooseABundleSelectionScreen), "_Ready")]
internal static class EventBundleCardScreenPatch
{
    [HarmonyPostfix] private static void AfterReady(Node __instance) => EventCardAdvisor.Analyze(__instance, new EventSelectionIntent(EventCardPurpose.Gain, 1, 1, DateTimeOffset.UtcNow), true);
}

[HarmonyPatch(typeof(NChooseACardSelectionScreen), "_ExitTree")]
internal static class EventSingleCardCleanupPatch { [HarmonyPrefix] private static void BeforeExit(Node __instance) => AdviceOverlay.ClearSelectionBadges(__instance); }
[HarmonyPatch(typeof(NCardGridSelectionScreen), "_ExitTree")]
internal static class EventGridCardCleanupPatch { [HarmonyPrefix] private static void BeforeExit(Node __instance) => AdviceOverlay.ClearSelectionBadges(__instance); }
[HarmonyPatch(typeof(NChooseABundleSelectionScreen), "_ExitTree")]
internal static class EventBundleCardCleanupPatch { [HarmonyPrefix] private static void BeforeExit(Node __instance) => AdviceOverlay.ClearSelectionBadges(__instance); }

[HarmonyPatch(typeof(CardSelectCmd), "FromDeckForRemoval")]
internal static class RemovalIntentPatch { [HarmonyPrefix] private static void Before(object[] __args) => EventSelectionIntentRegistry.Mark(EventCardPurpose.Remove, __args); }
[HarmonyPatch(typeof(CardSelectCmd), "FromDeckForTransformation")]
internal static class TransformIntentPatch { [HarmonyPrefix] private static void Before(object[] __args) => EventSelectionIntentRegistry.Mark(EventCardPurpose.Transform, __args); }
[HarmonyPatch(typeof(CardSelectCmd), "FromDeckForUpgrade")]
internal static class UpgradeIntentPatch { [HarmonyPrefix] private static void Before(object[] __args) => EventSelectionIntentRegistry.Mark(EventCardPurpose.Upgrade, __args); }
[HarmonyPatch]
internal static class EnchantIntentPatch
{
    private static IEnumerable<System.Reflection.MethodBase> TargetMethods() => AccessTools.GetDeclaredMethods(typeof(CardSelectCmd))
        .Where(x => x.Name == "FromDeckForEnchantment");
    [HarmonyPrefix] private static void Before(object[] __args) => EventSelectionIntentRegistry.Mark(EventCardPurpose.Enchant, __args);
}
[HarmonyPatch(typeof(CardSelectCmd), "FromSimpleGridForRewards")]
internal static class GridRewardIntentPatch { [HarmonyPrefix] private static void Before(object[] __args) => EventSelectionIntentRegistry.Mark(EventCardPurpose.Gain, __args); }
[HarmonyPatch(typeof(CardSelectCmd), "FromSimpleGrid")]
internal static class GridGenericIntentPatch { [HarmonyPrefix] private static void Before(object[] __args) => EventSelectionIntentRegistry.Mark(EventCardPurpose.Generic, __args); }
[HarmonyPatch(typeof(CardSelectCmd), "FromDeckGeneric")]
internal static class DeckGenericIntentPatch { [HarmonyPrefix] private static void Before(object[] __args) => EventSelectionIntentRegistry.Mark(EventCardPurpose.Generic, __args); }

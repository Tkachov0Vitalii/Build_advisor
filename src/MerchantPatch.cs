using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;
using BuildAdvisor.Data;
using BuildAdvisor.UI;

namespace BuildAdvisor.Integration;

[HarmonyPatch(typeof(NMerchantInventory), "Open")]
public static class MerchantPatch
{
    private static readonly SpireCodexMetricsClient Metrics = new();
    private static readonly CardCatalogClient Catalog = new();
    private static readonly RelicCatalogClient RelicCatalog = new();
    private static readonly RelicCatalogClient PotionCatalog = new("potions");

    // Community statistics are enrichment: a failed request yields no metrics, not a lost plan.
    private static async Task<IReadOnlyDictionary<string, EntityMetric>> OrEmpty(Task<IReadOnlyDictionary<string, EntityMetric>> task, string what)
    {
        try { return await task; }
        catch (Exception error) { GD.PrintErr($"Build Advisor shop {what} metrics: {error.Message}"); return new Dictionary<string, EntityMetric>(StringComparer.OrdinalIgnoreCase); }
    }
    private static long openSession;

    internal static void Invalidate() => Interlocked.Increment(ref openSession);
    private static bool IsCurrent(long session, GodotObject instance) =>
        session == Interlocked.Read(ref openSession) && GodotObject.IsInstanceValid(instance);

    [HarmonyPostfix]
    public static async void AfterOpen(Node __instance)
    {
        var session = Interlocked.Increment(ref openSession);
        // Inventory is populated over the opening animation. Wait only until real slots exist.
        await Task.Delay(120);
        if (!IsCurrent(session, __instance)) return;
        // Inventory exposes the actual sale slots.  This is the canonical source: each slot has
        // its own visual (for ID) and Entry.Cost, unlike the nested UI containers.
        if (__instance is not NMerchantInventory inventory) return;
        AdviceOverlay.ClearShopBadges(__instance);
        AdviceOverlay.SetRefreshAction(() => AfterOpen(__instance));
        var allItems = ReadSlots(inventory).ToArray();
        for (var attempt = 0; allItems.Length == 0 && attempt < 24; attempt++)
        {
            await Task.Delay(125);
            if (!IsCurrent(session, __instance)) return;
            allItems = ReadSlots(inventory).ToArray();
        }
        var cardSlots = allItems.Where(x => x.Kind == "card").ToArray();
        var cardItems = cardSlots.Select(x => x.Item).ToArray();
        var relicItems = allItems.Where(x => x.Kind == "relic").Select(x => x.Item).ToArray();
        var potionItems = allItems.Where(x => x.Kind == "potion").Select(x => x.Item).ToArray();
        var cards = cardItems.Select(x => x.Id).ToArray();
        var relics = relicItems.Select(x => x.Id).ToArray();
        if (cards.Length == 0 && relics.Length == 0) { AdviceOverlay.Render(__instance, "BUILD ADVISOR · магазин\nТовары ещё загружаются — откройте совет через мгновение."); return; }
        var context = RunContextReader.TryRead();
        var local = LocalDeckAdvisor.ForShop(context, cards, relics);
        // A useful local plan is immediate; remote statistics replace it when available.
        AdviceOverlay.Render(__instance, "BUILD ADVISOR · магазин\n" + local + "\n\nЗагрузка общей статистики…", null, context);
        var cachedCatalog = Catalog.TryGetCached();
        if (context is not null && cachedCatalog.Count > 0)
        {
            var localAssessment = DeckBuildScoring.Evaluate(context, cards, cachedCatalog, new Dictionary<string, EntityMetric>(StringComparer.OrdinalIgnoreCase));
            RenderCardBadges(__instance, cardSlots, localAssessment.Offers, context?.Gold ?? 0);
        }
        try
        {
            var bracket = context?.Bracket ?? "a10";
            if (context is null)
            {
                var standaloneMetrics = await Metrics.GetMetricsAsync("cards", bracket);
                if (IsCurrent(session, __instance)) AdviceOverlay.Render(__instance, "BUILD ADVISOR · магазин\n" + AdviceText.ForMetrics("Карты", cards, standaloneMetrics));
                return;
            }
            var cardMetricsTask = Metrics.GetMetricsAsync("cards", bracket, context.Character);
            var relicMetricsTask = Metrics.GetMetricsAsync("relics", bracket, context.Character);
            var potionMetricsTask = potionItems.Length == 0 ? Task.FromResult<IReadOnlyDictionary<string, EntityMetric>>(new Dictionary<string, EntityMetric>())
                : Metrics.GetMetricsAsync("potions", bracket, context.Character);
            var potionNamesTask = potionItems.Length == 0 ? Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>()) : PotionCatalog.GetAsync();
            var catalogTask = Catalog.GetAsync();
            var relicCatalogTask = RelicCatalog.GetAsync();
            // Card badges do not depend on the slower relic/metrics requests. Display a
            // complete local evaluation as soon as the catalogue is ready, then enrich it.
            var catalog = await catalogTask;
            if (IsCurrent(session, __instance))
                RenderCardBadges(__instance, cardSlots, DeckBuildScoring.Evaluate(context, cards, catalog, new Dictionary<string, EntityMetric>(StringComparer.OrdinalIgnoreCase)).Offers, context.Gold);
            var cardMetrics = await OrEmpty(cardMetricsTask, "card");
            var relicMetrics = await OrEmpty(relicMetricsTask, "relic");
            var potionMetrics = await OrEmpty(potionMetricsTask, "potion");
            var relicNames = await relicCatalogTask;
            var potionNames = await potionNamesTask;
            if (!IsCurrent(session, __instance)) return;
            var plan = ShopPlanBuilder.Create(context, cardItems, relicItems, catalog, cardMetrics, relicMetrics, relicNames, potionItems, potionMetrics, potionNames);
            if (IsCurrent(session, __instance))
            {
                AdviceOverlay.Render(__instance, "BUILD ADVISOR · магазин · " + bracket.ToUpperInvariant() + "\n" + plan);
                // M9: relic slots get the same badges as cards.
                var relicScores = relics.Select(id => RelicAdvisor.Score(context, id, relicMetrics, relicNames));
                RenderCardBadges(__instance, cardSlots.Concat(allItems.Where(x => x.Kind == "relic")).ToArray(),
                    DeckBuildScoring.Evaluate(context, cards, catalog, cardMetrics).Offers.Concat(relicScores).ToArray(), context.Gold);
            }
        }
        catch (Exception error) { if (IsCurrent(session, __instance)) AdviceOverlay.Render(__instance, "BUILD ADVISOR · магазин\n" + local + "\n\nСетевые данные недоступны — показан локальный совет."); GD.PrintErr($"Build Advisor shop: {error}"); }
    }

    private static void RenderCardBadges(Node screen, IReadOnlyList<(string Kind, ShopItem Item, Control Slot)> slots, IReadOnlyList<OfferScore> scores, int gold)
    {
        var byId = scores.GroupBy(x => SpireCodexMetricsClient.NormalizeId(x.Id), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
        var prices = slots.GroupBy(x => SpireCodexMetricsClient.NormalizeId(x.Item.Id), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First().Item.Price, StringComparer.OrdinalIgnoreCase);
        AdviceOverlay.RenderShopBadges(screen, slots.Select(x => (x.Slot, byId.GetValueOrDefault(SpireCodexMetricsClient.NormalizeId(x.Item.Id))))
            .Where(x => x.Item2 is not null).Select(x => (x.Slot, x.Item2!)),
            score => PriceNote(prices.GetValueOrDefault(SpireCodexMetricsClient.NormalizeId(score.Id)), gold));
    }

    // L3: what a purchase leaves for a removal, potions or the next shop.
    internal static string PriceNote(int price, int gold) =>
        price <= 0 || gold <= 0 ? "" :
        price <= gold ? $"цена {price} · после покупки останется {gold - price} золота" : $"цена {price} · не хватает {price - gold} золота";

    private static IEnumerable<(string Kind, ShopItem Item, Control Slot)> ReadSlots(NMerchantInventory inventory)
    {
        foreach (var slot in inventory.GetAllSlots().Cast<object>())
        {
            // Every visible shop slot is an NMerchantSlot.  The actual kind lives on
            // its Entry (MerchantCardEntry / MerchantRelicEntry), not on the slot.
            var entry = ReflectionIds.Member(slot, "Entry");
            var type = entry?.GetType().Name ?? string.Empty;
            var kind = type.Contains("MerchantCardEntry", StringComparison.Ordinal) ? "card"
                : type.Contains("MerchantRelicEntry", StringComparison.Ordinal) ? "relic"
                : type.Contains("MerchantPotionEntry", StringComparison.Ordinal) ? "potion" : null;
            if (kind is null) continue;
            var visual = ReflectionIds.Member(slot, "Visual");
            var id = visual is null ? ReflectionIds.FindId(entry!) : ReflectionIds.FindId(visual) ?? ReflectionIds.FindId(entry!);
            if (id is null) continue;
            var price = entry is null ? 0 : ReflectionIds.FindDirectInt(entry, "Cost") ?? 0;
            if (slot is Control control) yield return (kind, new ShopItem(id, price), control);
        }
    }
}

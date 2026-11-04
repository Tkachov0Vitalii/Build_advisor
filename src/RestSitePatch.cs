using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using BuildAdvisor.Data;
using BuildAdvisor.UI;

namespace BuildAdvisor.Integration;

// M6: the rest site. The panel says whether to rest or upgrade, and which card to upgrade.
[HarmonyPatch(typeof(NRestSiteRoom), "_Ready")]
public static class RestSitePatch
{
    private static readonly CardCatalogClient Catalog = new();
    private static readonly CampfireMetricsClient Metrics = new();

    [HarmonyPostfix]
    public static async void AfterReady(NRestSiteRoom __instance)
    {
        try
        {
            // Option buttons are filled right after _Ready.
            await Task.Delay(300);
            if (!GodotObject.IsInstanceValid(__instance)) return;
            var context = RunContextReader.TryRead();
            if (context is null) return;
            // 7.1: a disabled option (Smith with nothing to upgrade, Cook with < 2 removable cards)
            // is shown greyed out by the game and must not be advised.
            var options = (__instance.Options ?? []).Where(x => x is not null && x.IsEnabled).ToArray();
            var ids = options.Select(x => x.OptionId).Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
            if (ids.Length == 0) return;
            decimal? heal = null;
            var healOption = options.OfType<HealRestSiteOption>().FirstOrDefault();
            try { if (healOption is not null && RunContextReader.LocalPlayerModel() is { } owner) heal = HealRestSiteOption.GetHealAmount(owner); }
            catch (Exception error) { GD.Print("Build Advisor heal amount: " + error.Message); }
            var risk = SegmentRiskModel.Assess(context);
            decimal? riskValue = risk.IsAvailable ? risk.Probability : null;

            var cached = Catalog.TryGetCached();
            Render(__instance, context, ids, heal, riskValue, cached.Count > 0 ? DeckActionPlanner.BestUpgrade(context, cached) : null, null, Extras(context, cached));
            IReadOnlyDictionary<string, CardDefinition> catalog = cached;
            try { catalog = await Catalog.GetAsync(); }
            catch (Exception error) when (error is not OperationCanceledException) { GD.Print("Build Advisor campfire catalog: " + error.Message); }
            IReadOnlyDictionary<string, CampfireMetric>? metrics = null;
            try { metrics = await Metrics.GetAsync(context.Bracket, ModLocalization.ApiLanguage); }
            catch (Exception error) when (error is not OperationCanceledException) { GD.Print("Build Advisor campfire metrics: " + error.Message); }
            if (!GodotObject.IsInstanceValid(__instance)) return;
            Render(__instance, context, ids, heal, riskValue, catalog.Count > 0 ? DeckActionPlanner.BestUpgrade(context, catalog) : null, metrics, Extras(context, catalog));
        }
        catch (Exception error) { GD.PrintErr("Build Advisor rest site: " + error.Message); }
    }

    // Relic state the options depend on: Girya.TimesLifted, PumpkinCandle.KindleCount, cards with
    // the Clone enchantment (Pael's Growth), removal targets for Cook, teammates' HP for Mend.
    private static CampfireAdvisor.CampfireExtras Extras(RunContext context, IReadOnlyDictionary<string, CardDefinition> catalog)
    {
        int? lifts = null, kindle = null; var clones = 0;
        try
        {
            if (RunContextReader.LocalPlayerModel() is { } player)
            {
                foreach (var relic in ReflectionIds.Member(player, "Relics") is System.Collections.IEnumerable list ? list.Cast<object>() : [])
                {
                    var name = relic.GetType().Name;
                    if (name == "Girya" && ReflectionIds.Member(relic, "TimesLifted") is int t) lifts = t;
                    if (name == "PumpkinCandle" && ReflectionIds.Member(relic, "KindleCount") is int k) kindle = k;
                }
                var deck = ReflectionIds.Member(player, "Deck");
                var cards = deck is null ? null : ReflectionIds.Member(deck, "Cards") as System.Collections.IEnumerable;
                clones = cards?.Cast<object>().Count(card => ReflectionIds.Member(card, "Enchantment")?.GetType().Name == "Clone") ?? 0;
            }
        }
        catch (Exception error) { GD.Print("Build Advisor campfire relics: " + error.Message); }
        var teammates = context.Teammates.Where(x => x.MaxHp > 0).Select(x => (decimal)x.Hp / x.MaxHp).ToArray();
        return new CampfireAdvisor.CampfireExtras(lifts, clones, kindle,
            catalog.Count > 0 ? DeckActionPlanner.RemovalTargets(context.Cards, catalog) : 0,
            teammates.Length > 0 ? teammates.Min() : null);
    }

    private static void Render(Node screen, RunContext context, IReadOnlyList<string> ids, decimal? heal, decimal? risk,
        (string Name, decimal Value)? upgrade, IReadOnlyDictionary<string, CampfireMetric>? metrics, CampfireAdvisor.CampfireExtras extras)
    {
        var advice = CampfireAdvisor.Advise(context, ids, heal, risk, upgrade, metrics, extras);
        AdviceOverlay.Render(screen, "BUILD ADVISOR · костёр\n" + advice.Text, null, context);
    }
}

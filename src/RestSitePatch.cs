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
            var options = (__instance.Options ?? []).Where(x => x is not null).ToArray();
            var ids = options.Select(x => x.OptionId).Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
            if (ids.Length == 0) return;
            decimal? heal = null;
            var healOption = options.OfType<HealRestSiteOption>().FirstOrDefault();
            try { if (healOption is not null && RunContextReader.LocalPlayerModel() is { } owner) heal = HealRestSiteOption.GetHealAmount(owner); }
            catch (Exception error) { GD.Print("Build Advisor heal amount: " + error.Message); }
            var risk = SegmentRiskModel.Assess(context);
            decimal? riskValue = risk.IsAvailable ? risk.Probability : null;

            var cached = Catalog.TryGetCached();
            Render(__instance, context, ids, heal, riskValue, cached.Count > 0 ? DeckActionPlanner.BestUpgrade(context, cached) : null, null);
            IReadOnlyDictionary<string, CardDefinition> catalog = cached;
            try { catalog = await Catalog.GetAsync(); }
            catch (Exception error) when (error is not OperationCanceledException) { GD.Print("Build Advisor campfire catalog: " + error.Message); }
            IReadOnlyDictionary<string, CampfireMetric>? metrics = null;
            try { metrics = await Metrics.GetAsync(context.Bracket, ModLocalization.ApiLanguage); }
            catch (Exception error) when (error is not OperationCanceledException) { GD.Print("Build Advisor campfire metrics: " + error.Message); }
            if (!GodotObject.IsInstanceValid(__instance)) return;
            Render(__instance, context, ids, heal, riskValue, catalog.Count > 0 ? DeckActionPlanner.BestUpgrade(context, catalog) : null, metrics);
        }
        catch (Exception error) { GD.PrintErr("Build Advisor rest site: " + error.Message); }
    }

    private static void Render(Node screen, RunContext context, IReadOnlyList<string> ids, decimal? heal, decimal? risk,
        (string Name, decimal Value)? upgrade, IReadOnlyDictionary<string, CampfireMetric>? metrics)
    {
        var advice = CampfireAdvisor.Advise(context, ids, heal, risk, upgrade, metrics);
        AdviceOverlay.Render(screen, "BUILD ADVISOR · костёр\n" + advice.Text, null, context);
    }
}

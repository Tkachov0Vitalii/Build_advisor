using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using BuildAdvisor.UI;
using BuildAdvisor.Data;

namespace BuildAdvisor.Integration;

// The map is created at the beginning of every act and after a resumed run.
// It guarantees the persistent overlay exists before the first reward screen.
[HarmonyPatch(typeof(NMapScreen), "_Ready")]
public static class MapHudBootstrapPatch
{
    private static readonly CardCatalogClient Catalog = new();
    private static readonly SpireCodexMetricsClient Metrics = new();

    [HarmonyPostfix]
    public static async void AfterReady(Node __instance)
    {
        AdviceOverlay.Ensure(__instance);
        // Fetch once while the map is on screen, so search opens immediately at a reward.
        try
        {
            var context = RunContextReader.TryRead();
            var catalogTask = Catalog.GetAsync();
            var metricsTask = context is null ? Task.FromResult<IReadOnlyDictionary<string, EntityMetric>>(new Dictionary<string, EntityMetric>()) : Metrics.GetMetricsAsync("cards", context.Bracket);
            await Task.WhenAll(catalogTask, metricsTask);
            if (context is not null)
            {
                var catalog = await catalogTask;
                var metrics = await metricsTask;
                AdviceOverlay.EnableCardSearch(__instance, context, () => CardRewardPatch.BuildSearchEntries(RunContextReader.TryRead() ?? context, catalog, metrics));
            }
        }
        catch { /* normal advice falls back safely */ }
    }
}

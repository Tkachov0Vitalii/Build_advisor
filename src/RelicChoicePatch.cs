using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Relics;
using MegaCrit.Sts2.Core.Nodes.Screens;
using BuildAdvisor.Data;
using BuildAdvisor.UI;

namespace BuildAdvisor.Integration;

// M9: relic choice (boss and treasure choices). Each relic is scored by RelicAdvisor: community
// lift plus energy, survival and economy needs of the current deck; badges sit on the holders.
[HarmonyPatch(typeof(NChooseARelicSelection), "_Ready")]
public static class RelicChoicePatch
{
    private static readonly SpireCodexMetricsClient Metrics = new();
    private static readonly RelicCatalogClient Names = new();
    private static readonly DeckAdvisorClient DeckAdvisor = new();

    [HarmonyPostfix]
    public static async void AfterReady(Node __instance)
    {
        try
        {
            // Holders are created right after _Ready.
            await Task.Delay(200);
            if (!GodotObject.IsInstanceValid(__instance)) return;
            var holders = Descendants(__instance).OfType<NRelicBasicHolder>()
                .Select(holder => (Holder: (Control)holder, Id: holder.Relic?.Model is { } model ? ReflectionIds.FindId(model) : null))
                .Where(x => x.Id is not null).Select(x => (x.Holder, Id: x.Id!)).ToArray();
            var ids = holders.Length > 0 ? holders.Select(x => x.Id).ToArray() : ReflectionIds.FindIds(__instance).ToArray();
            if (ids.Length == 0) return;
            var context = RunContextReader.TryRead();
            if (context is null) { AdviceOverlay.Render(__instance, "BUILD ADVISOR · реликвии\n" + LocalDeckAdvisor.ForRelics(null, ids)); return; }
            var risk = SegmentRiskModel.Assess(context);
            decimal? riskValue = risk.IsAvailable ? risk.Probability : null;
            var empty = new Dictionary<string, EntityMetric>(StringComparer.OrdinalIgnoreCase);
            Render(__instance, context, holders, ids, empty, null, riskValue, "\n\nЗагрузка общей статистики…");

            IReadOnlyDictionary<string, EntityMetric> metrics = empty;
            var note = "";
            try { metrics = await Metrics.GetMetricsAsync("relics", context.Bracket, context.Character); }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                note = "\n\nСтатистика сообщества недоступна — оценка по ролям реликвий и колоде.";
                GD.Print("Build Advisor relic metrics: " + error.Message);
            }
            IReadOnlyDictionary<string, string>? names = null;
            try { names = await Names.GetAsync(); } catch (Exception error) when (error is not OperationCanceledException) { GD.Print("Build Advisor relic names: " + error.Message); }
            var support = new Dictionary<string, decimal>();
            try { foreach (var pair in await DeckAdvisor.GetRelicSupportAsync(context)) support[pair.Key] = pair.Value; }
            catch (Exception error) when (error is not OperationCanceledException) { GD.Print("Build Advisor relic support: " + error.Message); }
            if (!GodotObject.IsInstanceValid(__instance)) return;
            var matches = ids.Where(support.ContainsKey).Select(id => $"{(names is not null && names.TryGetValue(SpireCodexMetricsClient.NormalizeId(id), out var n) ? n : id)}: у похожих победных билдов {support[id]:F0}%").ToArray();
            if (matches.Length > 0) note += "\n\n" + string.Join("\n", matches);
            Render(__instance, context, holders, ids, metrics, names, riskValue, note);
        }
        catch (Exception error) { GD.PrintErr($"Build Advisor relics: {error}"); }
    }

    private static void Render(Node screen, RunContext context, IReadOnlyList<(Control Holder, string Id)> holders, IReadOnlyList<string> ids,
        IReadOnlyDictionary<string, EntityMetric> metrics, IReadOnlyDictionary<string, string>? names, decimal? risk, string note)
    {
        var ranked = RelicAdvisor.Rank(context, ids, metrics, names, risk);
        AdviceOverlay.Render(screen, "BUILD ADVISOR · реликвии\n" + RelicAdvisor.Format(context, ranked) + note, null, context);
        var byId = ranked.ToDictionary(x => SpireCodexMetricsClient.NormalizeId(x.Id), StringComparer.OrdinalIgnoreCase);
        AdviceOverlay.RenderSelectionBadges(screen, holders
            .Where(x => byId.ContainsKey(SpireCodexMetricsClient.NormalizeId(x.Id)))
            .Select(x => (x.Holder, byId[SpireCodexMetricsClient.NormalizeId(x.Id)])));
    }

    private static IEnumerable<Node> Descendants(Node root)
    {
        foreach (var child in root.GetChildren())
        {
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
}

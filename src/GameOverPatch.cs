using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Screens.GameOverScreen;
using MegaCrit.Sts2.Core.Runs;
using BuildAdvisor.Data;
using BuildAdvisor.UI;

namespace BuildAdvisor.Integration;

// M7: the game-over screen records the run result and shows the review of this run's choices.
[HarmonyPatch(typeof(NGameOverScreen), "_Ready")]
public static class GameOverPatch
{
    private static readonly CardCatalogClient Catalog = new();

    [HarmonyPostfix]
    public static void AfterReady(NGameOverScreen __instance)
    {
        try
        {
            var history = ReflectionIds.Member(__instance, "_history") as RunHistory;
            var runState = ReflectionIds.Member(__instance, "_runState") as RunState;
            var context = RunContextReader.TryRead();
            // RunHistory.Seed and RunState.Rng.StringSeed should agree; take the one the diary used.
            var decisions = PersonalRunDiary.Decisions();
            var candidates = new[] { history?.Seed, context?.RunSeed }.Where(x => !string.IsNullOrWhiteSpace(x)).Cast<string>().ToArray();
            var seed = candidates.FirstOrDefault(x => decisions.Any(d => d.Seed == x)) ?? candidates.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(seed) || history?.WasAbandoned == true) return;
            var win = history?.Win ?? false;
            var floor = runState?.TotalFloor ?? context?.Floor ?? 0;
            PersonalRunDiary.FlushPending();
            PersonalRunDiary.RecordOutcome(new RunOutcome(DateTimeOffset.UtcNow, context?.Character ?? "", seed, win, floor, history?.Ascension ?? context?.Ascension ?? 0));
            var catalog = Catalog.TryGetCached();
            string Name(string id) => catalog.TryGetValue(SpireCodexMetricsClient.NormalizeId(id), out var card) ? card.Name : id;
            AdviceOverlay.Render(__instance, "BUILD ADVISOR · итог\n" +
                RunReview.Build(seed, win, floor, PersonalRunDiary.Decisions(), PersonalRunDiary.Outcomes(), Name), null, context);
        }
        catch (Exception error) { GD.PrintErr("Build Advisor game over: " + error.Message); }
    }
}

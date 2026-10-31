using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Rewards;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Rewards;
using BuildAdvisor.Data;
using BuildAdvisor.UI;

namespace BuildAdvisor.Integration;

// M3: the post-combat reward list. When it offers potions, the panel rates them against the held
// potions and the open slots. Card rewards keep their own screen and advice.
[HarmonyPatch(typeof(NRewardsScreen), "_Ready")]
public static class RewardsScreenPatch
{
    private static readonly SpireCodexMetricsClient Metrics = new();
    private static readonly RelicCatalogClient PotionNames = new("potions");

    [HarmonyPostfix]
    public static async void AfterReady(Node __instance)
    {
        try
        {
            // Reward buttons are created right after _Ready.
            await Task.Delay(250);
            if (!GodotObject.IsInstanceValid(__instance)) return;
            var potions = Descendants(__instance).OfType<NRewardButton>()
                .Select(button => button.Reward is PotionReward reward && reward.Potion is { } potion ? ReflectionIds.FindId(potion) : null)
                .Where(id => id is not null).Cast<string>().Distinct().ToArray();
            if (potions.Length == 0) return;
            var context = RunContextReader.TryRead();
            if (context is null) return;
            var risk = SegmentRiskModel.Assess(context);
            var offered = potions.Select(id => (id, 0)).ToArray();
            decimal? riskValue = risk.IsAvailable ? risk.Probability : null;
            AdviceOverlay.Render(__instance, "BUILD ADVISOR · награды\n" +
                PotionAdvisor.Format(context, PotionAdvisor.Rate(context, offered, new Dictionary<string, EntityMetric>(), null, riskValue)), null, context);
            IReadOnlyDictionary<string, EntityMetric> metrics = new Dictionary<string, EntityMetric>();
            try { metrics = await Metrics.GetMetricsAsync("potions", context.Bracket, context.Character); }
            catch (Exception error) { GD.Print("Build Advisor potion metrics: " + error.Message); }
            var names = await PotionNames.GetAsync();
            if (!GodotObject.IsInstanceValid(__instance)) return;
            AdviceOverlay.Render(__instance, "BUILD ADVISOR · награды\n" +
                PotionAdvisor.Format(context, PotionAdvisor.Rate(context, offered, metrics, names, riskValue)) +
                (risk.IsAvailable ? "\n\n" + SegmentRiskModel.Summary(risk) : ""), null, context);
        }
        catch (Exception error) { GD.PrintErr("Build Advisor rewards: " + error.Message); }
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

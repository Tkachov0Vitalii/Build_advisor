using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Screens;
using BuildAdvisor.Data;
using BuildAdvisor.UI;

namespace BuildAdvisor.Integration;

[HarmonyPatch(typeof(NChooseARelicSelection), "_Ready")]
public static class RelicChoicePatch
{
    private static readonly SpireCodexMetricsClient Metrics = new();
    private static readonly DeckAdvisorClient DeckAdvisor = new();
    [HarmonyPostfix]
    public static async void AfterReady(Node __instance)
    {
        var ids = ReflectionIds.FindIds(__instance); if (ids.Count == 0) return;
        var context = RunContextReader.TryRead();
        var local = LocalDeckAdvisor.ForRelics(context, ids);
        AdviceOverlay.Render(__instance, "BUILD ADVISOR · реликвии\nЗагрузка общей статистики…\nЕсли сеть недоступна, совет будет рассчитан локально.");
        try
        {
            var metrics = await Metrics.GetMetricsAsync("relics", context?.Bracket ?? "a10", context?.Character);
            if (!GodotObject.IsInstanceValid(__instance)) return;
            var text = "BUILD ADVISOR · реликвии\n" + AdviceText.ForMetrics($"Статистика сообщества · {(context?.Bracket ?? "a10").ToUpperInvariant()}", ids, metrics);
            if (context is not null)
            {
                var support = await DeckAdvisor.GetRelicSupportAsync(context);
                var matches = ids.Where(support.ContainsKey).Select(id => $"{(metrics.TryGetValue(SpireCodexMetricsClient.NormalizeId(id), out var metric) ? metric.Name : id)}: у похожих победных билдов {support[id]:F0}%");
                text += $"\n\nКонтекст: Act {context.Act} · этаж {context.Floor} · {context.Cards.Count} карт · {context.Relics.Count} реликвий" + (matches.Any() ? "\n" + string.Join("\n", matches) : "\nСреди ближайших победных билдов этих реликвий пока нет.");
            }
            AdviceOverlay.Render(__instance, text);
        }
        catch (Exception error) { if (GodotObject.IsInstanceValid(__instance)) AdviceOverlay.Render(__instance, "BUILD ADVISOR · реликвии\n" + local + "\n\nСетевые данные недоступны — показан локальный совет."); GD.PrintErr($"Build Advisor relics: {error}"); }
    }
}

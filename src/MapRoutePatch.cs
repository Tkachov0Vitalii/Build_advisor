using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using BuildAdvisor.UI;

namespace BuildAdvisor.Integration;

// H1: whenever the map opens, every path from the current point to the boss is simulated and
// the reachable points get labels; the recommended path is marked with a star.
[HarmonyPatch(typeof(NMapScreen), nameof(NMapScreen.Open))]
public static class MapRoutePatch
{
    private static readonly EncounterDamageClient Damage = new();
    private static int generation;

    [HarmonyPostfix]
    public static void AfterOpen(NMapScreen __instance) => _ = RenderAsync(__instance, ++generation);

    private static async Task RenderAsync(NMapScreen map, int version)
    {
        try
        {
            // Points and paths are laid out right after Open.
            await Task.Delay(200);
            if (!GodotObject.IsInstanceValid(map) || version != generation) return;
            var context = RunContextReader.TryRead();
            if (context is null) return;
            var nodes = MapNodes(map);
            if (nodes.Count == 0) return;
            var graph = Graph(nodes, RunContextReader.CurrentMapPoint() as MapPoint, ReflectionIds.Member(map, "_startingPointNode") as NMapPoint);
            if (graph.Starts.Count == 0) { AdviceOverlay.ClearMapBadges(map); return; }
            var damage = await Damage.GetAsync(context.Act, context.Character);
            var plan = await Task.Run(() => RoutePlanner.Plan(context, graph, damage));
            if (!GodotObject.IsInstanceValid(map) || version != generation) return;
            AdviceOverlay.RenderMapBadges(map, () => GodotObject.IsInstanceValid(map) && map.IsOpen && map.IsVisibleInTree(), plan.ByNode.Where(x => nodes.ContainsKey(x.Key)).Select(x =>
                ((Control)nodes[x.Key], RoutePlanner.Label(x.Value), RoutePlanner.Severity(x.Value), x.Value.OnBest, Tooltip(x.Value))));
            AdviceOverlay.Render(map, "BUILD ADVISOR · маршрут\n" + RoutePlanner.Format(context, plan) +
                (SegmentRiskModel.Assess(context) is { IsAvailable: true } risk ? "\n\n" + SegmentRiskModel.Summary(risk) : ""), null, context);
        }
        catch (Exception error) { GD.PrintErr("Build Advisor route: " + error.Message); }
    }

    // The map stays in the scene under rooms; its labels go when it closes.
    [HarmonyPatch(typeof(NMapScreen), nameof(NMapScreen.Close))]
    public static class ClosePatch
    {
        [HarmonyPostfix]
        public static void AfterClose(NMapScreen __instance)
        {
            generation++;
            try { AdviceOverlay.ClearMapBadges(__instance); } catch (Exception error) { GD.PrintErr("Build Advisor route close: " + error.Message); }
        }
    }

    private static string Tooltip(NodeForecast node) => node.Type switch
    {
        "rest_site" => node.Rest ? $"Костёр: отдых +{node.Heal:0} HP (HP {node.HpBefore:0} → {node.HpAfter:0})." : $"Костёр: HP {node.HpBefore:0} хватает — улучшение карты.",
        _ when node.Damage > 0 => $"Ожидаемый урон {node.Damage:0} (среднее Spire Codex для этого акта) · HP {node.HpBefore:0} → {node.HpAfter:0} · {node.Danger * 100m:0}% текущего HP.",
        _ => "Без боя."
    };

    private static Dictionary<string, NMapPoint> MapNodes(NMapScreen map)
    {
        var result = new Dictionary<string, NMapPoint>(StringComparer.Ordinal);
        void Add(object? value)
        {
            if (value is NMapPoint node && GodotObject.IsInstanceValid(node) && node.Point is { } point) result[Key(point)] = node;
        }
        if (ReflectionIds.Member(map, "_mapPointDictionary") is System.Collections.IDictionary dictionary)
            foreach (var value in dictionary.Values) Add(value);
        Add(ReflectionIds.Member(map, "_bossPointNode"));
        Add(ReflectionIds.Member(map, "_secondBossPointNode"));
        Add(ReflectionIds.Member(map, "_startingPointNode"));
        return result;
    }

    private static string Key(MapPoint point) => point.coord.ToString();

    private static RouteGraph Graph(IReadOnlyDictionary<string, NMapPoint> nodes, MapPoint? current, NMapPoint? start)
    {
        var graph = new Dictionary<string, RouteNode>(StringComparer.Ordinal);
        foreach (var (key, node) in nodes)
        {
            var point = node.Point!;
            graph[key] = new RouteNode(key, RunContextReader.MapPointType(point), (point.Children ?? []).Select(Key).ToArray());
        }
        IReadOnlyList<string> starts = current is not null
            ? (current.Children ?? []).Select(Key).Where(graph.ContainsKey).ToArray()
            : start?.Point is { } first ? [Key(first)] : [];
        return new RouteGraph(starts, graph);
    }
}

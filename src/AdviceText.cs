using BuildAdvisor.Data;

namespace BuildAdvisor.UI;

public static class AdviceText
{
    public static string ForMetrics(string section, IEnumerable<string> ids, IReadOnlyDictionary<string, EntityMetric> metrics) =>
        section + "\n" + string.Join("\n", ids.Distinct().Select(id => metrics.TryGetValue(SpireCodexMetricsClient.NormalizeId(id), out var item)
            ? $"{item.Name}: общая оценка {(item.CodexScore ?? Math.Clamp(50m + (item.WinRate - .5m) * 100m, 0m, 100m)):F0}/100 · {Confidence(item.Samples)}"
            : $"{id}: пока недостаточно данных для общей оценки"));

    private static string Confidence(int samples) => samples switch
    {
        < 30 => "данных мало",
        < 150 => "данных достаточно",
        _ => "данных много"
    };
}

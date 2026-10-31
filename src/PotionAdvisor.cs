using BuildAdvisor.Data;

namespace BuildAdvisor.Integration;

// M3 (StsCompanion shows potion pick rate; we use lift, use rate and the slot situation).
// A potion is worth its community lift. When every slot is full, taking it only makes sense if it
// beats the weakest held potion; before a dangerous stretch an open slot should be filled.
internal static class PotionAdvisor
{
    internal sealed record Rated(string Id, string Name, decimal? Lift, int Samples, decimal? UseRate, int Price, string Verdict, string Line);

    // Potions without a lift are treated as average (+0.0); only data can rank them apart.
    private static decimal LiftOf(IReadOnlyDictionary<string, EntityMetric> metrics, string id) =>
        metrics.TryGetValue(SpireCodexMetricsClient.NormalizeId(id), out var metric) && metric.Lift is { } lift ? lift : 0m;

    public static IReadOnlyList<Rated> Rate(RunContext context, IReadOnlyList<(string Id, int Price)> offered,
        IReadOnlyDictionary<string, EntityMetric> metrics, IReadOnlyDictionary<string, string>? names, decimal? segmentRisk = null)
    {
        string Name(string id) => names is not null && names.TryGetValue(SpireCodexMetricsClient.NormalizeId(id), out var name) ? name
            : metrics.TryGetValue(SpireCodexMetricsClient.NormalizeId(id), out var metric) ? metric.Name : id;
        var weakest = context.Potions.OrderBy(id => LiftOf(metrics, id)).FirstOrDefault();
        var risky = segmentRisk is >= .35m;
        return offered.Select(item =>
        {
            metrics.TryGetValue(SpireCodexMetricsClient.NormalizeId(item.Id), out var metric);
            var lift = LiftOf(metrics, item.Id);
            string verdict;
            if (context.HasOpenPotionSlot)
                verdict = lift >= 0m || risky ? (risky ? "брать: впереди опасный участок" : "брать: есть свободный слот") : "можно пропустить: слабое зелье";
            else if (weakest is not null && lift > LiftOf(metrics, weakest) + 1m)
                verdict = $"заменить {Name(weakest)} ({TakeSkipModel.Format(LiftOf(metrics, weakest))} п.п.)";
            else
                verdict = "слоты заняты: не лучше текущих зелий";
            var stats = metric?.Lift is { } known
                ? $"{TakeSkipModel.Format(known)} п.п. к победе · уверенность {TakeSkipModel.EvidenceLabel(metric.LiftSamples)}" +
                  (metric.UseRate is { } use ? $" · используют {use * 100m:0.#}%" : "")
                : "статистики нет";
            var price = item.Price > 0 ? (item.Price <= context.Gold ? $" · {item.Price} золота" : $" · {item.Price} золота, не хватает {item.Price - context.Gold}") : "";
            return new Rated(item.Id, Name(item.Id), metric?.Lift, metric?.LiftSamples ?? 0, metric?.UseRate, item.Price, verdict, $"{Name(item.Id)}: {stats}{price} — {verdict}");
        }).OrderByDescending(x => x.Lift ?? 0m).ToArray();
    }

    public static string Format(RunContext context, IReadOnlyList<Rated> rated)
    {
        var slots = context.PotionSlots > 0 ? $"Слоты зелий: {context.Potions.Count}/{context.PotionSlots}." : "Слоты зелий не прочитаны.";
        return "Зелья\n" + slots + "\n" + string.Join("\n", rated.Select(x => x.Line));
    }
}

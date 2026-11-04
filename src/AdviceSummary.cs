namespace BuildAdvisor.Integration;

// 7.0: the head of the advice panel. The long text (formulas, plans, archetypes) stays below as
// "Подробный разбор"; the summary answers first: what to take, how sure, how the options rank.
internal sealed record AdviceRow(string Name, string Tier, decimal Score, string Note, bool Best);
internal sealed record AdviceSummary(string Verdict, bool Positive, string Detail, IReadOnlyList<AdviceRow> Rows, IReadOnlyList<string> Chips);

internal static class AdviceSummaries
{
    internal static string Tier(decimal score) => score >= 90 ? "S" : score >= 78 ? "A" : score >= 65 ? "B" : score >= 50 ? "C" : "D";

    private static IReadOnlyList<AdviceRow> Rows(IEnumerable<OfferScore> offers, Func<OfferScore, string> note, int take = 6)
    {
        var ranked = offers.OrderByDescending(x => x.Final).Take(take).ToArray();
        var best = ranked.FirstOrDefault()?.Final;
        return ranked.Select(x => new AdviceRow(x.Name, Tier(x.Final), x.Final, note(x), ranked.Length > 1 && x.Final == best)).ToArray();
    }

    private static string Delta(OfferScore offer) => $"{TakeSkipModel.Format(TakeSkipModel.WinDelta(offer))} п.п.";

    // Card rewards: the take/skip verdict on the win-rate scale.
    internal static AdviceSummary? ForCards(BuildAssessment assessment)
    {
        if (assessment.Offers.Count == 0) return null;
        var verdict = TakeSkipModel.Evaluate(assessment);
        var best = verdict.Best ?? assessment.Offers.OrderByDescending(x => x.Final).First();
        var chips = assessment.Needs.Where(x => x != "синергия/качество").Select(x => "нужно: " + x)
            .Concat(assessment.Archetypes.Select(x => "архетип: " + x)).ToArray();
        return verdict.Take
            ? new AdviceSummary($"Возьмите {best.Name}", true,
                $"{best.Final:F0}/100 · {TakeSkipModel.Format(verdict.WinDelta)} п.п. к победе против пропуска · уверенность {verdict.Evidence}", Rows(assessment.Offers, Delta), chips)
            : new AdviceSummary("Лучше пропустить", false,
                $"Лучшая карта {best.Name}: {TakeSkipModel.Format(verdict.WinDelta)} п.п. — меньше порога пользы", Rows(assessment.Offers, Delta), chips);
    }

    // Shop, relic choice and event screens: the ranked options with a short verdict.
    internal static AdviceSummary? ForChoice(string verdictPrefix, IEnumerable<OfferScore> offers, string detail = "")
    {
        var rows = Rows(offers, x => x.Role, 8);
        if (rows.Count == 0) return null;
        var best = rows[0];
        return new AdviceSummary($"{verdictPrefix}: {best.Name}", true,
            detail.Length > 0 ? detail : $"{best.Score:F0}/100 · тир {best.Tier}", rows, []);
    }

    // 7.1: the hover text of the Skip button ribbon.
    internal static string SkipExplanation(TakeSkipVerdict verdict) => verdict.Best is null
        ? "Пропуск: награда ещё не оценена."
        : verdict.Take
            ? $"Пропуск: 0 п.п. к победе.\nЛучше взять {verdict.Best.Name}: {TakeSkipModel.Format(verdict.WinDelta)} п.п. к победе против пропуска (уверенность {verdict.Evidence})."
            : $"Совет: пропустить награду.\nЛучшая карта {verdict.Best.Name} даёт {TakeSkipModel.Format(verdict.WinDelta)} п.п. — меньше порога пользы; лишняя карта разбавляет колоду.";

}

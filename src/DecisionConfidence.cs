namespace BuildAdvisor.Integration;

internal sealed record DecisionConfidence(string Label, string Explanation, bool CanAutoPick, decimal Lead, decimal Evidence)
{
    public decimal ScoreLow { get; init; }
    public decimal ScoreHigh { get; init; }
}

// A user-facing confidence gate. It deliberately describes what the current model knows;
// it is not presented as a calibrated probability until the held-out corpus is available.
internal static class DecisionConfidenceModel
{
    // Selected on validation only (226,563 reward decisions), then confirmed unchanged on
    // the 208,192-decision test split. Keep auto-actions deliberately rarer than advice.
    internal const decimal AutoPickMinimumScore = 90m;
    internal const decimal AutoPickMinimumLead = 20m;
    internal const int AutoPickMaximumSkipProbability = 11;
    // 6.18: the pick must also beat skipping by a clear win-rate margin on the take/skip scale.
    internal const decimal AutoPickMinimumWinDelta = 3m;

    public static DecisionConfidence Evaluate(BuildAssessment assessment)
    {
        var ranked = assessment.Offers.OrderByDescending(x => x.Final).ToArray();
        if (ranked.Length == 0) return new("нет оценки", "варианты не прочитаны", false, 0m, 0m);
        var best = ranked[0];
        var runnerUp = ranked.Skip(1).FirstOrDefault()?.Final ?? assessment.SkipScore;
        var lead = best.Final - Math.Max(runnerUp, assessment.SkipScore);
        var evidence = best.Confidence;
        var margin = Math.Clamp((1m - evidence) * 20m + (lead < 12m ? 4m : 0m), 3m, 24m);
        var low = Math.Max(0m, best.Final - margin); var high = Math.Min(100m, best.Final + margin);
        if (evidence < .30m)
            return new("мало данных", $"диапазон модели {low:F0}–{high:F0}; совет опирается в основном на локальную модель", false, lead, evidence) { ScoreLow = low, ScoreHigh = high };
        var skipThreshold = RunHistoryInsights.SkipThreshold * 100m;
        if (best.Final < 70m || lead < 8m || assessment.SkipScore >= skipThreshold)
            return new("спорный выбор", $"диапазон модели {low:F0}–{high:F0}; варианты близки или пропуск остаётся конкурентным", false, lead, evidence) { ScoreLow = low, ScoreHigh = high };
        var verdict = TakeSkipModel.Evaluate(assessment);
        var safe = best.Final >= AutoPickMinimumScore && lead >= AutoPickMinimumLead &&
                   assessment.SkipScore <= AutoPickMaximumSkipProbability && low >= 80m &&
                   verdict.Take && verdict.Best?.Id == best.Id && verdict.WinDelta >= AutoPickMinimumWinDelta && verdict.Evidence != "низкая";
        return safe
            ? new("уверенный автовыбор", $"диапазон модели {low:F0}–{high:F0}, отрыв {lead:F0}, вероятность пропуска {assessment.SkipScore}%", true, lead, evidence) { ScoreLow = low, ScoreHigh = high }
            : new("умеренная уверенность", $"диапазон модели {low:F0}–{high:F0}; преимущество лучшего варианта {lead:F0}", false, lead, evidence) { ScoreLow = low, ScoreHigh = high };
    }
}

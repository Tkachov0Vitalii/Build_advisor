namespace BuildAdvisor.Integration;

internal enum AutoPickAction { None, Take, Skip }
internal sealed record AutoPickDecision(AutoPickAction Action, OfferScore? Card);

// Session-level opt-in (off by default). Since 7.1 auto-pick does what the panel says: it takes
// the card the take/skip verdict recommends and skips when the verdict is "better skip"; in the
// shop it buys the optimal basket of the plan. Up to 7.0 it acted only on score ≥ 90 with a
// 20-point lead, which made the toggle look broken: it almost never picked anything.
internal static class AutoPickSettings
{
    public static bool Enabled { get; set; }

    public static AutoPickDecision Decide(BuildAssessment assessment)
    {
        if (assessment.Offers.Count == 0) return new(AutoPickAction.None, null);
        var verdict = TakeSkipModel.Evaluate(assessment);
        if (verdict.Best is null) return new(AutoPickAction.None, null);
        return verdict.Take ? new(AutoPickAction.Take, verdict.Best) : new(AutoPickAction.Skip, null);
    }
}

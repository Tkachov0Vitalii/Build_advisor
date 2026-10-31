namespace BuildAdvisor.Integration;

// Session-level opt-in. Automatic actions are intentionally conservative: the player must
// enable the toggle, there must be real community evidence, and the top option must be clear.
internal static class AutoPickSettings
{
    public static bool Enabled { get; set; }

    public static bool ShouldPick(BuildAssessment assessment)
        => DecisionConfidenceModel.Evaluate(assessment).CanAutoPick;
}

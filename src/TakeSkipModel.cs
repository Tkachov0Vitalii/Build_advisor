namespace BuildAdvisor.Integration;

internal sealed record TakeSkipVerdict(OfferScore? Best, decimal WinDelta, bool Take, string Evidence, string Text);

// Take-or-skip on one scale, in percentage points of win rate, the way MetaBot frames it:
// skipping is 0, a card is worth its community lift (runs that took it minus runs that did
// not) plus this deck's adjustments converted to the same unit. The 6.16 rule compared a
// 0-100 card score with the percentage of players who skipped, which are different scales.
internal static class TakeSkipModel
{
    // Least-squares fit of Spire Codex lift on Codex Score over 491 cards (A10, 2026-10-30):
    // lift ≈ 0.19 × (score − 27), residual SD 1.9 p.p. Used to convert deck adjustments and to
    // estimate lift when the API has none.
    internal const decimal PointsPerScore = 0.19m;
    internal const decimal ZeroLiftScore = 27m;
    // MetaBot recommends a card when it beats skipping by half a point. With little evidence a
    // larger margin is required.
    internal const decimal TakeMargin = 0.5m;
    internal const decimal LowEvidenceMargin = 2m;
    internal const int HighEvidence = 1000;
    internal const int MediumEvidence = 300;

    public static decimal WinDelta(OfferScore offer)
    {
        var baseline = offer.CommunityLift ?? (offer.Community - ZeroLiftScore) * PointsPerScore;
        var deckShift = (offer.Final - offer.Community) * PointsPerScore;
        return decimal.Round(baseline + deckShift, 1);
    }

    // Without a lift the sample-based confidence (√(samples / 1000)) decides; 0.55 ≈ 300 samples.
    public static string Evidence(OfferScore offer) => offer.CommunityLift is not null
        ? EvidenceLabel(offer.LiftSamples)
        : offer.Confidence >= .95m ? "высокая" : offer.Confidence >= .55m ? "средняя" : "низкая";
    public static string EvidenceLabel(int samples) => samples >= HighEvidence ? "высокая" : samples >= MediumEvidence ? "средняя" : "низкая";

    public static TakeSkipVerdict Evaluate(BuildAssessment assessment)
    {
        var best = assessment.Offers.OrderByDescending(WinDelta).FirstOrDefault();
        if (best is null) return new(null, 0m, false, "нет данных", "награда не прочитана");
        var delta = WinDelta(best);
        var evidence = Evidence(best);
        var margin = evidence == "низкая" ? LowEvidenceMargin : TakeMargin;
        var take = delta >= margin;
        var text = take
            ? $"взять {best.Name} ({Format(delta)} п.п. к победе против пропуска, уверенность {evidence})"
            : $"пропустить (лучшая карта {best.Name}: {Format(delta)} п.п., нужно не меньше {Format(margin)})";
        return new(best, delta, take, evidence, text);
    }

    public static string Format(decimal points) => points.ToString("+0.0;-0.0;0.0", System.Globalization.CultureInfo.InvariantCulture);
}

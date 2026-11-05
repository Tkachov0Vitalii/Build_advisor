namespace BuildAdvisor.Integration;

internal sealed record TakeSkipVerdict(OfferScore? Best, decimal WinDelta, bool Take, string Evidence, string Text)
{
    // 7.2: the delta the best card needs to be worth taking (margin + learned skip probability).
    public decimal Bar { get; init; }
}

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
    internal const int HighEvidence = 1000;
    internal const int MediumEvidence = 300;

    // 7.2: the delta is measured from the card players typically take in this act and deck-size
    // bucket (OfferScore.TypicalPick), not from zero. Community lift is a correlation: the average
    // card has +7 pp, so against zero almost every reward said "take" (0.2 % skips in the corpus
    // replay, while players skip 32 % and winners 37 %). Relics and other offers keep TypicalPick 0.
    public static decimal WinDelta(OfferScore offer) => decimal.Round(RawWinDelta(offer) - offer.TypicalPick, 1);

    public static decimal RawWinDelta(OfferScore offer)
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

    // 7.2 verdict (tools/calibrate_take_skip.py, docs/take-skip-calibration-report.json): take the
    // best card when it beats the typical pick by TakeSkipCalibration.Margin; a high learned skip
    // probability (players in this state usually take nothing) raises the bar.
    public static decimal SkipBar(int skipPercent) =>
        TakeSkipCalibration.Margin + TakeSkipCalibration.SkipWeight * Math.Max(0m, skipPercent / 100m - .5m) * 10m;

    public static TakeSkipVerdict Evaluate(BuildAssessment assessment)
    {
        var best = assessment.Offers.OrderByDescending(WinDelta).FirstOrDefault();
        if (best is null) return new(null, 0m, false, "нет данных", "награда не прочитана");
        var delta = WinDelta(best);
        var evidence = Evidence(best);
        var bar = SkipBar(assessment.SkipScore);
        var take = delta >= bar;
        var text = take
            ? $"взять {best.Name} ({Format(delta)} п.п. относительно типичной карты, уверенность {evidence})"
            : $"пропустить (лучшая карта {best.Name}: {Format(delta)} п.п. относительно типичной карты, нужно не меньше {Format(bar)})";
        return new(best, delta, take, evidence, text) { Bar = bar };
    }

    public static string Format(decimal points) => points.ToString("+0.0;-0.0;0.0", System.Globalization.CultureInfo.InvariantCulture);
}

// 7.2: fitted by tools/calibrate_take_skip.py on the train split of the run corpus (177,251 runs,
// 2,184,681 card rewards incl. skips); report: docs/take-skip-calibration-report.json.
// TypicalPick: median win-rate delta (lift + deck adjustments) of the card players took, by act
// and by deck size relative to the size expected for the floor (the skip model's buckets).
// Margin and SkipWeight: advise skipping about as often as winning runs skip (± 3 pp), maximising
// agreement with winners' take-or-skip decisions.
internal static class TakeSkipCalibration
{
    internal const decimal Margin = 0.25m;
    internal const decimal SkipWeight = 6.0m;
    private static readonly decimal[] ExcessEdges = [-4m, -1m, 2m, 5m, 9m];
    private static readonly decimal[,] Typical =
    {
        { 9.3m, 8.8m, 8.5m, 8.9m, 9.8m, 8.7m },
        { 10.0m, 9.6m, 9.1m, 8.2m, 7.2m, 6.0m },
        { 9.4m, 9.3m, 7.7m, 5.7m, 4.2m, 3.2m }
    };

    internal static decimal TypicalPick(int act, int deckSize, int expectedDeckSize)
    {
        var row = Math.Clamp(act, 1, 3) - 1;
        var bucket = ExcessEdges.Count(edge => deckSize - expectedDeckSize > edge);
        return Typical[row, bucket];
    }

    internal static decimal TypicalPick(RunContext context) =>
        TypicalPick(context.Act, context.Cards.Count, RunHistoryInsights.ExpectedDeckSize(context.Character, context.Floor));
}

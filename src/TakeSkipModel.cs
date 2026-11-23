using BuildAdvisor.Data;

namespace BuildAdvisor.Integration;

internal sealed record TakeSkipVerdict(OfferScore? Best, decimal WinDelta, bool Take, string Evidence, string Text);

// 7.3.1: what a card reward's lifts are measured against: the centre and spread of the statistics
// in use and the take threshold for this act, deck size and learned skip probability.
internal readonly record struct TakeSkipFrame(decimal Center, decimal Spread, decimal Bar);

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

    // The number on ribbons and in the verdict: percentage points above (+) or below (−) the take
    // threshold. 0 is the threshold; a reward is skipped when its best card is below it.
    // 7.2 measured from the median picked card with raw lifts fitted on the "all" bracket: at
    // ascension 10 ("a10" lifts are about 2 pp lower) nearly every card was negative, B to S tiers
    // included, and act 1 could pass without one "take". Offers outside a card reward (relics, shop)
    // have no frame and keep the raw scale.
    public static decimal WinDelta(OfferScore offer)
    {
        if (offer.Frame is not { } frame) return RawWinDelta(offer);
        var lift = offer.CommunityLift is { } value
            ? (value - frame.Center) * TakeSkipCalibration.ReferenceSpread / frame.Spread
            : (offer.Community - ZeroLiftScore) * PointsPerScore - TakeSkipCalibration.FallbackCenter;
        var deckShift = (offer.Final - offer.Community) * PointsPerScore;
        return decimal.Round(lift + deckShift - frame.Bar, 1);
    }

    // 7.3.1: the middle and the spread of the lift scale in the statistics in use. Brackets differ:
    // the median card lift is +9.1 pp in "all" but +7.2 pp in "a10", and "a10" is a quarter wider.
    // 7.2 was fitted on "all", so at ascension 10 every card read about 2 pp too low and skipping
    // was advised almost always. Lifts are now read relative to this centre, in units of the spread.
    internal readonly record struct LiftScale(decimal Center, decimal Spread);

    // The scale is taken over the character's own cards (the pool a reward draws from): in a
    // per-character metrics set the other classes keep global rows with different lifts.
    internal static LiftScale? Scale(IReadOnlyDictionary<string, EntityMetric> metrics,
        IReadOnlyDictionary<string, CardDefinition>? catalog = null, string? character = null)
    {
        var rows = metrics.Values.Where(x => x.Lift is not null && x.LiftSamples >= 200).ToArray();
        if (catalog is not null && character is not null)
        {
            var own = rows.Where(x => catalog.TryGetValue(SpireCodexMetricsClient.NormalizeId(x.Id), out var card) &&
                                      string.Equals(card.Character, character, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (own.Length >= 20) rows = own;
        }
        var lifts = rows.Select(x => x.Lift!.Value).OrderBy(x => x).ToArray();
        if (lifts.Length < 20) return null;
        return new(lifts[lifts.Length / 2], Math.Max(1m, lifts[lifts.Length * 3 / 4] - lifts[lifts.Length / 4]));
    }

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

    public static TakeSkipVerdict Evaluate(BuildAssessment assessment)
    {
        var best = assessment.Offers.OrderByDescending(WinDelta).FirstOrDefault();
        if (best is null) return new(null, 0m, false, "нет данных", "награда не прочитана");
        var delta = WinDelta(best);
        var evidence = Evidence(best);
        var take = delta >= 0m;
        var text = take
            ? $"взять {best.Name} ({Format(delta)} п.п. к порогу взятия, уверенность {evidence})"
            : $"пропустить (лучшая карта {best.Name}: {Format(delta)} п.п. к порогу взятия)";
        return new(best, delta, take, evidence, text);
    }

    public static string Format(decimal points) => points.ToString("+0.0;-0.0;0.0", System.Globalization.CultureInfo.InvariantCulture);
}

// Fitted by tools/calibrate_take_skip.py on the train split of the run corpus (177,251 runs,
// 2,184,681 card rewards incl. skips); report: docs/take-skip-calibration-report.json.
// Bars: for each act and deck size relative to the size expected for the floor (the skip model's
// buckets), the quantile of the best card's score at the skip rate of winning runs, so skipping is
// advised as often as winners skip in that situation (act 1: 23 %, act 2: 40 %, act 3: 53 %).
// The score is the lift relative to the centre of the statistics in use, scaled to ReferenceSpread
// pp per interquartile range, plus deck adjustments; a learned skip probability above 50 % raises
// the threshold by SkipWeight pp per 10 points.
internal static class TakeSkipCalibration
{
    internal const decimal ReferenceSpread = 6.0m;
    internal const decimal SkipWeight = 6.0m;
    // Without lifts (offline, an empty response) lift is estimated from the Codex Score with the
    // A10 fit, whose median card is +7.2 pp.
    internal const decimal FallbackCenter = 7.2m;
    private static readonly decimal[] ExcessEdges = [-4m, -1m, 2m, 5m, 9m];
    // 7.5: refitted on v0.111.0/v0.111.1 runs (1.6M rewards, tools/refit_for_version.sh); report
    // docs/take-skip-calibration-report.json.
    private static readonly decimal[,] Bars =
    {
        { 0.79m, 0.54m, 0.35m, 0.14m, 0.19m, 0.4m },
        { 1.4m, 1.32m, 1.07m, 0.2m, -0.8m, -2.2m },
        { -0.46m, 0.6m, -0.59m, -1.8m, -2.8m, -3.2m }
    };

    internal static decimal Bar(int act, int deckSize, int expectedDeckSize, int skipPercent)
    {
        var row = Math.Clamp(act, 1, 3) - 1;
        var bucket = ExcessEdges.Count(edge => deckSize - expectedDeckSize > edge);
        return Bars[row, bucket] + SkipWeight * Math.Max(0m, skipPercent / 100m - .5m) * 10m;
    }

    internal static TakeSkipFrame Frame(RunContext context, int deckSize, int skipPercent, IReadOnlyDictionary<string, EntityMetric> metrics,
        IReadOnlyDictionary<string, CardDefinition>? catalog = null)
    {
        var scale = TakeSkipModel.Scale(metrics, catalog, context.Character) ?? new TakeSkipModel.LiftScale(FallbackCenter, ReferenceSpread);
        return new(scale.Center, scale.Spread, Bar(context.Act, deckSize, RunHistoryInsights.ExpectedDeckSize(context.Character, context.Floor), skipPercent));
    }
}

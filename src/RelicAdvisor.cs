using BuildAdvisor.Data;

namespace BuildAdvisor.Integration;

// M9 (TierLister and StsCompanion rank relics by community data; we add the deck): one relic
// score used by the relic choice screen, the shop and Ancient event options. It is an
// OfferScore, so the breakdown tooltip, the win-rate scale and the badges work as for cards.
internal static class RelicAdvisor
{
    public static OfferScore Score(RunContext context, string id, IReadOnlyDictionary<string, EntityMetric> metrics,
        IReadOnlyDictionary<string, string>? names = null, decimal? segmentRisk = null)
    {
        var key = SpireCodexMetricsClient.NormalizeId(id);
        var metric = metrics.TryGetValue(key, out var found) ? found : null;
        // Baseline: Codex Score, else the score the lift corresponds to (lift ≈ 0.19 × (score − 27)).
        var community = metric?.CodexScore
            ?? (metric?.Lift is { } lift ? Math.Clamp(TakeSkipModel.ZeroLiftScore + lift / TakeSkipModel.PointsPerScore, 0m, 100m) : 50m);
        var adjustments = new List<ScoreAdjustment>();
        var notes = new List<string>();
        void Adjust(decimal value, string label, string note) { adjustments.Add(new(value, label)); notes.Add(note); }

        // The average value of a relic is already in its community lift; only a need the deck has
        // right now moves the score.
        if (RelicKnowledge.GivesEnergy(id) && context.AverageCost >= 1.7m) Adjust(24m, "даёт энергию", "решает дефицит энергии дорогой колоды");
        if (RelicKnowledge.HelpsSurvival(id))
        {
            if (context.Hp > 0 && context.Hp * 3 < context.MaxHp) Adjust(16m, "выживаемость", "поддерживает выживаемость при низком HP");
            else if (segmentRisk is >= .35m) Adjust(8m, "выживаемость", "защищает перед опасным участком");
        }
        if (RelicKnowledge.HelpsEconomy(id))
        {
            if (context.Act >= 3) Adjust(-8m, "экономика", "в последнем акте осталось мало магазинов");
            else if (context.Gold < 100) Adjust(10m, "экономика", "улучшает экономику следующих магазинов");
        }
        var userTier = TierOverrides.ForRelic(context.Character, id);
        if (userTier != 0) Adjust(userTier, "ваша поправка", $"ваша поправка {userTier:+0;-0}");

        var raw = community + adjustments.Sum(x => x.Value);
        var final = Math.Clamp(raw, 0m, 100m);
        if (final != raw) adjustments.Add(new(final - raw, "граница шкалы"));
        var name = names is not null && names.TryGetValue(key, out var localized) ? localized : metric?.Name ?? id;
        var reason = notes.Count > 0 ? string.Join("; ", notes) : "общая ценность по данным сообщества";
        var confidence = metric is null ? .2m : Math.Min(1m, (decimal)Math.Sqrt(Math.Max(0, metric.LiftSamples > 0 ? metric.LiftSamples : metric.Samples) / 1000d));
        return new OfferScore(id, name, final, community, final - community, "Relic", reason, confidence,
            Adjustments: adjustments, CommunityLift: metric?.Lift, LiftSamples: metric?.LiftSamples ?? 0);
    }

    public static IReadOnlyList<OfferScore> Rank(RunContext context, IEnumerable<string> ids, IReadOnlyDictionary<string, EntityMetric> metrics,
        IReadOnlyDictionary<string, string>? names = null, decimal? segmentRisk = null) =>
        ids.Distinct(StringComparer.OrdinalIgnoreCase).Select(id => Score(context, id, metrics, names, segmentRisk))
            .OrderByDescending(TakeSkipModel.WinDelta).ToArray();

    public static string Format(RunContext context, IReadOnlyList<OfferScore> ranked)
    {
        if (ranked.Count == 0) return "Реликвии не прочитаны.";
        var lines = ranked.Select((x, index) =>
            $"{(index == 0 && ranked.Count > 1 ? "★ " : "")}{x.Name}: {x.Final:0}/100 · {TakeSkipModel.Format(TakeSkipModel.WinDelta(x))} п.п. к победе · уверенность {TakeSkipModel.Evidence(x)} — {x.Reason}");
        var best = ranked[0];
        var margin = ranked.Count > 1 ? TakeSkipModel.WinDelta(best) - TakeSkipModel.WinDelta(ranked[1]) : 0m;
        var verdict = ranked.Count < 2 ? "" : margin < .5m
            ? "\nРазница меньше 0,5 п.п.: выбирайте по плану колоды."
            : $"\nСовет: {best.Name} (+{margin:0.0} п.п. к следующей).";
        return $"Act {context.Act} · {context.Cards.Count} карт · средняя стоимость {context.AverageCost:0.0}\n" +
               string.Join("\n", lines) + verdict + "\n\nРазбор лучшей:\n" + ScoreExplanation.Breakdown(best) +
               "\n\nЛифт реликвий смещён длиной забега (поздние реликвии получают выжившие), но при выборе из нескольких реликвий смещение у всех одинаковое.";
    }
}

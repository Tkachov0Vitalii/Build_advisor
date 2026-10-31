namespace BuildAdvisor.Integration;

// M7 (Deck Compass shows a post-run dashboard): the run's card decisions against the mod's
// advice, the result, and how often following the advice went together with winning across the
// personal diary. Pure functions over diary records, so the review is testable outside the game.
internal static class RunReview
{
    internal sealed record Divergence(int Floor, string Chosen, string Advised, decimal Loss);

    // Win-rate points the choice gave up against the advice, by the mod's own estimate at the time.
    internal static decimal? Loss(PersonalDecision decision)
    {
        if (decision.Advised is null || decision.Deltas is null || decision.Chosen is null) return null;
        decimal Value(string id) => id == PersonalRunDiary.SkipMarker ? 0m : decision.Deltas.TryGetValue(id, out var v) ? v : 0m;
        return decimal.Round(Value(decision.Advised) - Value(decision.Chosen), 1);
    }

    internal static bool Followed(PersonalDecision decision) =>
        decision.Advised is not null && decision.Chosen is not null && string.Equals(decision.Advised, decision.Chosen, StringComparison.OrdinalIgnoreCase);

    public static string Build(string seed, bool win, int floor, IReadOnlyList<PersonalDecision> diary, IReadOnlyList<RunOutcome> outcomes, Func<string, string>? name = null)
    {
        string Name(string id) => id == PersonalRunDiary.SkipMarker ? "пропуск" : name?.Invoke(id) ?? id;
        var run = diary.Where(x => x.Seed == seed && x.Chosen is not null).OrderBy(x => x.Floor).ToArray();
        var advised = run.Where(x => x.Advised is not null).ToArray();
        var lines = new List<string> { $"Разбор забега: {(win ? "победа" : "поражение")} · этаж {floor}" };
        if (run.Length == 0)
        {
            lines.Add("Выборов карт в этом забеге не записано.");
            return string.Join("\n", lines);
        }
        var followed = advised.Count(Followed);
        lines.Add($"Наград с картами: {run.Length} · совпало с советом: {followed}/{advised.Length}" + (advised.Length > 0 ? $" ({followed * 100 / advised.Length}%)" : ""));
        var divergences = advised.Where(x => !Followed(x)).Select(x => new Divergence(x.Floor, x.Chosen!, x.Advised!, Loss(x) ?? 0m))
            .Where(x => x.Loss > 0m).OrderByDescending(x => x.Loss).ToArray();
        if (divergences.Length > 0)
        {
            lines.Add("Крупнейшие расхождения:");
            lines.AddRange(divergences.Take(5).Select(x => $"этаж {x.Floor}: {Name(x.Chosen)} вместо {Name(x.Advised)} (−{x.Loss:0.0} п.п.)"));
            lines.Add($"Всего по оценке мода: −{divergences.Sum(x => x.Loss):0.0} п.п. — это оценка на момент выбора, а не причина исхода.");
        }
        else lines.Add("Выборы не уступали совету по оценке мода.");

        // Across runs: agreement with advice in wins and in losses (from 3 finished runs each).
        var bySeed = diary.Where(x => x.Seed is not null && x.Advised is not null && x.Chosen is not null).GroupBy(x => x.Seed!)
            .ToDictionary(x => x.Key, x => (decimal)x.Count(Followed) / x.Count());
        var rated = outcomes.Where(x => bySeed.ContainsKey(x.Seed)).ToArray();
        var wins = rated.Where(x => x.Win).Select(x => bySeed[x.Seed]).ToArray();
        var losses = rated.Where(x => !x.Win).Select(x => bySeed[x.Seed]).ToArray();
        lines.Add("");
        if (wins.Length >= 3 && losses.Length >= 3)
            lines.Add($"По вашим забегам: совпадение с советом в победах {wins.Average() * 100m:0}% ({wins.Length}), в поражениях {losses.Average() * 100m:0}% ({losses.Length}).");
        else
            lines.Add($"Сравнение побед и поражений появится после 3 побед и 3 поражений с записанными советами (сейчас побед {wins.Length}, поражений {losses.Length}).");
        var all = outcomes.Count;
        if (all > 0) lines.Add($"Записано забегов: {all} · побед {outcomes.Count(x => x.Win)} ({outcomes.Count(x => x.Win) * 100 / all}%) · средний этаж {outcomes.Average(x => x.Floor):0}.");
        return string.Join("\n", lines);
    }
}

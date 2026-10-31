namespace BuildAdvisor.Core;

/// <summary>Ranks card choices using comparable, completed and opt-in player runs.</summary>
public sealed class RecommendationEngine
{
    private const int MinimumSampleForStrongClaim = 30;

    public IReadOnlyList<CardAdvice> Recommend(CurrentRunState current, IEnumerable<RunSnapshot> history)
    {
        var comparable = history.Where(run =>
            run.Character == current.Character &&
            run.GameVersion == current.GameVersion &&
            Math.Abs(run.Ascension - current.Ascension) <= 1 &&
            Similarity(current, run) >= 0.45m).ToList();

        return current.OfferedCards.Select(card => BuildAdvice(card, comparable))
            .OrderByDescending(advice => advice.Confidence * advice.WinRateAfterPick)
            .ThenByDescending(advice => advice.PickRate)
            .ToList();
    }

    private static CardAdvice BuildAdvice(string card, IReadOnlyList<RunSnapshot> runs)
    {
        var offered = runs.Where(run => run.OfferedCards.Contains(card)).ToList();
        var picked = offered.Where(run => run.PickedCard == card).ToList();
        var matches = offered.Count;
        var pickedCount = picked.Count;
        var wins = picked.Count(run => run.Won);
        var pickRate = matches == 0 ? 0 : decimal.Round((decimal)pickedCount / matches, 2);
        var winRate = pickedCount == 0 ? 0 : decimal.Round((decimal)wins / pickedCount, 2);
        // Shrinks small samples toward neutral (50%) instead of overstating a few lucky wins.
        var confidence = decimal.Round(Math.Min(1m, (decimal)pickedCount / MinimumSampleForStrongClaim), 2);
        var verdict = matches == 0 ? "Недостаточно похожих ранов" :
            pickedCount < 8 ? "Слабый сигнал — мало выборок" :
            winRate >= .60m ? "Часто усиливает похожий билд" :
            winRate <= .40m ? "В похожих билдах результат слабее" : "Нейтральный вариант";

        return new CardAdvice(card, matches, pickedCount, wins, pickRate, winRate, confidence, verdict);
    }

    private static decimal Similarity(CurrentRunState current, RunSnapshot past)
    {
        var deck = Jaccard(current.Deck, past.Deck);
        var relics = Jaccard(current.Relics, past.Relics);
        var boss = current.UpcomingBoss is not null && current.UpcomingBoss == past.UpcomingBoss ? .10m : 0m;
        var floor = 1m - Math.Min(1m, Math.Abs(current.Floor - past.Floor) / 20m);
        return (.50m * deck) + (.25m * relics) + (.15m * floor) + boss;
    }

    private static decimal Jaccard(IReadOnlySet<string> a, IReadOnlySet<string> b)
    {
        if (a.Count == 0 && b.Count == 0) return 1m;
        var intersection = a.Count(b.Contains);
        var union = a.Count + b.Count - intersection;
        return union == 0 ? 0m : (decimal)intersection / union;
    }
}

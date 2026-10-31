using BuildAdvisor.Data;

namespace BuildAdvisor.Integration;

internal sealed record ScoreAdjustment(decimal Value, string Label);
internal sealed record OfferScore(string Id, string Name, decimal Final, decimal Community, decimal Context, string Role, string Reason,
    decimal Confidence = 0m, decimal Personal = 0m, string Impact = "", string TagText = "",
    IReadOnlyList<ScoreAdjustment>? Adjustments = null);
internal sealed record BuildAssessment(string Summary, IReadOnlyList<string> Needs, IReadOnlyList<string> Archetypes, decimal AverageCost, int SkipScore, IReadOnlyList<OfferScore> Offers);

// The Regent's Colorless route is a creation engine, not a blanket bonus to every gray card.
// Current game text and the 35,631-run prior distinguish payoffs, generators and cards that
// dispose of weak random creations. This prevents a generator without an engine from looking
// like a complete build by itself.
internal static class RegentColorlessArchetype
{
    private static readonly HashSet<string> Payoffs = new(StringComparer.OrdinalIgnoreCase)
        { "ARSENAL", "PILLAR_OF_CREATION", "SUPERMASSIVE" };
    private static readonly HashSet<string> Generators = new(StringComparer.OrdinalIgnoreCase)
        { "MANIFEST_AUTHORITY", "BUNDLE_OF_JOY", "QUASAR", "SPECTRUM_SHIFT", "JACK_OF_ALL_TRADES", "HEIRLOOM_HAMMER" };
    private static readonly HashSet<string> Supports = new(StringComparer.OrdinalIgnoreCase)
        { "BEGONE", "TYRANNY", "CHARGE", "GUARDS", "HEIRLOOM_HAMMER" };

    private static string Key(CardDefinition card) => SpireCodexMetricsClient.NormalizeId(card.Id);
    public static bool IsPayoff(CardDefinition card) => Payoffs.Contains(Key(card));
    public static bool IsGenerator(CardDefinition card)
    {
        if (Generators.Contains(Key(card))) return true;
        var text = (card.Id + " " + card.Description).ToUpperInvariant();
        return Has(text, "RANDOM COLORLESS", "БЕСЦВЕТН") && Has(text, "ADD", "CREATE", "ДОБАВ", "СОЗДА");
    }
    public static bool IsSupport(CardDefinition card) => Supports.Contains(Key(card));
    public static bool IsMatch(CardDefinition card) => IsPayoff(card) || IsGenerator(card) || IsSupport(card);
    public static decimal Bonus(CardDefinition card, IReadOnlyList<CardDefinition> deck)
    {
        var payoffCount = deck.Count(IsPayoff);
        var generatorCount = deck.Count(IsGenerator);
        if (IsPayoff(card)) return generatorCount > 0 ? 18m : 10m;
        if (IsGenerator(card)) return payoffCount > 0 ? 16m : 5m;
        if (IsSupport(card)) return generatorCount > 0 ? 9m : 3m;
        return 0m;
    }
    private static bool Has(string text, params string[] tokens) => tokens.Any(text.Contains);
}

// Relic roles taken from the game assembly (Slay the Spire 2 v0.111): relics that override
// ModifyMaxEnergy, ModifyHpLost and ModifyMerchantPrice. The 6.16 substring lists (COFFEE,
// CROWN, FUSION, KEY…) were Slay the Spire 1 names and matched no relic in this game.
internal static class RelicKnowledge
{
    private static readonly HashSet<string> MaxEnergy = new(StringComparer.Ordinal)
    {
        "BLESSEDANTLER", "BLOODSOAKEDROSE", "BREAD", "ECTOPLASM", "PAELSFLESH", "PHILOSOPHERSSTONE",
        "PRISMATICGEM", "PUMPKINCANDLE", "SOZU", "SPIKEDGAUNTLETS", "VELVETCHOKER", "WHISPERINGEARRING"
    };
    private static readonly HashSet<string> Survival = new(StringComparer.Ordinal)
    {
        "TUNGSTENROD", "ORICHALCUM", "FAKEORICHALCUM", "BEATINGREMNANT", "THEBOOT"
    };
    private static readonly HashSet<string> Economy = new(StringComparer.Ordinal) { "MEMBERSHIPCARD", "THECOURIER" };

    // Game IDs are snake case (PAELS_FLESH) while class names are Pascal case (PaelsFlesh);
    // comparing letters and digits only accepts both spellings.
    private static string Compact(string id) => new string(id.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
    public static bool GivesEnergy(string id) => MaxEnergy.Contains(Compact(id));
    public static bool HelpsSurvival(string id) => Survival.Contains(Compact(id));
    public static bool HelpsEconomy(string id) => Economy.Contains(Compact(id));
}

internal static class ScoreExplanation
{
    public static string Formula(OfferScore score)
    {
        var baseline = (int)Math.Round(score.Community, MidpointRounding.AwayFromZero);
        var target = (int)Math.Round(score.Final, MidpointRounding.AwayFromZero);
        var parts = (score.Adjustments ?? []).Where(x => Math.Abs(x.Value) >= .05m)
            .Select(x => (Value: (int)Math.Round(x.Value, MidpointRounding.AwayFromZero), x.Label))
            .Where(x => x.Value != 0).ToList();
        var missing = target - baseline - parts.Sum(x => x.Value);
        if (missing != 0 && parts.Count > 0)
        {
            var index = Enumerable.Range(0, parts.Count).OrderByDescending(i => Math.Abs(parts[i].Value)).First();
            parts[index] = (parts[index].Value + missing, parts[index].Label);
        }
        else if (missing != 0) parts.Add((missing, "поправка текущей колоды"));

        var changes = string.Join(" ", parts.Where(x => x.Value != 0)
            .Select(x => $"{x.Value:+#;-#;0} ({x.Label})"));
        return changes.Length == 0 ? $"База {baseline} = {target}/100" : $"База {baseline} {changes} = {target}/100";
    }
}

// The official Bayesian Codex Score is the quality baseline. Elo is a deliberately bounded
// preference signal; deck needs, archetype, duplicates and floor remain a separate local layer.
internal static class DeckBuildScoring
{
    public static BuildAssessment Evaluate(RunContext context, IEnumerable<string> offerIds,
        IReadOnlyDictionary<string, CardDefinition> cards, IReadOnlyDictionary<string, EntityMetric> metrics,
        IReadOnlyDictionary<string, CoachOffer>? coach = null)
    {
        CardDefinition? Find(string id) => cards.TryGetValue(SpireCodexMetricsClient.NormalizeId(id), out var card) ? card : null;
        var deck = context.Cards.Select(Find).Where(x => x is not null).Cast<CardDefinition>().ToArray();
        var averageCost = deck.Where(x => x.Cost >= 0).Select(x => x.Cost).DefaultIfEmpty(0).Average();
        var defense = deck.Count(x => x.Block > 0);
        var attacks = deck.Count(x => x.Type.Contains("attack", StringComparison.OrdinalIgnoreCase));
        var powers = deck.Count(x => x.Type.Contains("power", StringComparison.OrdinalIgnoreCase));
        var draw = deck.Sum(x => Math.Max(0, x.Draw));
        var energy = deck.Sum(x => Math.Max(0, x.Energy));
        var detectedArchetypes = DetectArchetypes(context.Character, deck, context.Floor);
        var archetypes = ArchetypePreferences.Apply(context.Character, detectedArchetypes);
        var threat = EncounterThreatModel.Assess(context);
        // Starter Strike/Defend copies are counted in the summary but do not satisfy a need:
        // five Strikes are not "enough damage" for Act 1 elites and four Defends are not a
        // defensive plan. Only reward-quality cards count as coverage.
        var core = deck.Where(x => !CardTaxonomy.IsStarterStrikeOrDefend(x)).ToArray();
        var coreDefense = core.Count(x => x.Block > 0);
        var coreAttacks = core.Count(x => x.Damage > 0 && x.Type.Contains("attack", StringComparison.OrdinalIgnoreCase));
        var defenseTarget = context.Act switch { <= 1 => context.Floor >= 8 ? 1 : 0, 2 => 2, _ => 3 };
        var needs = new List<string>();
        if (context.Hp > 0 && context.Hp * 3 < context.MaxHp || coreDefense < defenseTarget) needs.Add("защита");
        if (context.Act == 1 && coreAttacks < 2) needs.Add("быстрый урон");
        if (context.Act >= 2 && powers < 2 && !archetypes.Any(x => x.Contains("Темп"))) needs.Add("масштабирование");
        if (deck.Length >= 14 && draw < 2) needs.Add("добор");
        if (averageCost >= 1.7 && energy == 0 && !context.Relics.Any(IsEnergyRelic)) needs.Add("энергия");
        if (needs.Count == 0) needs.Add("синергия/качество");

        var offered = offerIds.Distinct().ToArray();
        var choiceSignals = offered.Select(id =>
        {
            var key = SpireCodexMetricsClient.NormalizeId(id);
            var copies = deck.Count(x => SpireCodexMetricsClient.NormalizeId(x.Id) == key);
            return RunHistoryInsights.ChoiceSignal(context.Character, id, context.Act, deck.Length, copies);
        }).ToArray();
        var choiceAdjustments = CenteredChoiceAdjustments(choiceSignals);
        var offers = offered.Select((id, index) => Score(id, Find(id), context, deck, metrics, coach, needs, archetypes, threat, averageCost, choiceAdjustments[index])).OrderByDescending(x => x.Final).ToArray();
        var best = offers.FirstOrDefault()?.Final ?? 0;
        var missesCurrentNeed = !needs.SequenceEqual(["синергия/качество"]) && offers.All(x => x.Context <= 0);
        var heuristicSkip = Math.Clamp(20
            + Math.Clamp((deck.Length - 12) * 3, 0, 30)
            + (needs.SequenceEqual(["синергия/качество"]) ? 15 : 0)
            + (missesCurrentNeed ? 25 : 0)
            + (best < 55 ? 20 : best < 65 ? 10 : 0)
            - (best >= 80 ? 25 : best >= 70 ? 12 : 0), 0, 100);
        // The corpus model estimates whether players took no card in a comparable state.
        // Its deployment threshold was selected on validation only; the old rule remains a
        // deterministic fallback for installations with missing or incompatible model data.
        var learnedSkip = RunHistoryInsights.SkipProbability(context, offered, choiceSignals);
        var skip = learnedSkip is { } probability
            ? Math.Clamp((int)Math.Round(probability * 100m, MidpointRounding.AwayFromZero), 0, 100)
            : heuristicSkip;
        var selected = ArchetypePreferences.Get(context.Character);
        var archetypeText = archetypes.Count == 0 ? "архетип ещё не определён" : string.Join(" + ", archetypes) + (selected == "Авто" ? " (авто)" : " (выбран вручную)");
        var segmentRisk = SegmentRiskModel.Assess(context);
        var summary = $"Колода: {deck.Length} карт · ср. стоимость {averageCost:0.0} · атаки {attacks} · защита {defense} · добор {draw} · энергия {energy}.\n" +
                      $"Направление: {archetypeText}. Сейчас важнее: {string.Join(", ", needs)}.\n" + EncounterThreatModel.Summary(threat);
        if (segmentRisk.IsAvailable) summary += "\n" + SegmentRiskModel.Summary(segmentRisk);
        return new BuildAssessment(summary, needs, archetypes, (decimal)averageCost, skip, offers);
    }

    private static OfferScore Score(string id, CardDefinition? card, RunContext context, IReadOnlyList<CardDefinition> deck,
        IReadOnlyDictionary<string, EntityMetric> metrics, IReadOnlyDictionary<string, CoachOffer>? coach,
        IReadOnlyList<string> needs, IReadOnlyList<string> archetypes, ThreatAssessment threat, double averageCost, decimal learnedChoiceAdjustment)
    {
        var key = SpireCodexMetricsClient.NormalizeId(id);
        var name = card?.Name ?? (metrics.TryGetValue(key, out var itemName) ? itemName.Name : id);
        var hasMetric = metrics.TryGetValue(key, out var metric);
        var confidence = hasMetric ? Math.Clamp((decimal)Math.Sqrt(Math.Clamp(metric!.Samples, 0, 1000) / 1000d), 0m, 1m) : 0m;
        var fallback = hasMetric ? Math.Clamp(50m + (metric!.WinRate - .5m) * 100m, 0m, 100m) : 50m;
        // Codex Score already contains Bayesian shrinkage. Only the legacy fallback needs local
        // shrinkage; applying it twice would incorrectly flatten the official S-D scale.
        var community = metric?.CodexScore is { }
            ? CalibratedCommunity(metric, metrics)
            : 50m + (fallback - 50m) * confidence;
        var contextScore = 0m; var notes = new List<string>(); var adjustments = new List<ScoreAdjustment>();
        void Adjust(decimal value, string label, string? detail = null)
        {
            contextScore += value;
            adjustments.Add(new ScoreAdjustment(value, label));
            notes.Add($"{value:+#.#;-#.#;0} {detail ?? label}");
        }
        if (card is null) { notes.Add("модель карты не найдена"); return new OfferScore(id, name, community, community, 0, "неизвестно", string.Join(", ", notes), confidence); }
        var role = Role(card);
        if (needs.Contains("защита") && card.Block > 0) Adjust(25, "нужна защита", $"закрывает дефицит защиты ({card.Block} блока)");
        if (needs.Contains("быстрый урон") && card.Damage > 0 && card.Cost <= 2) Adjust(15, "быстрый урон");
        var selectedArchetype = ArchetypePreferences.Get(context.Character);
        var manualArchetype = selectedArchetype != "Авто";
        var cardText = (card.Id + " " + card.Description).ToUpperInvariant();
        var matchesSelected = manualArchetype && MatchesArchetype(cardText, card, selectedArchetype);
        var competing = manualArchetype && !matchesSelected
            ? CompetingArchetypes(context.Character, selectedArchetype).Where(x => MatchesArchetype(cardText, card, x) && !CompatibleArchetypes(selectedArchetype, x)).ToArray()
            : [];
        var scalingCandidate = card.Type.Contains("power", StringComparison.OrdinalIgnoreCase) || Has(card, "strength", "dexterity", "focus", "ритуал", "сила", "ловкость");
        if (needs.Contains("масштабирование") && scalingCandidate && competing.Length == 0) Adjust(18, "усиление к поздней игре");
        if (needs.Contains("добор") && card.Draw > 0) Adjust(16, "нужен добор", $"добор ({card.Draw})");
        if (needs.Contains("энергия") && card.Energy > 0) Adjust(28, "даёт энергию", $"энергия ({card.Energy})");
        if (needs.Contains("энергия") && card.Cost >= 2 && card.Energy == 0) Adjust(-16, "дорогая при нехватке энергии");
        if (card.Cost >= 3 && averageCost >= 1.7 && card.Energy == 0) Adjust(-8, "сложность разыгрывания");
        var threatBonus = EncounterThreatModel.Adjustment(card, threat);
        if (threatBonus != 0) Adjust(threatBonus, "подготовка к боссу");
        var archetypeBonus = ArchetypeBonus(card, archetypes, deck);
        if (archetypeBonus != 0) Adjust(archetypeBonus, "синергия архетипа");
        var conflictPenalty = competing.Length == 0 ? 0m : context.Act switch { 1 => -22m, 2 => -30m, _ => -38m };
        if (conflictPenalty != 0)
        {
            Adjust(conflictPenalty, "не подходит выбранному архетипу", $"конфликт с ручным архетипом {selectedArchetype} ({string.Join("/", competing)})");
        }
        // Pick Coach reports commitment delta/support, not a replacement 0-100 card score.
        // Treat it as bounded contextual evidence on top of community quality.
        if (coach is not null && coach.TryGetValue(key, out var suggested))
        {
            // Similar winning decks must not silently override an explicit player direction.
            var coachBonus = competing.Length > 0 ? Math.Min(0m, suggested.Score) : Math.Clamp(suggested.Score, -12m, 12m);
            Adjust(coachBonus, "похожие победные колоды");
        }
        var personal = PersonalRunDiary.PreferenceBonus(context.Character, id);
        if (personal != 0) Adjust(personal, "ваша история выборов");
        var copies = deck.Count(x => SpireCodexMetricsClient.NormalizeId(x.Id) == key);
        var fillsNeed = needs.Any(need => Addresses(card, need));
        var effectiveFillsNeed = fillsNeed && competing.Length == 0;
        var repeatable = CardSemanticTags.IsNaturallyRepeatable(card);
        var duplicateAdjustment = RunHistoryInsights.DuplicateAdjustment(context.Character, id, copies + 1, repeatable, effectiveFillsNeed, context.Act);
        if (duplicateAdjustment != 0)
        {
            var evidence = RunHistoryInsights.ForCard(context.Character, id);
            var source = RunHistoryInsights.HasReliableDuplicateEvidence(evidence) ? "с учётом истории забегов" : "по структуре колоды";
            Adjust(duplicateAdjustment, $"копия {copies + 1}", $"копия {copies + 1} ({source})");
        }
        var sizeAdjustment = RunHistoryInsights.DeckSizeAdjustment(context, deck.Count + 1);
        // Even a synergistic card consumes a draw in an oversized late-game deck. Direct need
        // and archetype fit reduce the slot tax, but no longer erase it completely.
        if (effectiveFillsNeed) sizeAdjustment *= .65m;
        if (sizeAdjustment != 0)
        {
            Adjust(sizeAdjustment, "размер колоды", $"размер колоды (ориентир {RunHistoryInsights.ExpectedDeckSize(context.Character, context.Floor)})");
        }
        var stageAdjustment = StageAdjustment(card, context, effectiveFillsNeed, archetypeBonus > 0, competing.Length > 0, deck.Count);
        if (stageAdjustment != 0)
        {
            Adjust(stageAdjustment, $"ценность в акте {context.Act}");
        }
        var structuralPenalties = new[] { duplicateAdjustment, sizeAdjustment, stageAdjustment }.Where(x => x < 0m).ToArray();
        if (structuralPenalties.Length >= 2)
        {
            // Multiple independent forms of bloat compound each other: an extra late-game copy
            // in an oversized deck is worse than either warning in isolation.
            var multiplier = context.Act switch { 1 => .25m, 2 => .40m, _ => .60m };
            var compoundPenalty = -Math.Min(18m, Math.Abs(structuralPenalties.Sum()) * multiplier);
            Adjust(compoundPenalty, "несколько структурных минусов", "дубликат, размер колоды и поздняя ценность усиливают друг друга");
        }
        if (notes.Count == 0) notes.Add("нейтральна к текущим потребностям");
        // Positive evidence has diminishing returns near the top of the scale. This prevents
        // a globally strong card plus two correlated bonuses from turning into an automatic 100.
        // Penalties remain linear because a conflict or an unnecessary slot is real downside.
        var contextual = contextScore > 0m
            ? contextScore * Math.Max(0m, 100m - community) / 70m
            : contextScore;
        var finalBeforeLimits = Math.Clamp(community + contextual, 0m, 100m);
        var final = finalBeforeLimits;
        var appliedFactor = contextScore > 0m && contextScore != 0m ? contextual / contextScore : 1m;
        var appliedAdjustments = adjustments.Select(x => new ScoreAdjustment(x.Value * appliedFactor, x.Label)).ToList();
        var clampDifference = finalBeforeLimits - (community + contextual);
        if (Math.Abs(clampDifference) >= .05m) appliedAdjustments.Add(new ScoreAdjustment(clampDifference, "граница шкалы"));
        if (learnedChoiceAdjustment != 0m)
        {
            var beforeLearned = final;
            final = Math.Clamp(final + learnedChoiceAdjustment, 0m, 100m);
            var applied = final - beforeLearned;
            if (Math.Abs(applied) >= .05m)
            {
                appliedAdjustments.Add(new ScoreAdjustment(applied, "похожие ситуации"));
                notes.Add($"{applied:+#.#;-#.#;0} выборы в похожих колодах");
            }
        }
        if (competing.Length > 0)
        {
            var ceiling = context.Act switch { 1 => 67m, 2 => 59m, _ => 49m } + (effectiveFillsNeed ? 8m : 0m);
            if (final > ceiling)
            {
                appliedAdjustments.Add(new ScoreAdjustment(ceiling - final, "ограничение другого архетипа"));
                final = ceiling;
                notes.Add($"предел {ceiling:F0}: карта другого архетипа");
            }
        }
        var impact = Impact(card, deck, needs, averageCost);
        var tagText = CardSemanticTags.Describe(card, context, final, contextScore, deck, archetypes);
        return new OfferScore(id, name, final, community, contextScore, role, string.Join("; ", notes), confidence, personal, impact, tagText, appliedAdjustments);
    }

    internal static decimal[] CenteredChoiceAdjustments(IReadOnlyList<decimal?> signals)
    {
        if (signals.Count == 0 || signals.All(x => x is null)) return new decimal[signals.Count];
        const decimal neutral = .24m;
        var values = signals.Select(x => x ?? neutral).ToArray();
        var average = values.Average();
        return values.Select(x => Math.Clamp((x - average) * 100m, -10m, 10m)).ToArray();
    }

    private static decimal EloAdjustment(EntityMetric metric)
    {
        if (metric.Elo is not { } elo || metric.Offered < 20) return 0m;
        // Elo is useful pairwise preference evidence, but not an outcome probability. Keep it
        // small enough that it can refine, never overwrite, the Bayesian performance score.
        return Math.Clamp((elo - 1850m) / 50m, -6m, 6m);
    }

    private static decimal CalibratedCommunity(EntityMetric metric, IReadOnlyDictionary<string, EntityMetric> metrics)
    {
        var official = metric.CodexScore ?? 50m;
        var population = metrics.Values.Where(x => x.CodexScore is not null).Select(x => x.CodexScore!.Value).ToArray();
        if (population.Length < 20) return Math.Clamp(50m + (official - 50m) * .75m + EloAdjustment(metric), 0m, 100m);
        var lower = population.Count(x => x < official);
        var equal = population.Count(x => x == official);
        var percentile = (lower + equal * .5m) / population.Length;
        // Percentiles restore separation when the upstream Bayesian score has many values tied
        // at 100. Raw score still contributes 20%; global Elo can break close preference ties.
        var percentileScore = 35m + percentile * 55m;
        return Math.Clamp(official * .20m + percentileScore * .80m + EloAdjustment(metric), 0m, 100m);
    }

    private static decimal StageAdjustment(CardDefinition card, RunContext context, bool fillsNeed, bool archetypeFit, bool conflict, int deckSize)
    {
        if (context.Act <= 1) return 0m;
        var text = (card.Id + " " + card.Description).ToUpperInvariant();
        var scaling = archetypeFit || Has(text, "EACH TURN", "FOR EACH", "WHENEVER", "PERMANENT", "КАЖДЫЙ ХОД", "ЗА КАЖД", "НАВСЕГДА", "FOCUS", "ФОКУС");
        if (scaling && !conflict) return context.Act == 3 ? 2m : 1m;
        var marginal = !fillsNeed && (card.Damage > 0 || card.Block > 0 || card.Draw <= 0);
        if (!marginal) return 0m;
        var basePenalty = context.Act == 2 ? -3m : -8m;
        if (deckSize >= 30) basePenalty -= context.Act == 3 ? 5m : 2m;
        return basePenalty;
    }

    private static bool Addresses(CardDefinition card, string need) => need switch
    {
        "защита" => card.Block > 0,
        "быстрый урон" => card.Damage > 0 && card.Cost <= 2,
        "масштабирование" => card.Type.Contains("power", StringComparison.OrdinalIgnoreCase) || Has(card, "strength", "dexterity", "focus", "сила", "ловкость"),
        "добор" => card.Draw > 0,
        "энергия" => card.Energy > 0,
        _ => false
    };

    private static string Impact(CardDefinition card, IReadOnlyList<CardDefinition> deck, IReadOnlyList<string> needs, double averageCost)
    {
        var newAverage = deck.Count == 0 ? card.Cost : (averageCost * deck.Count + Math.Max(0, card.Cost)) / (deck.Count + 1);
        var changes = new List<string> { $"ср. стоимость {averageCost:0.0}→{newAverage:0.0}" };
        if (card.Damage > 0) changes.Add($"урон +{card.Damage}");
        if (card.Block > 0) changes.Add($"блок +{card.Block}");
        if (card.Draw > 0) changes.Add($"добор +{card.Draw}");
        if (card.Energy > 0) changes.Add($"энергия +{card.Energy}");
        var covered = needs.Where(need => Addresses(card, need)).ToArray();
        if (covered.Length > 0) changes.Add("закрывает: " + string.Join(", ", covered));
        return "После выбора: " + string.Join(" · ", changes) + ".";
    }

    private static IReadOnlyList<string> DetectArchetypes(string character, IEnumerable<CardDefinition> deck, int floor)
    {
        var candidates = ArchetypePreferences.Options(character).Where(x => x != "Авто").ToArray();
        var scores = candidates.ToDictionary(x => x, _ => 0, StringComparer.OrdinalIgnoreCase);
        foreach (var card in deck)
        {
            var text = (card.Id + " " + card.Description).ToUpperInvariant();
            foreach (var candidate in candidates)
            {
                if (!MatchesArchetype(text, card, candidate)) continue;
                var value = 2;
                if (card.Type.Contains("power", StringComparison.OrdinalIgnoreCase)) value += 2;
                value += CardTaxonomy.RarityName(card.Rarity) switch { "Редкая" => 2, "Необычная" => 1, _ => 0 };
                if (Has(text, "FOR EACH", "WHENEVER", "DOUBLE", "INCREASE", "ЗА КАЖД", "КОГДА", "УДВО", "УВЕЛИЧ")) value += 2;
                scores[candidate] += value;
            }
        }
        var best = scores.Values.DefaultIfEmpty(0).Max();
        var threshold = floor <= 15 ? 3 : 4;
        if (best < threshold) return [];
        // Commit early on one convincing engine. A second direction is shown only when its
        // evidence is nearly tied and independently strong, avoiding noisy starter-card labels.
        return scores.Where(x => x.Value == best || x.Value >= 5 && x.Value >= best - 1)
            .OrderByDescending(x => x.Value).Select(x => x.Key).Take(2).ToArray();
    }

    private static IEnumerable<string> CompetingArchetypes(string character, string selected) =>
        ArchetypePreferences.Options(character).Where(x => x != "Авто" && x != selected);

    private static bool CompatibleArchetypes(string selected, string candidate) =>
        (selected is "Мороз" or "Молнии" && candidate == "Фокус") ||
        (selected == "Фокус" && candidate is "Мороз" or "Молнии") ||
        ((selected is "Бесцветные карты" or "Звёзды") &&
         (candidate is "Бесцветные карты" or "Звёзды"));

    private static decimal ArchetypeBonus(CardDefinition card, IEnumerable<string> archetypes, IReadOnlyList<CardDefinition> deck)
    {
        var text = (card.Id + " " + card.Description).ToUpperInvariant();
        foreach (var archetype in archetypes)
            if (MatchesArchetype(text, card, archetype))
                return archetype == "Бесцветные карты" ? RegentColorlessArchetype.Bonus(card, deck) : 16m;
        return 0;
    }

    private static bool MatchesArchetype(string text, CardDefinition card, string archetype) => archetype switch
    {
        "Сила" => Has(text, "STRENGTH", "СИЛА"),
        "Истощение" => Has(text, "EXHAUST", "ИСТОЩ"),
        "Блок" => card.Block > 0 || Has(text, "BLOCK", "БЛОК"),
        "Саморанение" => Has(text, "BLOOD", "HP_LOSS", "РАН", "ПОТЕРЯЙТЕ", "ПОЛУЧАЕТЕ.*УРОН"),
        "Уязвимость" => Has(text, "VULNERABLE", "УЯЗВИМОСТ", "BASH", "UPPER_CUT", "TRIP"),
        "Яд" => Has(text, "POISON", "ЯД"),
        "Заточки" => Has(text, "SHIV", "ЗАТОЧК", "BLADE_DANCE", "ACCURACY"),
        "Сброс" => Has(text, "DISCARD", "СБРОС", "СБРАСЫВ", "DAGGER_THROW", "REFLEX", "TACTICIAN"),
        "Молнии" => Has(text, "LIGHTNING", "МОЛНИ"),
        "Мороз" => Has(text, "FROST", "МОРОЗ"),
        "Фокус" => Has(text, "FOCUS", "ФОКУС"),
        "Нулевая стоимость" => card.Cost == 0 || Has(text, "ZERO", "0 COST", "БЕСПЛАТ", "ALL_FOR_ONE", "SCRAPE"),
        "Звёзды" => Has(text, "STAR", "ЗВЁЗД"),
        "Кузня" => Has(text, "FORGE", "КУЗН", "SMITH"),
        "Бесцветные карты" => RegentColorlessArchetype.IsMatch(card),
        "Души" => Has(text, "SOUL", "ДУШ"),
        "Гибель" => Has(text, "DOOM", "ГИБЕЛ", "ПОГИБ"),
        "Призыв" => Has(text, "SUMMON", "ПРИЗЫВ", "ПРИЗЫВА"),
        _ => false
    };

    private static string Role(CardDefinition card)
    {
        var roles = new List<string>();
        if (card.Type.Contains("attack", StringComparison.OrdinalIgnoreCase)) roles.Add("атака");
        if (card.Block > 0) roles.Add("защита");
        if (card.Draw > 0) roles.Add("добор");
        if (card.Energy > 0) roles.Add("энергия");
        if (card.Type.Contains("power", StringComparison.OrdinalIgnoreCase)) roles.Add("талант");
        return roles.Count == 0 ? "навык" : string.Join("+ ", roles);
    }
    private static bool IsEnergyRelic(string id) => RelicKnowledge.GivesEnergy(id);
    private static bool Has(CardDefinition card, params string[] tokens) => tokens.Any(x => (card.Id + " " + card.Description).Contains(x, StringComparison.OrdinalIgnoreCase));
    private static bool Has(string text, params string[] tokens) => tokens.Any(x => text.Contains(x, StringComparison.OrdinalIgnoreCase));
}

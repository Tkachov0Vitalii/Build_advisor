using BuildAdvisor.Data;

namespace BuildAdvisor.Integration;

// Runs at startup without network access. These scenarios guard the recommendation model,
// every playable character, every manual archetype, and API taxonomy used by search filters.
internal static class BuildAdvisorRegression
{
    public static string LastError { get; private set; } = "";

    public static bool Run()
    {
        try
        {
            RarityFormatsAreStable();
            LocalizationIsBilingual();
            EnglishAdviceHasNoRussianFragments();
            EventAdviceIsBilingual();
            EnglishTagReferenceIsComplete();
            SemanticTagContractIsComplete();
            ExplainabilityAndConfidenceAreStable();
            UserFacingFormulaIsSimpleAndExact();
            OfficialMetricsAreParsedAndUsed();
            ShopBasketStaysWithinBudget();
            CoreNeedsWorkForEveryCharacter();
            DefensiveCardWinsWhenDefenseIsNeeded();
            ExpensiveDeckValuesEnergy();
            PickCoachDoesNotReplaceCommunityScore();
            DeckSizeAndDuplicatesAreAccountedFor();
            CompoundBloatPenaltyGrowsByAct();
            LearnedChoiceAdjustmentIsBounded();
            LearnedSkipModelIsAvailableAndDirectional();
            AutoPickIsConservative();
            BossThreatRefinesInsteadOfDominating();
            SegmentRiskIsCalibratedAndDirectional();
            ActionPlanExplainsAlternatives();
            UpgradePlanRejectsAlreadyUpgradedCards();
            EventCardActionsUseTheCorrectDirection();
            ShopBadgesStayAboveTheirSlots();
            ArchetypeSelectionResetsBetweenRuns();
            ManualArchetypeOverridesGenericPowerAndLateBloat();
            StrongEarlyCardDetectsArchetype();
            EveryManualArchetypeMatchesItsSignal();
            RegentColorlessEngineNeedsBothHalves();
            LastError = "";
            return true;
        }
        catch (Exception error)
        {
            LastError = error.Message;
            return false;
        }
    }

    private static void EventAdviceIsBilingual()
    {
        var russian = "BUILD ADVISOR · выбор в событии\nЦель: удаление. Выберите от 1 до 2 карт.\n" +
                      "Высокая оценка означает лучший выбор именно для этой цели.\n" +
                      "Strike: 82/100 — чем выше оценка, тем выгоднее удалить карту\n" +
                      "Автовыбор на экранах событий отключён: мод не подтверждает выбор и не мешает выбрать несколько карт.";
        var english = ModLocalization.Translate(russian, AdvisorLanguage.English);
        if (System.Text.RegularExpressions.Regex.IsMatch(english, "[А-Яа-яЁё]"))
            throw new InvalidOperationException("English event advice contains untranslated text: " + english);
    }

    private static void EventCardActionsUseTheCorrectDirection()
    {
        var weak = Card("STRIKE", "Attack", damage: 6);
        var strong = Card("RARE_POWER", "Power", rarity: "rare");
        var catalog = new Dictionary<string, CardDefinition>(StringComparer.OrdinalIgnoreCase)
            { ["STRIKE"] = weak, ["RARE_POWER"] = strong };
        var metrics = new Dictionary<string, EntityMetric>(StringComparer.OrdinalIgnoreCase)
        {
            ["STRIKE"] = new EntityMetric("STRIKE", "Strike", .40m, .30m, 1000, 30m),
            ["RARE_POWER"] = new EntityMetric("RARE_POWER", "Rare Power", .60m, .70m, 1000, 80m)
        };
        var context = new RunContext("ironclad", ["STRIKE", "RARE_POWER"], [], 0, 2, 20, 70, 80, 99, null, 1m, 1, 0, 1, []);
        var remove = EventCardAdvisor.Evaluate(context, ["STRIKE", "RARE_POWER"], catalog, metrics, EventCardPurpose.Remove);
        var upgrade = EventCardAdvisor.Evaluate(context, ["STRIKE", "RARE_POWER"], catalog, metrics, EventCardPurpose.Upgrade);
        if (remove.Offers[0].Id != "STRIKE") throw new InvalidOperationException("Event removal advice did not prefer the weaker starter card.");
        if (upgrade.Offers[0].Id != "RARE_POWER") throw new InvalidOperationException("Event upgrade advice did not prefer the stronger scaling card.");
    }

    private static void LocalizationIsBilingual()
    {
        var english = ModLocalization.Translate("Оценка вариантов · Архетип: Мороз · Обновить", AdvisorLanguage.English);
        if (!english.Contains("Card options", StringComparison.Ordinal) || !english.Contains("Frost", StringComparison.Ordinal) || !english.Contains("Refresh", StringComparison.Ordinal))
            throw new InvalidOperationException("English localization contract failed: " + english);
        if (ModLocalization.Translate("Совет", AdvisorLanguage.Russian) != "Совет")
            throw new InvalidOperationException("Russian localization contract failed.");
    }

    private static void EnglishAdviceHasNoRussianFragments()
    {
        var card = new CardDefinition("EN_CARD", "English Card", "defect", "Skill", 1, 0, 8, 1, 0, "uncommon", "Gain Block. Draw 1.", new HashSet<string>(), null);
        var catalog = new Dictionary<string, CardDefinition>(StringComparer.OrdinalIgnoreCase) { ["EN_CARD"] = card };
        var context = new RunContext("defect", Enumerable.Repeat("EN_CARD", 18).ToArray(), [], 0, 3, 38, 30, 70, 150, null, 1.2m, 3, 15, 0, [])
        { Route = new RouteSnapshot("monster", [new RouteOption(["elite", "rest_site", "boss"])]) };
        var assessment = DeckBuildScoring.Evaluate(context, ["EN_CARD"], catalog, Metrics("EN_CARD"));
        var score = assessment.Offers[0];
        var raw = assessment.Summary + "\n" + ScoreExplanation.Formula(score) + "\n" + score.TagText + "\n" + score.Impact + "\n" + LocalDeckAdvisor.Snapshot(context) + "\n" +
                  DeckActionPlanner.Create(context, assessment, catalog) + "\n" +
                  ShopPlanBuilder.Create(context, [new ShopItem("EN_CARD", 50)], [], catalog, Metrics("EN_CARD"), new Dictionary<string, EntityMetric>());
        var english = ModLocalization.Translate(raw, AdvisorLanguage.English);
        if (System.Text.RegularExpressions.Regex.IsMatch(english, "[А-Яа-яЁё]"))
            throw new InvalidOperationException("English advice contains untranslated text: " + english);
    }

    private static void EnglishTagReferenceIsComplete()
    {
        foreach (var tag in CardSemanticTags.Reference)
        {
            var english = ModLocalization.Translate(tag.Category + " " + tag.Label + " " + tag.Description, AdvisorLanguage.English);
            if (System.Text.RegularExpressions.Regex.IsMatch(english, "[А-Яа-яЁё]"))
                throw new InvalidOperationException("English tag reference contains untranslated text: " + english);
        }
    }

    private static void UserFacingFormulaIsSimpleAndExact()
    {
        var score = new OfferScore("FORMULA", "Формула", 85m, 78m, 7m, "навык", "",
            Adjustments: [new ScoreAdjustment(12m, "синергия"), new ScoreAdjustment(-5m, "сложность разыгрывания")]);
        var text = ScoreExplanation.Formula(score);
        if (text != "База 78 +12 (синергия) -5 (сложность разыгрывания) = 85/100" ||
            text.Contains("Elo", StringComparison.Ordinal) || text.Contains("Codex", StringComparison.Ordinal) || text.Contains("n=", StringComparison.Ordinal))
            throw new InvalidOperationException("User-facing score formula is not simple or exact: " + text);
    }

    private static void ManualArchetypeOverridesGenericPowerAndLateBloat()
    {
        var frost = new CardDefinition("FROST_ENGINE", "Морозный двигатель", "defect", "Skill", 1, 0, 8, 0, 0, "uncommon", "Channel Frost.", new HashSet<string>(), null);
        var storm = new CardDefinition("STORM", "Гром", "defect", "Power", 1, 0, 0, 0, 0, "rare", "Whenever you play a Power, channel Lightning.", new HashSet<string>(), null);
        var filler = Card("FILLER", "Skill");
        var catalog = new Dictionary<string, CardDefinition>(StringComparer.OrdinalIgnoreCase) { ["FROST_ENGINE"] = frost, ["STORM"] = storm, ["FILLER"] = filler };
        var deck = new[] { "FROST_ENGINE" }.Concat(Enumerable.Repeat("FILLER", 37)).ToArray();
        var context = new RunContext("defect", deck, [], 0, 3, 42, 70, 75, 200, null, 1m, 0, 38, 0, []);
        var metrics = new Dictionary<string, EntityMetric>(StringComparer.OrdinalIgnoreCase) { ["STORM"] = new EntityMetric("STORM", "Гром", .4m, .56m, 30000, 94m, "S", 2000m, 50000, 20000) };
        var previous = ArchetypePreferences.Get("defect"); ArchetypePreferences.Set("defect", "Мороз");
        try
        {
            var score = DeckBuildScoring.Evaluate(context, ["STORM"], catalog, metrics, new Dictionary<string, CoachOffer> { ["STORM"] = new("STORM", "Гром", 12m, 5m) }).Offers[0];
            if (score.Final >= 50m || !score.Reason.Contains("конфликт с ручным архетипом", StringComparison.Ordinal) || !score.Reason.Contains("размер колоды", StringComparison.Ordinal))
                throw new InvalidOperationException($"Manual archetype conflict or late-deck discipline failed: {score.Final:F1}; {score.Reason}");
        }
        finally { ArchetypePreferences.Set("defect", previous); }
    }

    private static void StrongEarlyCardDetectsArchetype()
    {
        var engine = new CardDefinition("EARLY_FROST_ENGINE", "Ледяное ядро", "defect", "Power", 1, 0, 0, 0, 0, "rare", "Channel Frost whenever you play a Skill.", new HashSet<string>(), null);
        var result = typeof(DeckBuildScoring).GetMethod("DetectArchetypes", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, new object[] { "defect", new[] { engine }, 5 }) as IReadOnlyList<string>;
        if (result is null || !result.Contains("Мороз")) throw new InvalidOperationException("Strong early archetype signal was ignored.");
    }

    private static void OfficialMetricsAreParsedAndUsed()
    {
        using var document = System.Text.Json.JsonDocument.Parse("""{"rows":[{"id":"CODEX_CARD","upgraded":false,"score":82,"tier":"A","elo":2000.5,"win_rate":43.0,"pick_rate":45.3,"picks":30877,"offered":71105,"picked":32219,"pick_rate_by_act":[41.6,48.8,48.3]},{"id":"CODEX_CARD","upgraded":true,"score":100,"tier":"S","elo":2500,"win_rate":70,"picks":10000}]}""");
        var parsed = SpireCodexMetricsClient.ParseMetrics(document.RootElement, "cards");
        var metric = parsed["CODEX_CARD"];
        if (metric.CodexScore != 82m || metric.Tier != "A" || metric.Elo != 2000.5m || metric.Offered != 71105 || metric.PickRateByAct?[1] != .488m)
            throw new InvalidOperationException("Official Spire Codex fields were not parsed, or upgraded metrics overwrote the base card.");

        var card = Card("CODEX_CARD", "Attack", damage: 7);
        var catalog = new Dictionary<string, CardDefinition>(StringComparer.OrdinalIgnoreCase) { ["CODEX_CARD"] = card };
        var context = new RunContext("test", [], [], 0, 1, 1, 80, 80, 99, null, 1m, 0, 0, 0, []);
        var score = DeckBuildScoring.Evaluate(context, ["CODEX_CARD"], catalog, parsed).Offers[0];
        if (score.Community < 76m || score.Community > 80m || score.Reason.Contains("Elo", StringComparison.Ordinal) || score.Reason.Contains("Codex", StringComparison.Ordinal))
            throw new InvalidOperationException("Official Codex Score/Elo blend was not used.");
    }

    private static void SemanticTagContractIsComplete()
    {
        var missing = CardSemanticTags.RequiredCompatibilityAxes.Where(key => !CardSemanticTags.Supports(key)).ToArray();
        if (missing.Length > 0) throw new InvalidOperationException("Missing semantic axes: " + string.Join(",", missing));
        var card = new CardDefinition("POISON_CLOUD", "Ядовитое облако", "silent", "Skill", 1, 0, 0, 1, 0, "uncommon", "Накладывает 5 яда на всех врагов.", new HashSet<string>(), null);
        var context = new RunContext("silent", [], [], 0, 1, 1, 70, 70, 99, null, 1m, 0, 0, 0, []);
        var tags = CardSemanticTags.Detect(card, context, 92, 25, [], ["Яд"]);
        foreach (var required in new[] { "POISON", "POISON_PRODUCER", "DRAW", "AOE", "ARCHETYPE_FIT", "TOP_PICK" })
            if (!tags.Contains(required)) throw new InvalidOperationException("Semantic detection failed: " + required);
    }

    private static void ExplainabilityAndConfidenceAreStable()
    {
        var card = Card("DRAW_TEST", "Skill", draw: 2);
        var catalog = new Dictionary<string, CardDefinition>(StringComparer.OrdinalIgnoreCase) { ["DRAW_TEST"] = card, ["ATTACK"] = Card("ATTACK", "Attack", damage: 6), ["DEFEND"] = Card("DEFEND", "Skill", block: 5) };
        var metrics = new Dictionary<string, EntityMetric>(StringComparer.OrdinalIgnoreCase) { ["DRAW_TEST"] = new EntityMetric("DRAW_TEST", "DRAW_TEST", .5m, .99m, 1) };
        var deck = Enumerable.Repeat("ATTACK", 11).Concat(Enumerable.Repeat("DEFEND", 3)).ToArray();
        var context = new RunContext("ironclad", deck, [], 0, 1, 1, 80, 80, 99, null, 1m, 11, 3, 0, []);
        var score = DeckBuildScoring.Evaluate(context, ["DRAW_TEST"], catalog, metrics).Offers[0];
        if (score.Confidence >= .1m || score.Community >= 60m || !score.Impact.Contains("добор +2", StringComparison.Ordinal))
            throw new InvalidOperationException("Confidence shrinkage or counterfactual explanation failed.");
    }

    private static void ShopBasketStaysWithinBudget()
    {
        var catalog = Cards(("SHOP_A", "Покупка А", "Skill", 1, 0, 6, 0, 0), ("SHOP_B", "Покупка Б", "Attack", 1, 8, 0, 0, 0), ("ATTACK", "ATTACK", "Attack", 1, 6, 0, 0, 0), ("DEFEND", "DEFEND", "Skill", 1, 0, 5, 0, 0));
        var metrics = new Dictionary<string, EntityMetric>(StringComparer.OrdinalIgnoreCase)
        {
            ["SHOP_A"] = new EntityMetric("SHOP_A", "Покупка А", .5m, .70m, 1000),
            ["SHOP_B"] = new EntityMetric("SHOP_B", "Покупка Б", .5m, .70m, 1000)
        };
        var deck = Enumerable.Repeat("ATTACK", 4).Concat(Enumerable.Repeat("DEFEND", 3)).ToArray();
        var context = new RunContext("ironclad", deck, [], 0, 1, 1, 80, 80, 100, null, 1m, 4, 3, 0, []);
        var plan = ShopPlanBuilder.Create(context, [new ShopItem("SHOP_A", 50), new ShopItem("SHOP_B", 50)], [], catalog, metrics, new Dictionary<string, EntityMetric>());
        if (!plan.Contains("100/100 золота", StringComparison.Ordinal))
            throw new InvalidOperationException("Shop basket budget optimization failed.");
    }

    private static void RarityFormatsAreStable()
    {
        var cases = new[]
        {
            ("common", "Обычная"), ("CARD_RARITY_COMMON", "Обычная"), ("Обычная", "Обычная"),
            ("uncommon", "Необычная"), ("CARD_RARITY_UNCOMMON", "Необычная"), ("Необычная", "Необычная"),
            ("rare", "Редкая"), ("CARD_RARITY_RARE", "Редкая"), ("Редкая", "Редкая")
        };
        foreach (var (raw, expected) in cases)
        {
            var card = Card("RARITY_TEST", "Skill", rarity: raw);
            if (CardTaxonomy.RarityName(raw) != expected || !CardTaxonomy.MatchesRarity(card, expected))
                throw new InvalidOperationException($"Rarity mapping failed: {raw} -> {expected}.");
        }
    }

    private static void CoreNeedsWorkForEveryCharacter()
    {
        var catalog = new[]
        {
            Card("ATTACK", "Attack", damage: 6), Card("DEFEND", "Skill", block: 5),
            Card("HEAVY_ATTACK", "Attack", cost: 3, damage: 15), Card("HEAVY_BLOCK", "Skill", cost: 2, block: 10),
            Card("BLOCK_PICK", "Skill", block: 12), Card("QUICK_PICK", "Attack", damage: 9),
            Card("SCALE_PICK", "Power"), Card("DRAW_PICK", "Skill", draw: 2),
            Card("ENERGY_PICK", "Skill", energy: 1)
        }.ToDictionary(x => SpireCodexMetricsClient.NormalizeId(x.Id), StringComparer.OrdinalIgnoreCase);
        var metrics = Metrics("BLOCK_PICK", "QUICK_PICK", "SCALE_PICK", "DRAW_PICK", "ENERGY_PICK");

        foreach (var character in new[] { "ironclad", "silent", "defect", "regent", "necrobinder" })
        {
            var previous = ArchetypePreferences.Get(character);
            ArchetypePreferences.Set(character, "Авто");
            try
            {
                AssertNeed(character, "защита", 25, "BLOCK_PICK", ["ATTACK", "ATTACK", "ATTACK", "ATTACK"], 1, 10, 80, catalog, metrics);
                AssertNeed(character, "быстрый урон", 15, "QUICK_PICK", ["DEFEND", "DEFEND", "DEFEND"], 1, 80, 80, catalog, metrics);
                AssertNeed(character, "масштабирование", 18, "SCALE_PICK", ["DEFEND", "DEFEND", "DEFEND", "ATTACK", "ATTACK", "ATTACK", "ATTACK"], 2, 80, 80, catalog, metrics);
                // Draw still receives a clear need bonus, but a 15th card must also pay the
                // character-specific deck-size tax introduced by the stronger bloat model.
                AssertNeed(character, "добор", 10, "DRAW_PICK", Enumerable.Repeat("ATTACK", 11).Concat(Enumerable.Repeat("DEFEND", 3)).ToArray(), 1, 80, 80, catalog, metrics);
                AssertNeed(character, "энергия", 18, "ENERGY_PICK", Enumerable.Repeat("HEAVY_ATTACK", 4).Concat(Enumerable.Repeat("HEAVY_BLOCK", 10)).ToArray(), 1, 80, 80, catalog, metrics);
            }
            finally { ArchetypePreferences.Set(character, previous); }
        }
    }

    private static void AssertNeed(string character, string need, decimal minimumContext, string offer,
        IReadOnlyList<string> deck, int act, int hp, int maxHp,
        IReadOnlyDictionary<string, CardDefinition> catalog, IReadOnlyDictionary<string, EntityMetric> metrics)
    {
        var context = new RunContext(character, deck, [], 0, act, 1, hp, maxHp, 99, null, 1m, 0, 0, 0, []);
        var result = DeckBuildScoring.Evaluate(context, [offer], catalog, metrics);
        if (!result.Needs.Contains(need) || result.Offers[0].Context < minimumContext)
            throw new InvalidOperationException($"{character}: scenario '{need}' failed.");
    }

    private static void DefensiveCardWinsWhenDefenseIsNeeded()
    {
        var catalog = Cards(("STRIKE", "Удар", "Attack", 1, 6, 0, 0, 0), ("DEFEND", "Оборона", "Skill", 1, 0, 5, 0, 0), ("BLOOD_WALL", "Стена крови", "Skill", 2, 0, 14, 0, 0), ("HEAVY_BLOW", "Тяжёлый удар", "Attack", 2, 14, 0, 0, 0));
        var context = new RunContext("ironclad", ["STRIKE", "DEFEND"], [], 0, 2, 10, 40, 80, 99, null, 1m, 1, 1, 0, []);
        var advice = DeckBuildScoring.Evaluate(context, ["BLOOD_WALL", "HEAVY_BLOW"], catalog, Metrics("BLOOD_WALL", "HEAVY_BLOW"));
        if (advice.Offers[0].Id != "BLOOD_WALL") throw new InvalidOperationException("Defense ranking snapshot failed.");
    }

    private static void ExpensiveDeckValuesEnergy()
    {
        var catalog = Cards(("HEAVY_A", "Тяжёлая атака", "Attack", 3, 18, 0, 0, 0), ("HEAVY_B", "Тяжёлый талант", "Power", 2, 0, 0, 0, 0), ("ENERGY_CARD", "Энергия", "Skill", 1, 0, 0, 0, 1));
        var context = new RunContext("ironclad", ["HEAVY_A", "HEAVY_B"], [], 0, 2, 10, 70, 80, 99, null, 2.5m, 1, 0, 1, []);
        var advice = DeckBuildScoring.Evaluate(context, ["ENERGY_CARD"], catalog, Metrics("ENERGY_CARD"));
        if (advice.Offers[0].Context < 28) throw new InvalidOperationException("Energy ranking snapshot failed.");
    }

    private static void PickCoachDoesNotReplaceCommunityScore()
    {
        var catalog = Cards(("GOOD_CARD", "Хорошая карта", "Attack", 1, 8, 0, 0, 0));
        var metrics = new Dictionary<string, EntityMetric>(StringComparer.OrdinalIgnoreCase) { ["GOOD_CARD"] = new EntityMetric("GOOD_CARD", "Хорошая карта", .2m, .69m, 1000) };
        var coach = new Dictionary<string, CoachOffer>(StringComparer.OrdinalIgnoreCase) { ["GOOD_CARD"] = new CoachOffer("GOOD_CARD", "Хорошая карта", 4m, 3m) };
        var context = new RunContext("ironclad", [], [], 0, 1, 1, 80, 80, 99, null, 1m, 0, 0, 0, []);
        var score = DeckBuildScoring.Evaluate(context, ["GOOD_CARD"], catalog, metrics, coach).Offers[0];
        if (score.Community < 68 || score.Final < 68) throw new InvalidOperationException("Pick Coach replaced community baseline.");
    }

    private static void DeckSizeAndDuplicatesAreAccountedFor()
    {
        var filler = Card("REGRESSION_DUPLICATE", "Skill");
        var other = Card("REGRESSION_OTHER", "Skill");
        var catalog = new Dictionary<string, CardDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            ["REGRESSION_DUPLICATE"] = filler,
            ["REGRESSION_OTHER"] = other
        };
        var deck = Enumerable.Repeat("REGRESSION_DUPLICATE", 2).Concat(Enumerable.Repeat("REGRESSION_OTHER", 20)).ToArray();
        var context = new RunContext("regression", deck, [], 0, 1, 1, 80, 80, 99, null, 1m, 0, 22, 0, []);
        var score = DeckBuildScoring.Evaluate(context, ["REGRESSION_DUPLICATE"], catalog, Metrics("REGRESSION_DUPLICATE")).Offers[0];
        if (score.Context > -4m || !score.Reason.Contains("копия 3", StringComparison.Ordinal) || !score.Reason.Contains("размер колоды", StringComparison.Ordinal))
            throw new InvalidOperationException("Deck-size or duplicate model failed.");

        var stackable = new CardDefinition("PERFECTED_STRIKE", "Идеальный удар", "ironclad", "Attack", 2, 6, 0, 0, 0, "common", "Урон за каждую копию.", new HashSet<string>(), null);
        if (!CardSemanticTags.IsNaturallyRepeatable(stackable))
            throw new InvalidOperationException("Stackable duplicate detection failed.");
    }

    private static void CompoundBloatPenaltyGrowsByAct()
    {
        var duplicate = Card("REGRESSION_DUPLICATE", "Skill");
        var other = Card("REGRESSION_OTHER", "Skill");
        var catalog = new Dictionary<string, CardDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            ["REGRESSION_DUPLICATE"] = duplicate,
            ["REGRESSION_OTHER"] = other
        };
        var deck = Enumerable.Repeat("REGRESSION_DUPLICATE", 2).Concat(Enumerable.Repeat("REGRESSION_OTHER", 28)).ToArray();
        var early = new RunContext("regression", deck, [], 0, 1, 10, 80, 80, 99, null, 1m, 0, 30, 0, []);
        var late = early with { Act = 3, Floor = 40 };
        var earlyScore = DeckBuildScoring.Evaluate(early, ["REGRESSION_DUPLICATE"], catalog, Metrics("REGRESSION_DUPLICATE")).Offers[0];
        var lateScore = DeckBuildScoring.Evaluate(late, ["REGRESSION_DUPLICATE"], catalog, Metrics("REGRESSION_DUPLICATE")).Offers[0];
        if (lateScore.Context >= earlyScore.Context - 15m || !lateScore.Reason.Contains("усиливают друг друга", StringComparison.Ordinal))
            throw new InvalidOperationException($"Compound bloat penalty failed: early {earlyScore.Context:F1}, late {lateScore.Context:F1}.");
    }

    private static void LearnedChoiceAdjustmentIsBounded()
    {
        var values = DeckBuildScoring.CenteredChoiceAdjustments(new decimal?[] { .60m, .20m, null });
        if (values.Length != 3 || values.Any(x => Math.Abs(x) > 10m) || values[0] <= 0m || values[1] >= 0m)
            throw new InvalidOperationException("Learned choice adjustment is unbounded or lost its direction.");
        if (DeckBuildScoring.CenteredChoiceAdjustments(new decimal?[] { null, null }).Any(x => x != 0m))
            throw new InvalidOperationException("Missing learned context changed the score.");
    }

    private static void AutoPickIsConservative()
    {
        var confident = new BuildAssessment("", [], [], 1m, 8, [
            new OfferScore("BEST", "Best", 94m, 80m, 14m, "attack", "", .8m),
            new OfferScore("OTHER", "Other", 70m, 68m, 2m, "", "", .8m)]);
        if (!AutoPickSettings.ShouldPick(confident)) throw new InvalidOperationException("Auto-pick rejected a clear, high-confidence winner.");
        var close = confident with { Offers = [confident.Offers[0], confident.Offers[1] with { Final = 80m }] };
        var skip = confident with { SkipScore = 12 };
        var merelyGood = confident with { Offers = [confident.Offers[0] with { Final = 89m }, confident.Offers[1]] };
        var unknown = confident with { Offers = [confident.Offers[0] with { Confidence = .1m }, confident.Offers[1]] };
        if (AutoPickSettings.ShouldPick(close) || AutoPickSettings.ShouldPick(skip) || AutoPickSettings.ShouldPick(merelyGood) || AutoPickSettings.ShouldPick(unknown))
            throw new InvalidOperationException("Auto-pick safety gate accepted a disputed option.");
    }

    private static void LearnedSkipModelIsAvailableAndDirectional()
    {
        var context = new RunContext("defect", Enumerable.Repeat("FILLER", 28).ToArray(), [], 10, 3, 40,
            60, 75, 99, null, 1m, 0, 0, 0, []);
        var weak = RunHistoryInsights.SkipProbability(context, ["WEAK_A", "WEAK_B", "WEAK_C"], [.05m, .08m, .10m]);
        var strong = RunHistoryInsights.SkipProbability(context, ["GOOD_A", "GOOD_B", "GOOD_C"], [.55m, .60m, .50m]);
        if (weak is null || strong is null || RunHistoryInsights.SkipThreshold <= 0m || RunHistoryInsights.SkipThreshold >= 1m || weak <= strong)
            throw new InvalidOperationException($"Learned skip model is missing or non-directional: weak={weak}, strong={strong}.");
    }

    private static void BossThreatRefinesInsteadOfDominating()
    {
        var aoe = new CardDefinition("AOE", "Вихрь", "ironclad", "Attack", 1, 7, 0, 0, 0, "common", "Deal damage to ALL ENEMIES.", new HashSet<string>(), null);
        var single = aoe with { Id = "SINGLE", Name = "Удар", Description = "Deal damage." };
        var threat = EncounterThreatModel.Assess(new RunContext("ironclad", [], [], 0, 1, 8, 80, 80, 99, "THE_KIN_BOSS", 1m, 0, 0, 0, []));
        var delta = EncounterThreatModel.Adjustment(aoe, threat) - EncounterThreatModel.Adjustment(single, threat);
        if (delta <= 0m || delta > 6m || !threat.Priorities.Contains("массовый урон"))
            throw new InvalidOperationException("Boss threat model is missing or too strong.");
    }

    private static void SegmentRiskIsCalibratedAndDirectional()
    {
        var safe = new RunContext("defect", Enumerable.Repeat("CARD", 18).ToArray(), Enumerable.Repeat("RELIC", 7).ToArray(), 0, 1, 8, 70, 75, 150, null, 1m, 4, 10, 1, [])
        { Route = new RouteSnapshot("rest_site", [new RouteOption(["shop", "rest_site", "monster"])]) };
        var danger = safe with { Hp = 15, Ascension = 10, Route = new RouteSnapshot("monster", [new RouteOption(["elite", "monster", "elite"])]) };
        var safeRisk = SegmentRiskModel.Assess(safe); var dangerRisk = SegmentRiskModel.Assess(danger);
        if (!safeRisk.IsAvailable || !dangerRisk.IsAvailable || dangerRisk.Probability <= safeRisk.Probability ||
            dangerRisk.Low < 0m || dangerRisk.High > 1m || dangerRisk.Low > dangerRisk.High)
            throw new InvalidOperationException($"Segment-risk model unavailable or non-directional: safe={safeRisk.Probability}, danger={dangerRisk.Probability}.");
    }

    private static void ActionPlanExplainsAlternatives()
    {
        var strike = new CardDefinition("STRIKE", "Удар", "ironclad", "Attack", 1, 6, 0, 0, 0, "basic", "", new HashSet<string>(), null);
        var catalog = new Dictionary<string, CardDefinition>(StringComparer.OrdinalIgnoreCase) { ["STRIKE"] = strike };
        var context = new RunContext("ironclad", Enumerable.Repeat("STRIKE", 6).ToArray(), [], 0, 1, 5, 80, 80, 90, null, 1m, 6, 0, 0, []);
        var assessment = new BuildAssessment("", [], [], 1m, 60, [new OfferScore("STRIKE", "Удар", 62m, 60m, 2m, "атака", "", .8m)]);
        var plan = DeckActionPlanner.Create(context, assessment, catalog);
        if (!plan.Contains("пропустить", StringComparison.Ordinal) || !plan.Contains("удалить: Удар", StringComparison.Ordinal) || !plan.Contains("Уверенность", StringComparison.Ordinal))
            throw new InvalidOperationException("Unified action plan omitted an alternative.");
    }

    private static void UpgradePlanRejectsAlreadyUpgradedCards()
    {
        var power = new CardDefinition("POWER", "Power", "defect", "Power", 1, 0, 0, 0, 0, "rare", "", new HashSet<string>(), null);
        var skill = new CardDefinition("SKILL", "Skill", "defect", "Skill", 1, 0, 5, 1, 0, "uncommon", "", new HashSet<string>(), null);
        var catalog = new Dictionary<string, CardDefinition>(StringComparer.OrdinalIgnoreCase) { ["POWER"] = power, ["SKILL"] = skill };
        var context = new RunContext("defect", ["POWER", "SKILL"], [], 0, 2, 20, 60, 70, 100, null, 1m, 0, 1, 1, [])
        {
            CardStates = [new("POWER", 1, 1), new("SKILL", 0, 1)]
        };
        var assessment = new BuildAssessment("", [], [], 1m, 50, []);
        var plan = DeckActionPlanner.Create(context, assessment, catalog);
        if (!plan.Contains("улучшить: Skill", StringComparison.Ordinal) || plan.Contains("улучшить: Power", StringComparison.Ordinal))
            throw new InvalidOperationException("Upgrade planner recommended an already upgraded card: " + plan);

        var allUpgraded = context with { CardStates = [new("POWER", 1, 1), new("SKILL", 1, 1)] };
        var exhausted = DeckActionPlanner.Create(allUpgraded, assessment, catalog);
        if (!exhausted.Contains("улучшить: решать по ближайшему бою", StringComparison.Ordinal))
            throw new InvalidOperationException("Upgrade planner did not reject all upgraded cards: " + exhausted);
    }

    private static void ShopBadgesStayAboveTheirSlots()
    {
        var top = BuildAdvisor.UI.AdviceOverlay.ShopBadgePosition(new Godot.Vector2(600, 0), new Godot.Vector2(1920, 1080), row: 0);
        var bottom = BuildAdvisor.UI.AdviceOverlay.ShopBadgePosition(new Godot.Vector2(900, 420), new Godot.Vector2(1920, 1080), row: 1);
        var scaled = BuildAdvisor.UI.AdviceOverlay.ShopBadgePosition(new Godot.Vector2(600, 0), new Godot.Vector2(1280, 720), row: 0);
        if (Math.Abs(top.X - 530f) > .01f || Math.Abs(top.Y - 180f) > .01f ||
            Math.Abs(bottom.X - 830f) > .01f || Math.Abs(bottom.Y - 570f) > .01f ||
            Math.Abs(scaled.Y - 120f) > .01f)
            throw new InvalidOperationException($"Shop badge row anchors are unstable: {top}; {bottom}; {scaled}.");
    }

    private static void ArchetypeSelectionResetsBetweenRuns()
    {
        ArchetypePreferences.EndRun();
        ArchetypePreferences.ObserveRun("necrobinder", 20, 24);
        ArchetypePreferences.Set("necrobinder", "Души");
        ArchetypePreferences.ObserveRun("defect", 1, 8);
        if (ArchetypePreferences.Get("defect") != "Авто") throw new InvalidOperationException("Archetype leaked between characters.");
        ArchetypePreferences.Set("defect", "Мороз");
        ArchetypePreferences.ObserveRun("defect", 15, 24);
        ArchetypePreferences.ObserveRun("defect", 1, 8);
        if (ArchetypePreferences.Get("defect") != "Авто") throw new InvalidOperationException("Archetype leaked into a restarted run.");
        ArchetypePreferences.EndRun();
    }

    private static void EveryManualArchetypeMatchesItsSignal()
    {
        var cases = new[]
        {
            ("ironclad", "Сила", "STRENGTH_CARD", "STRENGTH"), ("ironclad", "Истощение", "EXHAUST_CARD", "EXHAUST"), ("ironclad", "Блок", "BLOCK_CARD", "BLOCK"), ("ironclad", "Саморанение", "BLOOD_CARD", "BLOOD"), ("ironclad", "Уязвимость", "VULN_CARD", "VULNERABLE"),
            ("silent", "Яд", "POISON_CARD", "POISON"), ("silent", "Заточки", "SHIV_CARD", "SHIV"), ("silent", "Сброс", "DAGGER_THROW", "СБРАСЫВАЕТЕ"),
            ("defect", "Молнии", "LIGHTNING_CARD", "LIGHTNING"), ("defect", "Мороз", "FROST_CARD", "FROST"), ("defect", "Фокус", "FOCUS_CARD", "FOCUS"), ("defect", "Нулевая стоимость", "ZERO_CARD", "ZERO"),
            ("regent", "Звёзды", "STAR_CARD", "STAR"), ("regent", "Кузня", "FORGE_CARD", "FORGE"), ("regent", "Бесцветные карты", "ARSENAL", "CREATE CARD"),
            ("necrobinder", "Души", "SOUL_CARD", "SOUL"), ("necrobinder", "Гибель", "DOOM_CARD", "DOOM"), ("necrobinder", "Призыв", "SUMMON_CARD", "SUMMON")
        };
        foreach (var item in cases)
        {
            var previous = ArchetypePreferences.Get(item.Item1);
            ArchetypePreferences.Set(item.Item1, item.Item2);
            try
            {
                var card = new CardDefinition(item.Item3, item.Item3, item.Item1, "Skill", item.Item2 == "Нулевая стоимость" ? 0 : 1, 0, 0, 0, 0, "common", item.Item4, new HashSet<string>(), null);
                var catalog = new Dictionary<string, CardDefinition> { [SpireCodexMetricsClient.NormalizeId(item.Item3)] = card };
                var context = new RunContext(item.Item1, [], [], 0, 1, 1, 80, 80, 99, null, 1m, 0, 0, 0, []);
                var advice = DeckBuildScoring.Evaluate(context, [item.Item3], catalog, Metrics(item.Item3));
                var minimum = item.Item2 == "Бесцветные карты" ? 10m : 16m;
                if (advice.Offers[0].Context < minimum) throw new InvalidOperationException("Archetype snapshot failed: " + item.Item2);
            }
            finally { ArchetypePreferences.Set(item.Item1, previous); }
        }
    }

    private static void RegentColorlessEngineNeedsBothHalves()
    {
        var payoff = new CardDefinition("ARSENAL", "Арсенал", "regent", "Power", 1, 0, 0, 0, 0, "rare", "Gain Strength when a card is created.", new HashSet<string>(), null);
        var generator = new CardDefinition("SPECTRUM_SHIFT", "Спектральный сдвиг", "regent", "Power", 2, 0, 0, 1, 0, "uncommon", "Add a random Colorless card each turn.", new HashSet<string>(), null);
        var catalog = new Dictionary<string, CardDefinition>(StringComparer.OrdinalIgnoreCase) { ["ARSENAL"] = payoff, ["SPECTRUM_SHIFT"] = generator };
        var previous = ArchetypePreferences.Get("regent");
        ArchetypePreferences.Set("regent", "Бесцветные карты");
        try
        {
            var empty = new RunContext("regent", [], [], 0, 1, 5, 80, 80, 99, null, 1m, 0, 0, 0, []);
            var generatorOnly = DeckBuildScoring.Evaluate(empty, ["SPECTRUM_SHIFT"], catalog, Metrics("SPECTRUM_SHIFT")).Offers[0];
            var assembled = DeckBuildScoring.Evaluate(empty with { Cards = ["ARSENAL"] }, ["SPECTRUM_SHIFT"], catalog, Metrics("SPECTRUM_SHIFT")).Offers[0];
            if (generatorOnly.Context < 4m || generatorOnly.Context > 7m || assembled.Context < 16m)
                throw new InvalidOperationException($"Regent Colorless engine gating failed: lone {generatorOnly.Context:F1}, assembled {assembled.Context:F1}.");
        }
        finally { ArchetypePreferences.Set("regent", previous); }
    }

    private static CardDefinition Card(string id, string type, int cost = 1, int damage = 0, int block = 0, int draw = 0, int energy = 0, string rarity = "common") =>
        new(id, id, "test", type, cost, damage, block, draw, energy, rarity, "", new HashSet<string>(), null);
    private static IReadOnlyDictionary<string, CardDefinition> Cards(params (string Id, string Name, string Type, int Cost, int Damage, int Block, int Draw, int Energy)[] values) =>
        values.ToDictionary(x => SpireCodexMetricsClient.NormalizeId(x.Id), x => new CardDefinition(x.Id, x.Name, "ironclad", x.Type, x.Cost, x.Damage, x.Block, x.Draw, x.Energy, "common", "", new HashSet<string>(), null), StringComparer.OrdinalIgnoreCase);
    private static IReadOnlyDictionary<string, EntityMetric> Metrics(params string[] ids) => ids.ToDictionary(x => SpireCodexMetricsClient.NormalizeId(x), x => new EntityMetric(x, x, .5m, .55m, 100), StringComparer.OrdinalIgnoreCase);
}

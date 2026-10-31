using BuildAdvisor.Data;

namespace BuildAdvisor.Integration;

// Runs at startup without network access. These scenarios guard the recommendation model,
// every playable character, every manual archetype, and API taxonomy used by search filters.
internal static class BuildAdvisorRegression
{
    public static string LastError { get; private set; } = "";
    // While true, personal data (diary, local .run files) is ignored so the matrix is hermetic.
    public static bool IsRunning { get; private set; }

    public static bool Run()
    {
        IsRunning = true;
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
            PooledHolderBadgesLeaveForeignScreens();
            ArchetypeSelectionResetsBetweenRuns();
            ManualArchetypeOverridesGenericPowerAndLateBloat();
            StrongEarlyCardDetectsArchetype();
            EveryManualArchetypeMatchesItsSignal();
            RegentColorlessEngineNeedsBothHalves();
            StarterDetectionUsesRarity();
            StarterStrikesDoNotSatisfyEarlyDamage();
            LowPercentMetricsStayLow();
            EventHintsMatchWholeWords();
            LiftFieldsAreParsed();
            TakeSkipUsesOneScale();
            CharacterMetricsIgnoreOffClassRows();
            ShopPriceNoteShowsRemainingGold();
            NewTextIsTranslated();
            UpgradeDataIsParsed();
            UpgradedOfferScoresHigher();
            UpgradedDeckAndPlannerUseUpgradeValue();
            TierOverridesApply();
            BracketMapsToSupportedValues();
            EventKeysAndMetricsParse();
            EventAdviceUsesCommunityLift();
            PotionAdviceRespectsSlots();
            ScoreBreakdownGroupsAdjustments();
            Release620TextIsTranslated();
            CampfireAdviceWeighsHpRiskAndUpgrade();
            RelicAdviceFollowsDeckNeeds();
            RunReviewComparesChoicesWithAdvice();
            TranslationReplacesWholeWords();
            RecycledGridBadgesFollowCards();
            RoutePlannerWeighsRewardsAgainstHp();
            ArchetypeDirectionUsesCommunityClusters();
            LastError = "";
            return true;
        }
        catch (Exception error)
        {
            LastError = error.Message;
            return false;
        }
        finally { IsRunning = false; }
    }

    // 6.17.0: TWIN_STRIKE/PERFECTED_STRIKE contain "STRIKE" but are reward cards.
    private static void StarterDetectionUsesRarity()
    {
        var strike = Card("STRIKE_IRONCLAD", "Attack", damage: 6, rarity: "basic");
        var twin = Card("TWIN_STRIKE", "Attack", damage: 5, rarity: "common");
        var perfected = Card("PERFECTED_STRIKE", "Attack", cost: 2, damage: 6, rarity: "common");
        var bash = Card("BASH", "Attack", cost: 2, damage: 8, rarity: "basic");
        var fixture = Card("STRIKE", "Attack", damage: 6, rarity: "");
        if (!CardTaxonomy.IsStarterCopy(strike, "STRIKE") || CardTaxonomy.IsStarterCopy(twin, "STRIKE") ||
            CardTaxonomy.IsStarterCopy(perfected, "STRIKE") || CardTaxonomy.IsStarterCopy(bash, "STRIKE") ||
            !CardTaxonomy.IsStarterCopy(fixture, "STRIKE"))
            throw new InvalidOperationException("Starter Strike detection must use rarity, not an ID substring.");
    }

    // 6.17.0: five starter Strikes used to hide the Act 1 "fast damage" need.
    private static void StarterStrikesDoNotSatisfyEarlyDamage()
    {
        var catalog = new[]
        {
            Card("STRIKE", "Attack", damage: 6, rarity: "basic"), Card("DEFEND", "Skill", block: 5, rarity: "basic"),
            Card("BASH", "Attack", cost: 2, damage: 8, rarity: "basic"), Card("QUICK_PICK", "Attack", damage: 9)
        }.ToDictionary(x => SpireCodexMetricsClient.NormalizeId(x.Id), StringComparer.OrdinalIgnoreCase);
        var deck = Enumerable.Repeat("STRIKE", 5).Concat(Enumerable.Repeat("DEFEND", 4)).Append("BASH").ToArray();
        var context = new RunContext("ironclad", deck, [], 0, 1, 3, 80, 80, 99, null, 1.1m, 6, 4, 0, []);
        var result = DeckBuildScoring.Evaluate(context, ["QUICK_PICK"], catalog, Metrics("QUICK_PICK"));
        if (!result.Needs.Contains("быстрый урон"))
            throw new InvalidOperationException("Starter deck in Act 1 must still need fast damage.");
        if (result.Needs.Contains("защита"))
            throw new InvalidOperationException("Floor 3 with full HP must not demand defense from starter Defends.");
    }

    // 6.17.0: rates are percent in the API; a 0.8% pick rate must not become 80%.
    private static void LowPercentMetricsStayLow()
    {
        using var document = System.Text.Json.JsonDocument.Parse("{\"rows\":[{\"id\":\"POPULAR\",\"win_rate\":51.0,\"pick_rate\":45.0,\"picks\":900},{\"id\":\"NICHE\",\"win_rate\":0.9,\"pick_rate\":0.8,\"picks\":40}]}");
        var parsed = SpireCodexMetricsClient.ParseMetrics(document.RootElement, "cards");
        if (parsed["NICHE"].PickRate != .008m || parsed["NICHE"].WinRate != .009m || parsed["POPULAR"].PickRate != .45m)
            throw new InvalidOperationException("Percent metrics must be scaled once per response.");
        using var fractions = System.Text.Json.JsonDocument.Parse("{\"rows\":[{\"id\":\"OLD\",\"win_rate\":0.52,\"pick_rate\":0.3}]}");
        if (SpireCodexMetricsClient.ParseMetrics(fractions.RootElement, "cards")["OLD"].PickRate != .3m)
            throw new InvalidOperationException("Fraction metrics must stay fractions.");
    }

    // 6.17.0: "DAM" (Dam fragment) used to match every option mentioning DAMAGE.
    private static void EventHintsMatchWholeWords()
    {
        var context = new RunContext("ironclad", [], [], 0, 1, 5, 80, 80, 150, null, 1m, 0, 0, 0, []);
        var advice = LocalDeckAdvisor.ForEvent(context, ["Deal 10 damage to all enemies"]);
        if (advice.Contains("повторный выбор награды", StringComparison.Ordinal))
            throw new InvalidOperationException("Event hint matched DAM inside DAMAGE.");
    }

    // 6.18.0: lift, lift_n and use_rate come from the API.
    private static void LiftFieldsAreParsed()
    {
        using var document = System.Text.Json.JsonDocument.Parse("{\"rows\":[{\"id\":\"A\",\"win_rate\":48.3,\"pick_rate\":22.1,\"lift\":12.4,\"lift_n\":15735,\"picks\":15735},{\"id\":\"P\",\"win_rate\":37.3,\"lift\":-1.5,\"lift_n\":900,\"use_rate\":63.8}]}");
        var parsed = SpireCodexMetricsClient.ParseMetrics(document.RootElement, "potions");
        if (parsed["A"].Lift != 12.4m || parsed["A"].LiftSamples != 15735 || parsed["P"].Lift != -1.5m || parsed["P"].UseRate != .638m)
            throw new InvalidOperationException("Lift fields were not parsed.");
    }

    // 6.18.0: take/skip on one win-rate scale, skip = 0 (M1).
    private static void TakeSkipUsesOneScale()
    {
        OfferScore Offer(string id, decimal final, decimal community, decimal? lift, int samples) =>
            new(id, id, final, community, final - community, "", "", .9m, CommunityLift: lift, LiftSamples: samples);
        var strong = new BuildAssessment("", [], [], 1m, 30, [Offer("STRONG", 70m, 70m, 6m, 5000), Offer("WEAK", 30m, 30m, 0.2m, 5000)]);
        var verdict = TakeSkipModel.Evaluate(strong);
        if (!verdict.Take || verdict.Best?.Id != "STRONG" || verdict.WinDelta != 6m)
            throw new InvalidOperationException("Take/skip did not take a clearly positive card.");
        // A bloated deck turns a decent card negative: the deck adjustment uses the same scale.
        var bloated = new BuildAssessment("", [], [], 1m, 30, [Offer("STRONG", 40m, 70m, 6m, 5000)]);
        if (TakeSkipModel.Evaluate(bloated).Take || TakeSkipModel.WinDelta(bloated.Offers[0]) != .3m)
            throw new InvalidOperationException("Deck penalties must be able to make skipping better.");
        // Little evidence needs a 2-point margin instead of half a point.
        var thin = new BuildAssessment("", [], [], 1m, 30, [Offer("THIN", 50m, 50m, 1.5m, 120)]);
        if (TakeSkipModel.Evaluate(thin).Take || TakeSkipModel.EvidenceLabel(120) != "низкая" || TakeSkipModel.EvidenceLabel(1000) != "высокая")
            throw new InvalidOperationException("Low-evidence lift must not be enough to take a card.");
        // Without an API lift the Codex-score fit is used: score 27 is the zero point.
        if (TakeSkipModel.WinDelta(Offer("NOLIFT", 27m, 27m, null, 0)) != 0m)
            throw new InvalidOperationException("Score-to-lift conversion changed its zero point.");
    }

    // 6.18.0: per-character rows replace global rows only with a real share of the sample.
    private static void CharacterMetricsIgnoreOffClassRows()
    {
        var global = new Dictionary<string, EntityMetric>(StringComparer.OrdinalIgnoreCase)
        {
            ["OWN"] = new("OWN", "Own", .3m, .40m, 10000, Lift: 4m, LiftSamples: 10000),
            ["OFFCLASS"] = new("OFFCLASS", "Off", .2m, .30m, 26000, Lift: -1.7m, LiftSamples: 26000),
            ["COLORLESS"] = new("COLORLESS", "Colorless", .2m, .35m, 8000, Lift: 2m, LiftSamples: 8000)
        };
        var specific = new Dictionary<string, EntityMetric>(StringComparer.OrdinalIgnoreCase)
        {
            ["OWN"] = new("OWN", "Own", .3m, .41m, 9700, Lift: 4.5m, LiftSamples: 9700),
            ["OFFCLASS"] = new("OFFCLASS", "Off", .2m, .50m, 514, Lift: 15.6m, LiftSamples: 514),
            ["COLORLESS"] = new("COLORLESS", "Colorless", .2m, .37m, 2000, Lift: 3m, LiftSamples: 2000),
            ["ONLY_HERE"] = new("ONLY_HERE", "Only", .2m, .37m, 50, Lift: 1m, LiftSamples: 50)
        };
        var merged = SpireCodexMetricsClient.Merge(global, specific);
        if (merged["OWN"].Lift != 4.5m || merged["COLORLESS"].Lift != 3m || merged["OFFCLASS"].Lift != -1.7m || !merged.ContainsKey("ONLY_HERE"))
            throw new InvalidOperationException("Character metric merge kept a survivor-biased off-class row or dropped a valid one.");
    }

    // 6.18.0: shop badges show what is left after buying (L3).
    private static void ShopPriceNoteShowsRemainingGold()
    {
        if (MerchantPatch.PriceNote(75, 120) != "цена 75 · после покупки останется 45 золота" ||
            MerchantPatch.PriceNote(150, 120) != "цена 150 · не хватает 30 золота" || MerchantPatch.PriceNote(0, 120) != "")
            throw new InvalidOperationException("Shop price note is wrong.");
    }

    // 6.18.0: every new user-facing line has an English translation.
    private static void NewTextIsTranslated()
    {
        OfferScore Offer(decimal final, decimal? lift, int samples) => new("X", "Card", final, final, 0m, "", "", .9m, CommunityLift: lift, LiftSamples: samples);
        var take = TakeSkipModel.Evaluate(new BuildAssessment("", [], [], 1m, 20, [Offer(70m, 6m, 5000)])).Text;
        var skip = TakeSkipModel.Evaluate(new BuildAssessment("", [], [], 1m, 20, [Offer(30m, .1m, 120)])).Text;
        var metric = new EntityMetric("R", "Relic", null, .4m, 900, Lift: 4.1m, LiftSamples: 900);
        var raw = string.Join("\n", take, skip, MerchantPatch.PriceNote(75, 120), MerchantPatch.PriceNote(150, 120),
            BuildAdvisor.UI.AdviceText.Lift(metric), EventOptionPatch.DeadlyWarning,
            "Взять или пропустить: " + take + ".", "Игроки в похожих ситуациях пропускали награду в 30% случаев.",
            "Card: 70/100 · +6.0 п.п. · уверенность высокая", "Автовыбор", "Офлайн");
        var english = ModLocalization.Translate(raw, AdvisorLanguage.English);
        if (System.Text.RegularExpressions.Regex.IsMatch(english, "[А-Яа-яЁё]"))
            throw new InvalidOperationException("6.18 text is not fully translated: " + english);
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
        // Slot-local placement: centred above the clickable area, whatever row the slot is in.
        var above = BuildAdvisor.UI.AttachedBadges.Above(new Godot.Vector2(150, 40), new Godot.Vector2(120, 34), 6f);
        if (Math.Abs(above.X - 90f) > .01f || Math.Abs(above.Y - 0f) > .01f)
            throw new InvalidOperationException($"Shop badge is not centred above its slot: {above}.");
        // Reward cards are centred on the holder: the badge clears the scaled card top.
        var top = BuildAdvisor.UI.AttachedBadges.CardTopCentre(new Godot.Vector2(0, 0), new Godot.Vector2(300, 422), new Godot.Vector2(.8f, .8f));
        var reward = BuildAdvisor.UI.AttachedBadges.Above(top, new Godot.Vector2(148, 58), 8f);
        if (Math.Abs(top.Y + 168.8f) > .01f || Math.Abs(reward.X + 74f) > .01f || Math.Abs(reward.Y + 234.8f) > .01f)
            throw new InvalidOperationException($"Reward badge does not sit above the card: {top}; {reward}.");
    }

    private static void PooledHolderBadgesLeaveForeignScreens()
    {
        var card = new object();
        var other = new object();
        if (!BuildAdvisor.UI.AttachedBadges.StillBelongs(true, true, card, card) ||
            !BuildAdvisor.UI.AttachedBadges.StillBelongs(true, true, null, other))
            throw new InvalidOperationException("A badge on its own screen and card was removed.");
        // A pooled holder reused by a combat card choice: the badge is outside its screen.
        if (BuildAdvisor.UI.AttachedBadges.StillBelongs(true, false, null, null) ||
            BuildAdvisor.UI.AttachedBadges.StillBelongs(false, false, null, null) ||
            BuildAdvisor.UI.AttachedBadges.StillBelongs(true, true, card, other))
            throw new InvalidOperationException("A badge survived on a reused holder or another card.");
        var a = new object(); var b = new object(); var c = new object();
        var merged = BuildAdvisor.UI.AttachedBadges.Merge(new[] { a, b }, new[] { b, c, null });
        if (merged.Count != 3 || !ReferenceEquals(merged[2], c))
            throw new InvalidOperationException("A drawn card missing from the screen list is not scored.");
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

    // 6.19.0 (M5): catalogue upgrade deltas are parsed; "cost" is absolute, deltas may be strings.
    private static void UpgradeDataIsParsed()
    {
        using var document = System.Text.Json.JsonDocument.Parse("{\"upgrade\":{\"damage\":\"+3\",\"cost\":0,\"cards\":1,\"strengthpower\":\"+1\",\"vulnerable\":\"+1\",\"remove_exhaust\":1}}");
        var upgrade = CardUpgrades.Parse(document.RootElement);
        if (upgrade is null || upgrade.Damage != 3 || upgrade.Cost != 0 || upgrade.Draw != 1 || upgrade.Scaling != 1 || upgrade.OtherEffects != 1 || !upgrade.RemovesExhaust)
            throw new InvalidOperationException("Card upgrade data was not parsed.");
        var card = Card("UP_CARD", "Attack", cost: 2, damage: 6) with { Upgrade = upgrade };
        var upgraded = CardUpgrades.Upgraded(card);
        if (upgraded.Cost != 0 || upgraded.Damage != 9 || upgraded.Draw != 1)
            throw new InvalidOperationException("Upgraded card numbers are wrong.");
        // 2 energy cut (14) + 1.8 damage + 5 draw + 5 exhaust + 3 other + 5 strength = 33.8 → capped at 15.
        if (CardUpgrades.Value(card) != 15m || CardUpgrades.Value(Card("NO_DATA", "Skill")) != 3m)
            throw new InvalidOperationException("Upgrade value is not bounded.");
    }

    // 6.19.0 (M5): an upgraded reward card scores above the base card and is shown as "Name+".
    private static void UpgradedOfferScoresHigher()
    {
        var card = Card("UP_OFFER", "Skill", cost: 1, block: 5) with { Upgrade = new CardUpgrade(null, 0, 3, 1, 0, false, false, false, 0) };
        var catalog = new Dictionary<string, CardDefinition>(StringComparer.OrdinalIgnoreCase) { ["UP_OFFER"] = card };
        var context = new RunContext("ironclad", [], [], 0, 2, 15, 70, 80, 99, null, 1m, 0, 0, 0, []);
        var plain = DeckBuildScoring.Evaluate(context, ["UP_OFFER"], catalog, Metrics("UP_OFFER")).Offers[0];
        var upgraded = DeckBuildScoring.Evaluate(context, ["UP_OFFER"], catalog, Metrics("UP_OFFER"), null, new HashSet<string> { "UP_OFFER" }).Offers[0];
        if (upgraded.Final <= plain.Final || upgraded.Name != "UP_OFFER+" || !ScoreExplanation.Formula(upgraded).Contains("улучшенная версия", StringComparison.Ordinal))
            throw new InvalidOperationException($"Upgraded offer was not valued: base {plain.Final}, upgraded {upgraded.Final}.");
    }

    // 6.19.0 (M5): upgraded deck copies use their upgraded cost; the planner upgrades the card
    // whose upgrade is worth most (a cost cut beats +2 damage).
    private static void UpgradedDeckAndPlannerUseUpgradeValue()
    {
        var costCut = Card("COST_CUT", "Skill", cost: 2, block: 8) with { Upgrade = new CardUpgrade(1, 0, 0, 0, 0, false, false, false, 0) };
        var smallBump = Card("SMALL_BUMP", "Attack", cost: 1, damage: 7) with { Upgrade = new CardUpgrade(null, 2, 0, 0, 0, false, false, false, 0) };
        var catalog = new Dictionary<string, CardDefinition>(StringComparer.OrdinalIgnoreCase) { ["COST_CUT"] = costCut, ["SMALL_BUMP"] = smallBump };
        // SMALL_BUMP comes first, so a tie would pick it: only the cost-cut value can win.
        var deck = new[] { "SMALL_BUMP", "COST_CUT" };
        var plain = new RunContext("ironclad", deck, [], 0, 2, 15, 70, 80, 99, null, 1.5m, 1, 1, 0, []) { CardStates = [new("SMALL_BUMP", 0, 1), new("COST_CUT", 0, 1)] };
        var upgraded = plain with { CardStates = [new("SMALL_BUMP", 0, 1), new("COST_CUT", 1, 1)] };
        if (CardUpgrades.Value(costCut) != 7m || CardUpgrades.Value(smallBump) != 2m)
            throw new InvalidOperationException("One energy of cost reduction must be worth 7 points.");
        if (DeckBuildScoring.Evaluate(upgraded, [], catalog, Metrics()).AverageCost >= DeckBuildScoring.Evaluate(plain, [], catalog, Metrics()).AverageCost)
            throw new InvalidOperationException("Upgraded deck copy kept its base cost.");
        var plan = DeckActionPlanner.Create(plain, new BuildAssessment("", [], [], 1m, 50, []), catalog);
        if (!plan.Contains("улучшить: COST_CUT", StringComparison.Ordinal))
            throw new InvalidOperationException("Planner did not pick the most valuable upgrade: " + plan);
    }

    // 6.19.0 (L5): personal corrections apply, character keys win, values are clamped to ±30.
    private static void TierOverridesApply()
    {
        var parsed = TierOverrides.Parse("// note\n{ \"cards\": { \"OVR_CARD\": 8, \"ironclad:OVR_CARD\": -12, \"big\": 99 }, \"relics\": { \"OVR_RELIC\": 6, } }");
        try
        {
            TierOverrides.TestOverrides = parsed;
            if (TierOverrides.ForCard("silent", "OVR_CARD") != 8m || TierOverrides.ForCard("ironclad", "ovr card") != -12m ||
                TierOverrides.ForCard("silent", "BIG") != 30m || TierOverrides.ForRelic("defect", "OVR_RELIC") != 6m || TierOverrides.ForCard("silent", "OTHER") != 0m)
                throw new InvalidOperationException("Tier override lookup is wrong.");
            var catalog = new Dictionary<string, CardDefinition>(StringComparer.OrdinalIgnoreCase) { ["OVR_CARD"] = Card("OVR_CARD", "Skill") };
            var context = new RunContext("ironclad", [], [], 0, 2, 15, 80, 80, 99, null, 1m, 0, 0, 0, []);
            var score = DeckBuildScoring.Evaluate(context, ["OVR_CARD"], catalog, Metrics("OVR_CARD")).Offers[0];
            if (score.Adjustments?.Any(x => x.Label == "ваша поправка" && x.Value == -12m) != true || !ScoreExplanation.Formula(score).Contains("(ваша поправка)", StringComparison.Ordinal))
                throw new InvalidOperationException("Tier override is not visible in the formula: " + ScoreExplanation.Formula(score));
            var plan = ShopPlanBuilder.Create(context, [], [new ShopItem("OVR_RELIC", 150)], catalog, Metrics(), Metrics("OVR_RELIC"));
            if (!plan.Contains("ваша поправка +6", StringComparison.Ordinal))
                throw new InvalidOperationException("Relic override is not visible in the shop plan.");
            if (HasCyrillic(ModLocalization.Translate(ScoreExplanation.Formula(score) + "\n" + plan, AdvisorLanguage.English)))
                throw new InvalidOperationException("6.19 text is not fully translated.");
        }
        finally { TierOverrides.TestOverrides = null; }
    }

    // 6.20.0: Spire Codex rejects a0..a9 (400 "unknown bracket"), which hid all advice below A10.
    private static void BracketMapsToSupportedValues()
    {
        RunContext At(int ascension) => new("ironclad", [], [], ascension, 1, 2, 80, 80, 99, null, 1m, 0, 0, 0, []);
        if (At(0).Bracket != "all" || At(2).Bracket != "all" || At(9).Bracket != "all" || At(10).Bracket != "a10" || At(20).Bracket != "a10")
            throw new InvalidOperationException("Run brackets must map to all/a10.");
    }

    private static void EventKeysAndMetricsParse()
    {
        var (eventId, optionId) = EventKeys.Parse("SELF_HELP_BOOK.pages.INITIAL.options.READ_ENTIRE_BOOK");
        if (eventId != SpireCodexMetricsClient.NormalizeId("SELF_HELP_BOOK") || optionId != SpireCodexMetricsClient.NormalizeId("READ_ENTIRE_BOOK"))
            throw new InvalidOperationException("Event text key was not split: " + eventId + "/" + optionId);
        if (EventKeys.Parse("NO_OPTIONS_HERE").Option is not null || EventKeys.Parse(null).Event is not null)
            throw new InvalidOperationException("Keys without options must not produce an option id.");
        var parsed = EventMetricsClient.Parse("""{"rows":[{"event":"SELF_HELP_BOOK","option":"READ_ENTIRE_BOOK","share":40.5,"win_rate":12.3,"lift":2.4,"lift_n":900},{"event":"X","share":1}]}""");
        if (parsed.Count != 1 || !parsed.TryGetValue(EventMetricsClient.Key("SELF_HELP_BOOK", "READ_ENTIRE_BOOK"), out var row) || row.Lift != 2.4m || row.LiftSamples != 900 || row.Share != 40.5m)
            throw new InvalidOperationException("Event metrics were not parsed.");
    }

    private static void EventAdviceUsesCommunityLift()
    {
        var events = new Dictionary<string, EventOptionMetric>(StringComparer.OrdinalIgnoreCase)
        {
            [EventMetricsClient.Key("EV", "GOOD")] = new("EV", "GOOD", 30m, 15m, 3.5m, 4000),
            [EventMetricsClient.Key("EV", "DEADLY")] = new("EV", "DEADLY", 10m, 20m, 9m, 4000),
            [EventMetricsClient.Key("EV", "THIN")] = new("EV", "THIN", 5m, 10m, 6m, EventAdvisor.MinimumSamples - 1),
        };
        var relics = new Dictionary<string, EntityMetric>(StringComparer.OrdinalIgnoreCase)
        {
            [SpireCodexMetricsClient.NormalizeId("ANCIENT_RELIC")] = new("ANCIENT_RELIC", "Ancient Relic", null, 40m, 2000, Lift: 1.2m, LiftSamples: 2000)
        };
        EventChoice Choice(string option, bool lethal = false, string? relic = null) => new(option, "EV", option, relic, lethal);
        var rated = EventAdvisor.Rate([Choice("GOOD"), Choice("DEADLY", lethal: true), Choice("THIN"), Choice("RELIC", relic: "ANCIENT_RELIC"), Choice("NONE")],
            events, relics, null, _ => "local");
        if (rated[0].Source != "event" || rated[0].Lift != 3.5m || rated[2].Source != "local" || rated[3].Source != "relic" || rated[3].Lift != 1.2m || rated[4].Line != "local")
            throw new InvalidOperationException("Event options must use event lift, relic lift, or the local hint.");
        var text = EventAdvisor.Format(rated);
        if (!text.Contains("★ GOOD") || text.Contains("★ DEADLY") || text.Contains("★ THIN"))
            throw new InvalidOperationException("The best mark must skip lethal and low-sample options: " + text);
        // 6.22.5: Ancient options that do not expose their relic are matched by option ID or name.
        var ancientRelics = new Dictionary<string, EntityMetric>(StringComparer.OrdinalIgnoreCase)
        {
            [SpireCodexMetricsClient.NormalizeId("SEAL_OF_GOLD")] = new("SEAL_OF_GOLD", "Seal of Gold", null, 40m, 8000, Lift: 14.5m, LiftSamples: 8000),
            [SpireCodexMetricsClient.NormalizeId("STORYBOOK")] = new("STORYBOOK", "Storybook", null, 40m, 9000, Lift: 17.1m, LiftSamples: 9000),
        };
        var ancientNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["STORYBOOK"] = "Сказка", ["SEAL_OF_GOLD"] = "Золотая печать" };
        var byOption = new EventChoice("Золотая печать Даёт энергию", "TEZCATARA", "SEAL_OF_GOLD", null, false);
        var byName = new EventChoice("Сказка Получить 1 «Ярчайшее пламя».", "TEZCATARA", "OPTION_2", null, false);
        var unknown = new EventChoice("Сказочный суп", "TEZCATARA", "SOUP", null, false);
        if (EventAdvisor.RelicOf(byOption, ancientRelics, null) != "SEAL_OF_GOLD" || EventAdvisor.RelicOf(byName, ancientRelics, ancientNames) != "STORYBOOK" ||
            EventAdvisor.RelicOf(unknown, ancientRelics, ancientNames) is not null ||
            EventAdvisor.Rate([byOption, byName, unknown], new Dictionary<string, EventOptionMetric>(), ancientRelics, ancientNames, _ => "local").Select(x => x.Source).SequenceEqual(["relic", "relic", "local"]) != true)
            throw new InvalidOperationException("Ancient options must find their relic by option ID or name.");
        // 6.22.2: the same verdict on the option buttons.
        var best = EventAdvisor.Best(rated);
        var tags = rated.Select(x => EventAdvisor.Tag(x, best)).ToArray();
        if (tags[0] != ("+3.5 п.п.", "good", true) || tags[1] != ("⚠ смерть", "high", false) || tags[2].Best || tags[4].Text != "" ||
            EventAdvisor.Tag(rated[0] with { Lift = -1m }, best).Severity != "medium" ||
            HasCyrillic(ModLocalization.Translate(string.Join(" ", tags.Select(x => x.Text)), AdvisorLanguage.English)))
            throw new InvalidOperationException("Option tags are wrong: " + string.Join(" | ", tags));
        if (!EventAdvisor.Format(EventAdvisor.Rate([Choice("NONE")], events, relics, null, _ => "local")).Contains("Статистики по этому событию нет"))
            throw new InvalidOperationException("An event without data must say so.");
    }

    private static void PotionAdviceRespectsSlots()
    {
        var metrics = new Dictionary<string, EntityMetric>(StringComparer.OrdinalIgnoreCase)
        {
            [SpireCodexMetricsClient.NormalizeId("STRONG")] = new("STRONG", "Strong", null, 40m, 3000, Lift: 3m, LiftSamples: 3000, UseRate: .8m),
            [SpireCodexMetricsClient.NormalizeId("WEAK")] = new("WEAK", "Weak", null, 40m, 3000, Lift: -1m, LiftSamples: 3000),
            [SpireCodexMetricsClient.NormalizeId("HELD")] = new("HELD", "Held", null, 40m, 3000, Lift: .5m, LiftSamples: 3000),
        };
        RunContext Context(params string[] held) => new("ironclad", [], [], 0, 1, 5, 80, 80, 99, null, 1m, 0, 0, 0, []) { Potions = held, PotionSlots = 2 };
        string Verdict(RunContext context, string id, decimal? risk = null) => PotionAdvisor.Rate(context, [(id, 0)], metrics, null, risk)[0].Verdict;
        if (!Verdict(Context("HELD"), "STRONG").StartsWith("брать: есть свободный слот") ||
            !Verdict(Context("HELD"), "WEAK").StartsWith("можно пропустить") ||
            !Verdict(Context("HELD"), "WEAK", .5m).StartsWith("брать: впереди опасный участок") ||
            !Verdict(Context("HELD", "HELD"), "STRONG").StartsWith("заменить Held") ||
            !Verdict(Context("HELD", "HELD"), "WEAK").StartsWith("слоты заняты"))
            throw new InvalidOperationException("Potion verdicts do not follow slots, lift and risk.");
        if (!PotionAdvisor.Format(Context("HELD"), []).Contains("Слоты зелий: 1/2"))
            throw new InvalidOperationException("Potion slots must be shown.");
    }

    private static void ScoreBreakdownGroupsAdjustments()
    {
        var score = new OfferScore("X", "X", 71m, 50m, 0m, "", "", .9m,
            Adjustments: [new(15m, "нужна защита"), new(4m, "нужен добор"), new(-6m, "копия 3"), new(8m, "синергия архетипа"), new(.2m, "граница шкалы")],
            CommunityLift: 2m, LiftSamples: 800);
        var text = ScoreExplanation.Breakdown(score);
        if (!text.Contains("База (статистика сообщества): 50") || !text.Contains("Потребности колоды: +19 (нужна защита, нужен добор)") ||
            !text.Contains("Копии и размер колоды: -6") || !text.Contains("Архетип и синергия: +8") || !text.Contains("Итог: 71/100") ||
            text.Contains("Граница шкалы") || !text.Contains("Лифт сообщества: +2.0 п.п. по 800 выборам"))
            throw new InvalidOperationException("Score breakdown is wrong: " + text);
        if (text.IndexOf("Потребности колоды", StringComparison.Ordinal) > text.IndexOf("Копии и размер колоды", StringComparison.Ordinal))
            throw new InvalidOperationException("Breakdown groups must keep their order.");
        var clamped = ScoreExplanation.Breakdown(score with { Final = 65m });
        if (!clamped.Contains("Граница шкалы и округление: -6") || ScoreExplanation.GroupOf("что-то новое") != "Прочее")
            throw new InvalidOperationException("Breakdown must show the clamp remainder: " + clamped);
    }

    private static void Release620TextIsTranslated()
    {
        var metrics = new Dictionary<string, EntityMetric>(StringComparer.OrdinalIgnoreCase)
        {
            [SpireCodexMetricsClient.NormalizeId("HELD")] = new("HELD", "Held", null, 40m, 3000, Lift: .5m, LiftSamples: 3000, UseRate: .5m),
            [SpireCodexMetricsClient.NormalizeId("WEAK")] = new("WEAK", "Weak", null, 40m, 3000, Lift: -1m, LiftSamples: 3000),
        };
        var open = new RunContext("ironclad", [], [], 0, 1, 5, 80, 80, 99, null, 1m, 0, 0, 0, []) { Potions = ["HELD"], PotionSlots = 2 };
        var full = open with { Potions = ["WEAK", "WEAK"] };
        var unread = open with { PotionSlots = 0 };
        var events = new Dictionary<string, EventOptionMetric>(StringComparer.OrdinalIgnoreCase) { [EventMetricsClient.Key("EV", "A")] = new("EV", "A", 30m, 15m, 3.5m, 4000) };
        var relics = new Dictionary<string, EntityMetric>(StringComparer.OrdinalIgnoreCase) { ["R"] = new("R", "Relic", null, 40m, 900, Lift: 1m, LiftSamples: 900) };
        var score = new OfferScore("X", "X", 66m, 50m, 0m, "", "", .9m, Adjustments: [new(15m, "нужна защита"), new(-6m, "копия 2"), new(4m, "синергия архетипа"),
            new(2m, "ценность в акте 1"), new(3m, "улучшенная версия"), new(2m, "ваша история выборов"), new(1m, "X")], CommunityLift: 1m, LiftSamples: 400);
        var raw = string.Join("\n",
            "BUILD ADVISOR · награды",
            PotionAdvisor.Format(open, PotionAdvisor.Rate(open, [("HELD", 50), ("WEAK", 0), ("NEW", 500)], metrics, null)),
            PotionAdvisor.Format(open, PotionAdvisor.Rate(open, [("WEAK", 0)], metrics, null, .5m)),
            PotionAdvisor.Format(full, PotionAdvisor.Rate(full, [("HELD", 0), ("WEAK", 0)], metrics, null)),
            PotionAdvisor.Format(unread, []),
            EventAdvisor.Format(EventAdvisor.Rate([new("A", "EV", "A", null, false), new("B", "EV", "B", "R", false), new("C", "EV", "C", null, true)], events, relics, null, _ => "")),
            EventAdvisor.Format(EventAdvisor.Rate([new("C", "EV", "C", null, false)], events, relics, null, _ => "")),
            ScoreExplanation.Breakdown(score), ScoreExplanation.Breakdown(score with { Final = 100m }),
            "Статистика сообщества недоступна — оценка по каталогу карт и локальной модели.");
        var english = ModLocalization.Translate(raw, AdvisorLanguage.English);
        if (HasCyrillic(english)) throw new InvalidOperationException("6.20 text is not fully translated: " + english);
    }

    // 6.21.0 (M6): rest wins at low HP, before a boss and at high risk; a strong upgrade wins at
    // healthy HP; a full-HP rest is worth nothing; community lift is reference only.
    private static void CampfireAdviceWeighsHpRiskAndUpgrade()
    {
        RunContext At(int hp, string next = "monster") => new("ironclad", [], [], 0, 1, 10, hp, 80, 99, null, 1m, 0, 0, 0, [])
            { Route = new RouteSnapshot("rest_site", [new RouteOption([next])]) };
        (string, decimal)? upgrade = ("Bash", 8m);
        string[] both = ["HEAL", "SMITH"];
        string? Pick(RunContext context, decimal risk, (string, decimal)? up = null) => CampfireAdvisor.Advise(context, both, 24m, risk, up ?? upgrade).Recommended;
        if (Pick(At(70), .15m) != "SMITH" || Pick(At(80), .9m) != "SMITH")
            throw new InvalidOperationException("Healthy HP must upgrade.");
        if (Pick(At(25), .1m) != "HEAL" || Pick(At(45), .6m) != "HEAL" || Pick(At(55), .2m, ("Bash", 4m)) != "HEAL")
            throw new InvalidOperationException("Low HP or high risk must rest.");
        if (Pick(At(60, "boss"), .2m) != "HEAL" || Pick(At(60), .2m) != "SMITH")
            throw new InvalidOperationException("A boss next must tip a mid-HP campfire to rest.");
        if (CampfireAdvisor.RestValue(At(80), 24m, 1m) != 0m || CampfireAdvisor.Advise(At(70), ["HEAL"], 24m, .1m, upgrade).Recommended != "HEAL")
            throw new InvalidOperationException("Full HP rest has no value; a lone rest option is still the advice.");
        var metrics = CampfireAdvisor.Parse("""{"rows":[{"choice":"HEAL","name":"Rest","share":38.6,"win_rate":40.8,"lift":-2.0,"lift_n":8891209,"low_hp_share":65.5},{"choice":"SMITH","name":"Smith","share":54.6,"lift":14.7,"lift_n":100,"low_hp_share":14.9}]}""");
        if (metrics.Count != 2 || metrics["HEAL"].Lift != -2m || metrics["SMITH"].LowHpShare != 14.9m)
            throw new InvalidOperationException("Campfire metrics were not parsed.");
        var advice = CampfireAdvisor.Advise(At(25), both, 24m, .3m, upgrade, metrics);
        if (advice.Recommended != "HEAL" || !advice.Text.Contains("Лучшее улучшение, если HP хватает: Bash") || !advice.Text.Contains("справочно"))
            throw new InvalidOperationException("Campfire text is incomplete: " + advice.Text);
        var english = ModLocalization.Translate(string.Join("\n", advice.Text, CampfireAdvisor.Advise(At(70), both, 24m, .1m, upgrade).Text,
            CampfireAdvisor.Advise(At(70), ["SMITH"], null, null, null).Text, CampfireAdvisor.Advise(At(70), ["DIG"], null, null, null).Text,
            CampfireAdvisor.Advise(new RunContext("ironclad", [], [], 0, 1, 1, 0, 0, 0, null, 1m, 0, 0, 0, []), both, null, null, upgrade).Text, "BUILD ADVISOR · костёр"), AdvisorLanguage.English);
        if (HasCyrillic(english)) throw new InvalidOperationException("Campfire text is not translated: " + english);
    }

    // 6.21.0 (M9): relic roles shift the community baseline by deck need; ranking is on the
    // win-rate scale; Ancient options use the same score when the run is known.
    private static void RelicAdviceFollowsDeckNeeds()
    {
        var metrics = new Dictionary<string, EntityMetric>(StringComparer.OrdinalIgnoreCase)
        {
            [SpireCodexMetricsClient.NormalizeId("SOZU")] = new("SOZU", "Sozu", null, 40m, 3000, CodexScore: 50m, Lift: 4.4m, LiftSamples: 3000),
            [SpireCodexMetricsClient.NormalizeId("TUNGSTEN_ROD")] = new("TUNGSTEN_ROD", "Tungsten Rod", null, 40m, 3000, CodexScore: 55m, Lift: 5.3m, LiftSamples: 3000),
            [SpireCodexMetricsClient.NormalizeId("MEMBERSHIP_CARD")] = new("MEMBERSHIP_CARD", "Membership Card", null, 40m, 3000, Lift: 3.8m, LiftSamples: 3000),
        };
        RunContext Context(decimal cost = 1m, int hp = 70, int act = 1, int gold = 150) => new("ironclad", [], [], 0, act, 5, hp, 80, gold, null, cost, 0, 0, 0, []);
        decimal Final(RunContext c, string id, decimal? risk = null) => RelicAdvisor.Score(c, id, metrics, null, risk).Final;
        if (Final(Context(1.8m), "SOZU") != 74m || Final(Context(1m), "SOZU") != 50m)
            throw new InvalidOperationException("Energy relics must follow deck cost.");
        if (Final(Context(hp: 20), "TUNGSTEN_ROD") != 71m || Final(Context(), "TUNGSTEN_ROD", .5m) != 63m || Final(Context(), "TUNGSTEN_ROD", .1m) != 55m)
            throw new InvalidOperationException("Survival relics must follow HP and risk.");
        var economyBase = TakeSkipModel.ZeroLiftScore + 3.8m / TakeSkipModel.PointsPerScore;
        if (Final(Context(act: 3, gold: 50), "MEMBERSHIP_CARD") != decimal.Round(economyBase - 8m, 4) && Math.Abs(Final(Context(act: 3, gold: 50), "MEMBERSHIP_CARD") - (economyBase - 8m)) > .01m ||
            Math.Abs(Final(Context(gold: 50), "MEMBERSHIP_CARD") - (economyBase + 10m)) > .01m || Final(Context(), "UNKNOWN_RELIC") != 50m)
            throw new InvalidOperationException("Economy relics must lose value in Act 3; unknown relics stay at 50.");
        var ranked = RelicAdvisor.Rank(Context(1.8m), ["TUNGSTEN_ROD", "SOZU"], metrics);
        var text = RelicAdvisor.Format(Context(1.8m), ranked);
        if (ranked[0].Id != "SOZU" || !text.StartsWith("Act 1") || !text.Contains("★ Sozu") || !text.Contains("Совет: Sozu") || !text.Contains("Потребности колоды: +24"))
            throw new InvalidOperationException("Relic ranking must follow deck needs: " + text);
        if (RelicAdvisor.Rank(Context(1m), ["TUNGSTEN_ROD", "SOZU"], metrics)[0].Id != "TUNGSTEN_ROD")
            throw new InvalidOperationException("Without an energy need the higher community relic wins.");
        var choice = new EventChoice("Take", "ANCIENT", "TAKE", "SOZU", false);
        var withRun = EventAdvisor.Rate([choice], new Dictionary<string, EventOptionMetric>(), metrics, null, _ => "", Context(1.8m))[0];
        var without = EventAdvisor.Rate([choice], new Dictionary<string, EventOptionMetric>(), metrics, null, _ => "")[0];
        if (without.Lift != 4.4m || withRun.Lift <= without.Lift || !withRun.Line.Contains("решает дефицит энергии"))
            throw new InvalidOperationException("Ancient relic options must use the deck-aware relic score.");
        var raw = string.Join("\n", text, RelicAdvisor.Format(Context(), RelicAdvisor.Rank(Context(hp: 20, act: 3, gold: 50), ["TUNGSTEN_ROD", "MEMBERSHIP_CARD", "X"], metrics, null, .5m)),
            RelicAdvisor.Format(Context(), RelicAdvisor.Rank(Context(gold: 50), ["SOZU", "MEMBERSHIP_CARD"], metrics, null, .5m)),
            RelicAdvisor.Format(Context(), []), RelicAdvisor.Format(Context(), RelicAdvisor.Rank(Context(), ["X", "Y"], metrics)), withRun.Line,
            "Статистика сообщества недоступна — оценка по ролям реликвий и колоде.", "Sozu: у похожих победных билдов 40%", "BUILD ADVISOR · реликвии\n\nЗагрузка общей статистики…");
        var english = ModLocalization.Translate(raw, AdvisorLanguage.English);
        if (HasCyrillic(english)) throw new InvalidOperationException("Relic text is not translated: " + english);
    }

    // 6.21.0 (M7): the review counts agreement, ranks divergences by the win-rate value given up,
    // treats a skip as 0 and compares agreement in wins and losses only with enough runs.
    private static void RunReviewComparesChoicesWithAdvice()
    {
        var skip = PersonalRunDiary.SkipMarker;
        PersonalDecision D(string seed, int floor, string chosen, string advised, params (string Id, decimal Delta)[] deltas) =>
            new(DateTimeOffset.UnixEpoch, "ironclad", 1, floor, deltas.Select(x => x.Id).ToArray(), chosen, null, seed, advised,
                deltas.ToDictionary(x => x.Id, x => x.Delta, StringComparer.OrdinalIgnoreCase));
        var diary = new List<PersonalDecision>
        {
            D("RUN", 3, "A", "A", ("A", 3m), ("B", 1m)),
            D("RUN", 6, "B", "A", ("A", 4m), ("B", 1.5m)),
            D("RUN", 9, "C", skip, ("C", -1.2m)),
            D("RUN", 12, skip, "D", ("D", 2m)),
            D("RUN", 14, "E", "F", ("E", 2m), ("F", 1m)),
            new(DateTimeOffset.UnixEpoch, "ironclad", 1, 2, ["OLD"], "OLD", null),
        };
        if (RunReview.Loss(diary[1]) != 2.5m || RunReview.Loss(diary[2]) != 1.2m || RunReview.Loss(diary[3]) != 2m || RunReview.Loss(diary[4]) != -1m || RunReview.Loss(diary[5]) is not null)
            throw new InvalidOperationException("Review losses must compare advice and choice on the win-rate scale.");
        var text = RunReview.Build("RUN", false, 15, diary, [], id => id == "B" ? "Bravo" : id);
        if (!text.Contains("поражение · этаж 15") || !text.Contains("совпало с советом: 1/5 (20%)") ||
            text.IndexOf("Bravo вместо A (−2.5", StringComparison.Ordinal) < 0 || text.IndexOf("этаж 6", StringComparison.Ordinal) > text.IndexOf("этаж 12", StringComparison.Ordinal) ||
            !text.Contains("пропуск вместо D (−2.0") || text.Contains("E вместо F") || !text.Contains("Всего по оценке мода: −5.7"))
            throw new InvalidOperationException("Run review is wrong: " + text);
        if (!RunReview.Build("OTHER", true, 50, diary, []).Contains("не записано"))
            throw new InvalidOperationException("A run without decisions must say so.");
        var many = new List<PersonalDecision>(diary);
        var outcomes = new List<RunOutcome>();
        for (var i = 0; i < 6; i++)
        {
            var seed = "S" + i; var win = i < 3;
            many.Add(D(seed, 5, win ? "A" : "B", "A", ("A", 2m), ("B", 1m)));
            outcomes.Add(new RunOutcome(DateTimeOffset.UnixEpoch, "ironclad", seed, win, win ? 50 : 20, 0));
        }
        var across = RunReview.Build("S0", true, 50, many, outcomes);
        if (!across.Contains("в победах 100% (3), в поражениях 0% (3)") || !across.Contains("Записано забегов: 6 · побед 3 (50%) · средний этаж 35") ||
            !RunReview.Build("S0", true, 50, many, outcomes.Take(4).ToArray()).Contains("сейчас побед 3, поражений 1"))
            throw new InvalidOperationException("Cross-run comparison is wrong: " + across);
        var english = ModLocalization.Translate(string.Join("\n", text, across, RunReview.Build("OTHER", true, 50, diary, []), RunReview.Build("S0", true, 50, many, outcomes.Take(4).ToArray()),
            RunReview.Build("S0", true, 50, [D("S0", 1, "A", "A", ("A", 1m))], []), "BUILD ADVISOR · итог"), AdvisorLanguage.English);
        if (HasCyrillic(english)) throw new InvalidOperationException("Run review is not translated: " + english);
    }

    // 6.21.0: short dictionary keys used to replace fragments inside longer words
    // ("достаточно" → "toстаточно", "потребности" → "потребнThorns").
    private static void TranslationReplacesWholeWords()
    {
        if (ModLocalization.ReplaceWords("достаточно до 5", "до", "to") != "достаточно to 5" ||
            ModLocalization.ReplaceWords("Тип: Атака; архетип", "Тип", "Type") != "Type: Атака; архетип" ||
            ModLocalization.ReplaceWords("(до)", "до", "to") != "(to)" || ModLocalization.ReplaceWords("x · y", " · ", "/") != "x/y")
            throw new InvalidOperationException("Dictionary keys must match whole words.");
        var english = ModLocalization.Translate("Недостаточно похожих ранов · потребности колоды · Локальный совет по реликвиям:", AdvisorLanguage.English);
        if (english.Contains("to", StringComparison.Ordinal) && english.Contains("toстат", StringComparison.Ordinal) || english.Contains("Thorns") || english.Contains("relicм"))
            throw new InvalidOperationException("A short key corrupted a word: " + english);
        if (ModLocalization.Translate("Недостаточно похожих ранов", AdvisorLanguage.English) != "Not enough similar runs")
            throw new InvalidOperationException("Older texts must be translated.");
    }

    // 6.21.1: the deck grid hands holders to other cards while scrolling; a badge must follow the
    // card, new rows must get badges and holders that left the grid must lose them.
    private static void RecycledGridBadgesFollowCards()
    {
        var shown = new Dictionary<int, string> { [1] = "BASH", [2] = "STRIKE", [3] = "ANGER" };
        bool Scored(string id) => id != "CURSE";
        var (create, remove) = BuildAdvisor.UI.BadgeTracking.Changes(shown, [(1, "BASH"), (2, "INFLAME"), (4, "CLOTHESLINE"), (5, "CURSE"), (2, "INFLAME")], Scored);
        if (create.Count != 2 || !create.Contains((2, "INFLAME")) || !create.Contains((4, "CLOTHESLINE")) ||
            remove.Count != 2 || !remove.Contains(2) || !remove.Contains(3))
            throw new InvalidOperationException("Grid badges must follow the card each holder shows.");
        var (none, kept) = BuildAdvisor.UI.BadgeTracking.Changes(shown, [(1, "bash"), (2, "STRIKE"), (3, "ANGER")], Scored);
        if (none.Count != 0 || kept.Count != 0) throw new InvalidOperationException("Unchanged holders must keep their badges.");
    }

    // 6.22.0 (H1): an elite is worth it at healthy HP and avoided when the expected damage would
    // kill; a rest heals when HP is low; each node carries the forecast of its best path.
    private static void RoutePlannerWeighsRewardsAgainstHp()
    {
        RouteNode N(string id, string type, params string[] children) => new(id, type, children);
        var graph = new RouteGraph(["E", "M"], new Dictionary<string, RouteNode>
        {
            ["E"] = N("E", "elite", "R"), ["M"] = N("M", "monster", "R"),
            ["R"] = N("R", "rest_site", "B"), ["B"] = N("B", "boss"),
        });
        var damage = new RouteDamage(10m, 28m, 46m);
        RunContext At(int hp) => new("ironclad", [], [], 0, 1, 5, hp, 80, 100, null, 1m, 0, 0, 0, []);
        var healthy = RoutePlanner.Plan(At(80), graph, damage);
        if (healthy.PathCount != 2 || healthy.Best?.Nodes[0].Id != "E" || !healthy.ByNode["E"].OnBest || healthy.ByNode["M"].OnBest)
            throw new InvalidOperationException("At full HP the elite path must win.");
        var low = RoutePlanner.Plan(At(27), graph, damage with { Boss = 30m });
        if (low.Best?.Nodes[0].Id != "M" || !low.Top.Any(x => x.Lethal && x.Nodes[0].Id == "E"))
            throw new InvalidOperationException("An elite that kills on average damage must be avoided.");
        var rest = low.Best!.Nodes.First(x => x.Type == "rest_site");
        if (!rest.Rest || rest.Heal != 24m || rest.HpAfter != 41m || low.Best.HpAtBoss != 41m || low.Best.Lethal)
            throw new InvalidOperationException("Low HP must rest before the boss: " + rest);
        var nearFull = RoutePlanner.Plan(At(80), new RouteGraph(["M"], new Dictionary<string, RouteNode> { ["M"] = N("M", "monster", "R"), ["R"] = N("R", "rest_site", "B"), ["B"] = N("B", "boss") }), damage with { Monster = 4m });
        if (nearFull.ByNode["R"].Rest || !healthy.ByNode["R"].Rest)
            throw new InvalidOperationException("The campfire must upgrade near full HP and rest at 52/80 before the boss.");
        // Penalty 30 × (share − 0.15)², doubled for the boss: elite 28/80 → 1.2; boss 46/76 → 12.4.
        if (healthy.Best.Penalty != 13.6m || RoutePlanner.Severity(healthy.ByNode["B"]) != "high")
            throw new InvalidOperationException("Danger penalty or severity is wrong: " + healthy.Best.Penalty);
        var elite = healthy.ByNode["E"];
        if (RoutePlanner.Label(elite) != "−28 → 52" || RoutePlanner.Severity(elite) != "medium" || RoutePlanner.Severity(low.ByNode["E"]) != "high" ||
            RoutePlanner.Label(rest) != "+24 HP" || RoutePlanner.Label(nearFull.ByNode["R"]) != "улучш.")
            throw new InvalidOperationException("Map labels are wrong.");
        // Cycles and missing children must not hang or crash the walk.
        var odd = new RouteGraph(["A"], new Dictionary<string, RouteNode> { ["A"] = N("A", "monster", "C", "X"), ["C"] = N("C", "unknown", "A") });
        if (RoutePlanner.Plan(At(80), odd, damage).PathCount != 1)
            throw new InvalidOperationException("Route walk must stop at cycles.");
        var json = """{"encounters":[{"room_type":"monster","total":1000,"fatal":10,"avg_damage":10,"characters":[{"character":"IRONCLAD","total":600,"fatal":3,"avg_damage":12}]},{"room_type":"monster","total":1000,"fatal":10,"avg_damage":8,"characters":[{"character":"IRONCLAD","total":100,"avg_damage":50}]},{"room_type":"elite","total":500,"fatal":40,"avg_damage":30}]}""";
        var parsed = RoutePlanner.ParseDamage(json, "CHARACTER.IRONCLAD");
        if (parsed.Monster != decimal.Round((12m * 600 + 8m * 1000) / 1600m, 1) || parsed.Elite != 30m || parsed.Boss != 0m)
            throw new InvalidOperationException("Encounter damage must weight fights and use large character rows only: " + parsed);
        var english = ModLocalization.Translate(string.Join("\n", RoutePlanner.Format(At(80), healthy), RoutePlanner.Format(At(27), low),
            RoutePlanner.Format(At(26), RoutePlanner.Plan(At(26), new RouteGraph([], new Dictionary<string, RouteNode>()), damage)),
            RoutePlanner.Label(nearFull.ByNode["R"]), "BUILD ADVISOR · маршрут", "★ путь",
            "Костёр: отдых +24 HP (HP 26 → 50).", "Костёр: HP 70 хватает — улучшение карты.",
            "Ожидаемый урон 28 (среднее Spire Codex для этого акта) · HP 80 → 52 · 35% текущего HP.", "Без боя."), AdvisorLanguage.English);
        if (HasCyrillic(english)) throw new InvalidOperationException("Route text is not translated: " + english);
    }

    // 6.22.0 (H2): the target is the closest developed community cluster, two close developed
    // clusters form a hybrid, readiness is the similarity, missing defining cards get a bonus.
    private static void ArchetypeDirectionUsesCommunityClusters()
    {
        ArchetypeEntity E(string id) => new(id, id.ToLowerInvariant());
        ArchetypeCluster C(string key, decimal similarity, decimal winRate, params string[] cards) =>
            new(key, key, similarity, 10m, winRate, cards.Select(E).ToArray(), [E("RELIC_" + key)]);
        var context = new RunContext("ironclad", ["STRIKE_IRONCLAD", "SHRUG_IT_OFF", "BODY_SLAM"], ["RELIC_BLOCK"], 0, 1, 6, 70, 80, 99, null, 1m, 0, 0, 0, []);
        var raw = C("RAW", 85m, .4m, "SHRUG_IT_OFF", "BODY_SLAM", "IRON_WAVE");
        var block = C("BLOCK", 78m, 46m, "BODY_SLAM", "BARRICADE", "ENTRENCH");
        var strength = C("STRENGTH", 74m, 50m, "INFLAME", "HEAVY_BLADE", "SPOT_WEAKNESS");
        var far = C("FAR", 60m, 70m, "DEMON_FORM", "LIMIT_BREAK", "REAPER");
        var direction = ArchetypeAdvisor.Choose(context, [raw, block, strength, far], true)!;
        if (direction.Primary.Key != "BLOCK" || direction.Secondary?.Key != "STRENGTH" || !direction.Name.Contains("×"))
            throw new InvalidOperationException("The closest developed cluster must win and a close developed one must form a hybrid.");
        if (direction.Missing.Any(x => x.Id == "BODY_SLAM") || !direction.Missing.Any(x => x.Id == "BARRICADE") || !direction.KeyCards.Contains("INFLAME"))
            throw new InvalidOperationException("Missing defining cards are wrong.");
        if (ArchetypeAdvisor.Choose(context, [raw, block, strength with { Similarity = 70m }], true)!.Secondary is not null ||
            ArchetypeAdvisor.Choose(context, [raw, far], true)!.Primary.Key != "RAW" || ArchetypeAdvisor.Choose(context, [raw, far], true)!.Target?.Key != "FAR" ||
            ArchetypeAdvisor.Choose(context, [raw, far with { Similarity = 45m }], true)!.Target is not null || ArchetypeAdvisor.Choose(context, [], true) is not null)
            throw new InvalidOperationException("Hybrids need a close partner; a far developed cluster must not replace the closest one.");
        var aimed = ArchetypeAdvisor.Choose(context, [raw, far], true)!;
        if (ArchetypeAdvisor.Bonus(aimed, "DEMON_FORM", context.Cards) != ArchetypeAdvisor.KeyCardBonus || aimed.KeyCards.Contains("IRON_WAVE") ||
            !ArchetypeAdvisor.Format(aimed, [raw, far]).Contains("Цель — ближайшее развитое направление: FAR"))
            throw new InvalidOperationException("An undeveloped closest cluster must aim at the closest developed one.");
        if (ArchetypeAdvisor.Bonus(ArchetypeAdvisor.Choose(context, [far with { Similarity = 40m }], true), "DEMON_FORM", context.Cards) != 0m)
            throw new InvalidOperationException("A distant target must not push its key cards.");
        if (ArchetypeAdvisor.Choose(context, [block with { Similarity = 57m }, strength with { Similarity = 55m }], true)!.Secondary is not null)
            throw new InvalidOperationException("A hybrid needs both clusters above the readiness minimum.");
        if (ArchetypeAdvisor.Bonus(direction, "BARRICADE", context.Cards) != ArchetypeAdvisor.KeyCardBonus || ArchetypeAdvisor.Bonus(direction, "BODY_SLAM", context.Cards) != 0m ||
            ArchetypeAdvisor.Bonus(direction, "ANGER", context.Cards) != 0m || ArchetypeAdvisor.Bonus(ArchetypeAdvisor.Choose(context, [raw], true), "IRON_WAVE", context.Cards) != 0m)
            throw new InvalidOperationException("Key card bonus must go only to missing defining cards of a developed target.");
        var local = ArchetypeAdvisor.LocalCandidates(context, [block, strength, C("BLOCK2", 0m, 30m, "SHRUG_IT_OFF", "BODY_SLAM", "X") with { Relics = [E("RELIC_BLOCK")] }]);
        if (local.Count != 2 || local[0].Key != "BLOCK2" || local[0].Similarity != 73.3m || local[1].Similarity != 46.7m)
            throw new InvalidOperationException("Local readiness must weigh defining cards 80% and relics 20%: " + string.Join(", ", local.Select(x => x.Key + " " + x.Similarity)));
        var catalog = new Dictionary<string, CardDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            ["BARRICADE"] = Card("BARRICADE", "Power", cost: 3, block: 0, rarity: "rare"),
            ["ANGER"] = Card("ANGER", "Attack", cost: 0, damage: 6, rarity: "common"),
        };
        var scored = DeckBuildScoring.Evaluate(context, ["BARRICADE", "ANGER"], catalog, new Dictionary<string, EntityMetric>(), null, null, direction).Offers;
        if (scored.First(x => x.Id == "BARRICADE").Adjustments?.Any(x => x.Label == "ключевая карта архетипа") != true ||
            scored.First(x => x.Id == "ANGER").Adjustments?.Any(x => x.Label == "ключевая карта архетипа") == true)
            throw new InvalidOperationException("Scoring must apply the key card bonus.");
        using (var doc = System.Text.Json.JsonDocument.Parse("""[{"key":"A+B","name":"A + B","similarity":83.0,"share":11.8,"win_rate":0.4,"defining_cards":[{"id":"A","name":"a"},{"id":"B","name":"b"}],"defining_relics":[{"id":"R","name":"r"}]},{"name":"empty","defining_cards":[]}]"""))
        {
            var parsed = ArchetypeAdvisor.ParseClusters(doc.RootElement);
            if (parsed.Count != 1 || parsed[0].Similarity != 83m || parsed[0].WinRate != .4m || parsed[0].Cards.Count != 2 || parsed[0].Relics[0].Id != "R")
                throw new InvalidOperationException("Cluster JSON was not parsed.");
        }
        if (ArchetypeAdvisor.ParseCatalog("""{"characters":{"IRONCLAD":[{"name":"X","defining_cards":[{"id":"A"}]}]}}""", "CHARACTER.IRONCLAD").Count != 1)
            throw new InvalidOperationException("Archetype catalogue was not parsed.");
        var english = ModLocalization.Translate(string.Join("\n", ArchetypeAdvisor.Format(direction, [raw, block, strength, far]),
            ArchetypeAdvisor.Format(ArchetypeAdvisor.Choose(context, [raw], false), [raw]), ArchetypeAdvisor.Format(aimed, [raw, far]), ArchetypeAdvisor.Format(null, [])), AdvisorLanguage.English);
        if (HasCyrillic(english.Replace("shrug_it_off", "").Replace("body_slam", ""))) throw new InvalidOperationException("Archetype text is not translated: " + english);
    }

    private static bool HasCyrillic(string text) => System.Text.RegularExpressions.Regex.IsMatch(text, "[А-Яа-яЁё]");

    private static CardDefinition Card(string id, string type, int cost = 1, int damage = 0, int block = 0, int draw = 0, int energy = 0, string rarity = "common") =>
        new(id, id, "test", type, cost, damage, block, draw, energy, rarity, "", new HashSet<string>(), null);
    private static IReadOnlyDictionary<string, CardDefinition> Cards(params (string Id, string Name, string Type, int Cost, int Damage, int Block, int Draw, int Energy)[] values) =>
        values.ToDictionary(x => SpireCodexMetricsClient.NormalizeId(x.Id), x => new CardDefinition(x.Id, x.Name, "ironclad", x.Type, x.Cost, x.Damage, x.Block, x.Draw, x.Energy, "common", "", new HashSet<string>(), null), StringComparer.OrdinalIgnoreCase);
    private static IReadOnlyDictionary<string, EntityMetric> Metrics(params string[] ids) => ids.ToDictionary(x => SpireCodexMetricsClient.NormalizeId(x), x => new EntityMetric(x, x, .5m, .55m, 100), StringComparer.OrdinalIgnoreCase);
}

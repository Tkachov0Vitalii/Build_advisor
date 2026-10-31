using BuildAdvisor.Data;

namespace BuildAdvisor.Integration;

internal sealed record AdvisorTag(string Key, string Label, string Category, string Description);

// Independent semantic vocabulary for Build Advisor. It deliberately uses observable card
// fields and conservative text rules, so an unknown/new card can still receive useful tags
// without importing another mod's card database or weights.
internal static class CardSemanticTags
{
    // Public semantic contracts seen in contemporary card advisers. Keeping the contract
    // explicit lets regression tests catch a lost mechanic when the game/API evolves.
    private static readonly string[] CompatibilityAxes = ("POISON_PRODUCER POISON POISON_CONSUMER AOE_POISON POISON_AMPLIFIER CUNNING_PRODUCER CUNNING CUNNING_CONSUMER DISCARD_PAYOFF DISCARD_TURN_PAYOFF EXHAUST_SELF EXHAUST_TAG EXHAUST_PRODUCER EXHAUST_BURST EXHAUST_DRAIN_PASSIVE EXHAUST_CONSUMER DOOM DOOM_PRODUCER DOOM_SELF_PRODUCER DOOM_CONSUMER AOE_DOOM DOOM_AMPLIFIER STAR STAR_PRODUCER STAR_CONSUMER STAR_BURST STAR_AMPLIFIER STAR_PAYOFF SOUL_PRODUCER SOUL_CONSUMER SKELETON_PRODUCER SKELETON_CONSUMER SKELETON_HP_AMPLIFIER SKELETON_ATTACK_AMPLIFIER SKELETON_AMPLIFIER ORB ORB_PRODUCER ORB_CONSUMER ORB_AMPLIFIER LIGHTNING_ORB LIGHTNING_EVOKE FROST_ORB FROST_PAYOFF DARK_ORB ORB_VARIETY_AMPLIFIER DARK_ORB_AMPLIFIER DARK_ORB_PRODUCER PLASMA_ORB GLASS_ORB ORB_EVOKE SLOT_EXPAND SLOT_COST VOLATILE_PRODUCER VOLATILE_CONSUMER VULN VULN_PRODUCER VULN_AMPLIFIER AOE_DEBUFF WEAK WEAK_PRODUCER WEAK_AMPLIFIER SCALING CARD_GEN CARD_GEN_PAYOFF BLOCK_SCALING STRENGTH_PRODUCER STRENGTH_CONSUMER DEXTERITY FOCUS VIGOR DAMAGE AOE_DAMAGE DAMAGE_AMPLIFIER HIT_2 HIT_3 HIT_4 HIT_5 MULTI_HIT_SCALING X_COST ENERGY_PRODUCER ENERGY ENERGY_CONSUMER DRAW_ON_DRAW DRAW_CONDITIONAL ABSENT_CONDITIONAL DRAW_AMPLIFIER STRIKE_CONSUMER STRIKE_ON_DRAW STRIKE_TYPE SHIV SHIV_PRODUCER SHIV_CONSUMER SHIV_AMPLIFIER FORGE FORGE_AMPLIFIER FORGE_PRODUCER LORDS_BLADE_ACCESS LORDS_BLADE_AMPLIFIER LORDS_BLADE_PAYOFF LORDS_BLADE_PRODUCER MINION DEFEND_TYPE_AMPLIFIER DEFEND_TYPE BLOCK ATTACK_REPLAY ATTACK_REPLAY_RANDOM SKILL_REPLAY BLOCK_PAYOFF BURST_FINISHER BLOCK_AMPLIFIER HP_LOSS HP_LOSS_CONSUMER HP_LOSS_PREVENT FOCUS_PENALTY STATUS_TO_HAND STATUS_PRODUCER STATUS_CONSUMER FREE_ATTACK_RETURNER FREE_ATTACK_RECLAIMER FREE_ATTACK_FILTER FREE_ATTACK POWER_CONDITIONAL POWER_COST_ENABLER POWER_AMPLIFIER POWER_TYPE SKILL_CONDITIONAL SKILL_COST_ENABLER SKILL_TYPE ATTACK_CONDITIONAL ATTACK_COST_ENABLER ATTACK_TYPE AOE ATTACK_COUNT DRAW_PILE_SEARCH FREE_ATTACK_PRODUCER REPEAT REPLAY SELF_RETURN SKILL_COUNT HEAL STRENGTH_DOWN CARD_RETURN EXHAUST_TARGET_RANDOM OSTY").Split(' ', StringSplitOptions.RemoveEmptyEntries);
    public static readonly IReadOnlyList<AdvisorTag> Reference = BuildReference();
    private static readonly IReadOnlyDictionary<string, AdvisorTag> ByKey = Reference.ToDictionary(x => x.Key, StringComparer.OrdinalIgnoreCase);

    public static string Describe(CardDefinition card, RunContext context, decimal final, decimal contextScore,
        IReadOnlyList<CardDefinition> deck, IReadOnlyList<string> archetypes)
    {
        var keys = Detect(card, context, final, contextScore, deck, archetypes);
        return string.Join(" · ", keys.Take(8).Select(key => ByKey.TryGetValue(key, out var tag) ? tag.Label : key));
    }

    // Some engines become more reliable with several copies of the same payoff/enabler.
    // This list is intentionally conservative: ordinary efficient cards still receive a
    // small duplicate tax, while explicitly stacking mechanics are allowed to repeat.
    internal static bool IsNaturallyRepeatable(CardDefinition card)
    {
        var text = (card.Id + " " + card.Description + " " + string.Join(' ', card.Tags)).ToUpperInvariant();
        return Has(text, "STACKABLE", "PERFECTED_STRIKE", "CLAW", "PRESSURE_POINTS", "SEARING_BLOW", "RAMPAGE", "ЗА КАЖДУЮ КОПИЮ", "ЗА КАЖДЫЙ УДАР") ||
               card.Tags.Any(x => Normalize(x) is "STACKABLE" or "DUPLICATE_OK");
    }

    internal static IReadOnlyList<string> Detect(CardDefinition card, RunContext context, decimal final, decimal contextScore,
        IReadOnlyList<CardDefinition> deck, IReadOnlyList<string> archetypes)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var text = (card.Id + " " + card.Description + " " + string.Join(' ', card.Tags)).ToUpperInvariant();
        void Add(string key, params string[] tokens) { if (tokens.Length == 0 || tokens.Any(text.Contains)) result.Add(key); }
        void Mechanic(string key, params string[] tokens)
        {
            if (!tokens.Any(text.Contains)) return;
            result.Add(key);
            if (Has(text, "GAIN", "APPLY", "ADD", "CHANNEL", "SUMMON", "CREATE", "ПОЛУЧ", "НАКЛАД", "ДОБАВ", "ПРИЗЫВ", "СОЗДА")) result.Add(key + "_PRODUCER");
            if (Has(text, "FOR EACH", "WHENEVER", "CONSUM", "REMOVE", "ЗА КАЖД", "КОГДА", "ПОТРЕБ", "СНИМА")) result.Add(key + "_CONSUMER");
            if (Has(text, "DOUBLE", "INCREASE", "MORE", "УДВО", "УВЕЛИЧ", "БОЛЬШЕ")) result.Add(key + "_AMPLIFIER");
        }

        if (card.Type.Contains("attack", StringComparison.OrdinalIgnoreCase)) result.Add("ATTACK");
        else if (card.Type.Contains("power", StringComparison.OrdinalIgnoreCase)) result.Add("POWER");
        else if (card.Type.Contains("skill", StringComparison.OrdinalIgnoreCase)) result.Add("SKILL");
        if (card.Block > 0) result.Add("BLOCK");
        if (card.Draw > 0) result.Add("DRAW");
        if (card.Energy > 0) result.Add("ENERGY_PRODUCER");
        if (card.Cost == 0) result.Add("ZERO_COST");
        if (card.Cost >= 3) result.Add("HEAVY");
        if (CardTaxonomy.RarityName(card.Rarity) == "Редкая") result.Add("RARE");

        Mechanic("POISON", "POISON", "ЯД", "ОТРАВ");
        Mechanic("SHIV", "SHIV", "ЗАТОЧК", "КЛИНОК");
        Mechanic("DISCARD", "DISCARD", "СБРОС", "СБРАСЫВ");
        Mechanic("CUNNING", "CUNNING", "ХИТРОСТ", "УЛОВК");
        Mechanic("EXHAUST", "EXHAUST", "ИСТОЩ", "РАСХОД");
        Mechanic("VULNERABLE", "VULNERABLE", "УЯЗВИМОСТ");
        Mechanic("WEAK", "WEAK", "СЛАБОСТ");
        Mechanic("DOOM", "DOOM", "ГИБЕЛ", "ПОГИБ");
        Mechanic("SOUL", "SOUL", "ДУШ");
        Mechanic("STAR", "STAR", "ЗВЕЗД", "ЗВЁЗД");
        Mechanic("FORGE", "FORGE", "КОВК", "КУЗН");
        if (RegentColorlessArchetype.IsMatch(card)) result.Add("COLORLESS_CREATION");
        if (RegentColorlessArchetype.IsGenerator(card)) { result.Add("CARD_GEN"); result.Add("COLORLESS_GENERATOR"); }
        if (RegentColorlessArchetype.IsPayoff(card)) { result.Add("CARD_GEN_PAYOFF"); result.Add("COLORLESS_PAYOFF"); }
        if (RegentColorlessArchetype.IsSupport(card)) result.Add("COLORLESS_CONTROL");
        Mechanic("MINION", "MINION", "SKELETON", "ПРИЗЫВ", "СКЕЛЕТ");
        Mechanic("ORB", "ORB", "СФЕР");
        Mechanic("VOLATILE", "VOLATILE", "НЕСТАБИЛ", "ЛЕТУЧ");
        Add("LIGHTNING", "LIGHTNING", "МОЛНИ"); Add("FROST", "FROST", "МОРОЗ", "ХОЛОД");
        Add("DARK", "DARK_ORB", "ТЬМА", "ТЕМН"); Add("PLASMA", "PLASMA", "ПЛАЗМ"); Add("GLASS", "GLASS_ORB", "СТЕКЛ");
        Mechanic("STRENGTH", "STRENGTH", "СИЛА"); Mechanic("DEXTERITY", "DEXTERITY", "ЛОВКОСТ"); Add("FOCUS", "FOCUS", "ФОКУС");
        Add("HP_LOSS", "HP_LOSS", "LOSE HP", "ПОТЕРЯЙТЕ", "ТЕРЯЕТЕ ОЗ"); Add("STATUS", "STATUS", "СТАТУС");
        Add("RETAIN", "RETAIN", "СОХРАН", "УДЕРЖ"); Add("ETHEREAL", "ETHEREAL", "БЕСПЛОТ");
        Add("RANDOM", "RANDOM", "СЛУЧАЙН"); Add("REPLAY", "REPLAY", "AGAIN", "ПОВТОР"); Add("RETURN", "RETURN", "ВОЗВРАТ");
        Add("INNATE", "INNATE", "ВРОЖД", "СТАРТОВ"); Add("X_COST", "X_COST", "X МАН", "X ЭНЕРГ");
        Add("AOE", "ALL ENEM", "ВСЕМ ВРАГ", "ВСЕХ ВРАГ", "AOE"); Add("DEBUFF", "DEBUFF", "ДЕБАФ");
        Add("FRAIL", "FRAIL", "ХРУПКОСТ"); Add("THORNS", "THORNS", "ОСТИ"); Add("HEAL", "HEAL", "ИСЦЕЛ", "ВОССТАНОВ");
        Add("CARD_GENERATION", "CREATE CARD", "ADD A CARD", "СОЗДАЙТЕ КАРТ", "ДОБАВЛЯЕТ КАРТ");
        Add("PILE_SEARCH", "SEARCH", "DRAW PILE", "НАЙДИТЕ", "СТОПКИ ДОБОРА");
        Add("MULTI_HIT", "TIMES", "РАЗА", "РАЗ ПО"); Add("SCALING", "EACH TURN", "PERMANENT", "КАЖДЫЙ ХОД", "НАВСЕГДА");
        Add("FINISHER", "FINISHER", "BODY_SLAM", "ALL_FOR_ONE", "FIEND_FIRE", "MEMENTO_MORI", "ФИНИШЕР");
        Add("LORDS_BLADE", "LORDS_BLADE", "КЛИНОК ЛОРДА"); Add("STRIKE", "STRIKE", "УДАР");

        foreach (var raw in card.Tags)
        {
            var normalized = Normalize(raw);
            if (ByKey.ContainsKey(normalized)) result.Add(normalized);
        }
        if (archetypes.Any(a => MatchesArchetype(text, a))) result.Add("ARCHETYPE_FIT");
        if (archetypes.Count >= 2 && archetypes.Count(a => MatchesArchetype(text, a)) >= 2) result.Add("BRIDGE");
        if (contextScore >= 20) result.Add("SHORE_UP");
        if (contextScore > 0) result.Add("SYNERGY"); else if (contextScore < 0) result.Add("OFF_SYNERGY");
        if (final >= 90) { result.Add("TOP_PICK"); result.Add("CORE"); }
        else if (final >= 78) result.Add("RECOMMENDED");
        else if (final >= 65) result.Add("SITUATIONAL");
        else result.Add("NOT_RECOMMENDED");
        var copies = deck.Count(x => SpireCodexMetricsClient.NormalizeId(x.Id) == SpireCodexMetricsClient.NormalizeId(card.Id));
        if (copies > 0 && IsNaturallyRepeatable(card)) { result.Add("STACKABLE"); result.Add("DUPLICATE_OK"); }
        else if (copies == 1) result.Add("DUPLICATE"); else if (copies == 2) result.Add("DUPLICATE_HEAVY"); else if (copies >= 3) result.Add("DUPLICATE_EXCESS");
        if (deck.Count >= 20 && contextScore <= 0) result.Add("BLOAT");
        if (card.Cost <= 1 && (card.Damage >= 8 || card.Block >= 8 || card.Draw >= 2)) result.Add("EFFICIENT");
        if (card.Cost >= 2 && card.Damage == 0 && card.Block == 0 && card.Draw == 0 && card.Energy == 0) result.Add("COMPLEX");
        if (context.Act == 1 && card.Damage > 0) result.Add("EARLY_VALUE");
        if (context.Act >= 2 && (result.Contains("SCALING") || card.Type.Contains("power", StringComparison.OrdinalIgnoreCase))) result.Add("LATE_VALUE");
        return result.OrderBy(TagPriority).ThenBy(x => x).ToArray();
    }

    private static int TagPriority(string key) => key switch
    {
        "TOP_PICK" or "CORE" or "SHORE_UP" => 0,
        "ARCHETYPE_FIT" or "BRIDGE" or "SYNERGY" => 1,
        "NOT_RECOMMENDED" or "OFF_SYNERGY" or "BLOAT" or "DUPLICATE_EXCESS" => 2,
        _ => 5
    };
    private static bool MatchesArchetype(string text, string archetype) => archetype switch
    {
        "Сила" => Has(text, "STRENGTH", "СИЛА"), "Истощение" => Has(text, "EXHAUST", "ИСТОЩ"), "Блок" => Has(text, "BLOCK", "БЛОК"),
        "Саморанение" => Has(text, "HP_LOSS", "ПОТЕРЯЙТЕ"), "Уязвимость" => Has(text, "VULNERABLE", "УЯЗВИМОСТ"),
        "Яд" => Has(text, "POISON", "ЯД"), "Заточки" => Has(text, "SHIV", "ЗАТОЧК"), "Сброс" => Has(text, "DISCARD", "СБРОС"),
        "Молнии" => Has(text, "LIGHTNING", "МОЛНИ"), "Мороз" => Has(text, "FROST", "МОРОЗ"), "Фокус" => Has(text, "FOCUS", "ФОКУС"),
        "Нулевая стоимость" => Has(text, "ZERO", "0 COST"), "Звёзды" => Has(text, "STAR", "ЗВЕЗД", "ЗВЁЗД"), "Кузня" => Has(text, "FORGE", "КОВК", "КУЗН"),
        "Бесцветные карты" => Has(text, "ARSENAL", "PILLAR_OF_CREATION", "MANIFEST_AUTHORITY", "BUNDLE_OF_JOY", "QUASAR", "SPECTRUM_SHIFT", "SUPERMASSIVE", "HEIRLOOM_HAMMER", "JACK_OF_ALL_TRADES", "BEGONE", "TYRANNY", "CHARGE", "GUARDS", "БЕСЦВЕТН"),
        "Души" => Has(text, "SOUL", "ДУШ"), "Гибель" => Has(text, "DOOM", "ГИБЕЛ"), "Призыв" => Has(text, "SUMMON", "MINION", "ПРИЗЫВ"), _ => false
    };
    private static string Normalize(string value) => value.Trim().Replace('-', '_').Replace(' ', '_').ToUpperInvariant();
    private static bool Has(string text, params string[] tokens) => tokens.Any(text.Contains);

    private static IReadOnlyList<AdvisorTag> BuildReference()
    {
        var tags = new List<AdvisorTag>();
        void Add(string category, string description, params (string Key, string Label)[] values)
        { tags.AddRange(values.Select(x => new AdvisorTag(x.Key, x.Label, category, description))); }
        Add("Базовые роли", "Непосредственная роль карты.", ("ATTACK", "Атака"), ("SKILL", "Навык"), ("POWER", "Талант"), ("BLOCK", "Блок"), ("DRAW", "Добор"), ("ENERGY_PRODUCER", "Энергия"), ("AOE", "По площади"), ("DEBUFF", "Ослабление"), ("HEAL", "Лечение"));
        Add("Структура", "Положение карты в плане колоды.", ("CORE", "Ядро"), ("KEYSTONE", "Ключевая карта"), ("FINISHER", "Финишер"), ("SUSTAIN", "Поддержание"), ("BRIDGE", "Мост архетипов"), ("ARCHETYPE_FIT", "В архетипе"), ("SYNERGY", "Синергия"), ("OFF_SYNERGY", "Вне синергии"), ("SHORE_UP", "Закрывает слабость"), ("FILLER", "Заполнитель"));
        Add("Вердикт", "Итоговая практическая пометка.", ("TOP_PICK", "Лучший выбор"), ("RECOMMENDED", "Рекомендуется"), ("SITUATIONAL", "Ситуативно"), ("NOT_RECOMMENDED", "Не рекомендуется"), ("EFFICIENT", "Эффективно"), ("INEFFICIENT", "Неэффективно"), ("STABLE", "Стабильно"), ("UNSTABLE", "Нестабильно"), ("COMPLEX", "Сложно разыгрывать"));
        Add("Кривая и размер", "Стоимость, повторяемость и влияние на размер колоды.", ("ZERO_COST", "Нулевая стоимость"), ("X_COST", "X-стоимость"), ("HEAVY", "Тяжёлая"), ("DUPLICATE", "Дубликат"), ("DUPLICATE_HEAVY", "Много копий"), ("DUPLICATE_EXCESS", "Перебор копий"), ("DUPLICATE_OK", "Полезная копия"), ("STACKABLE", "Можно накапливать"), ("BLOAT", "Размывает колоду"), ("DECK_SCALING", "Растёт с колодой"), ("ENERGY_CARRY", "Сохраняет энергию"));
        Add("Фаза", "Ценность относительно этапа забега.", ("TEMPO", "Темп"), ("EARLY_VALUE", "Ранняя ценность"), ("LATE_VALUE", "Поздняя ценность"), ("BURST", "Взрывной эффект"), ("SCALING", "Масштабирование"), ("HIGH_OUTPUT", "Высокая отдача"), ("LOW_OUTPUT", "Низкая отдача"));
        Add("Свойства", "Особые правила розыгрыша.", ("RARE", "Редкая"), ("STARTER", "Стартовая"), ("INNATE", "Врождённая"), ("RETAIN", "Сохранение"), ("ETHEREAL", "Бесплотная"), ("EXHAUST", "Истощение"), ("RANDOM", "Случайность"), ("REPLAY", "Повторный розыгрыш"), ("RETURN", "Возврат"), ("PILE_SEARCH", "Поиск в стопке"), ("CARD_GENERATION", "Создание карт"), ("STATUS", "Статусы"), ("MULTI_HIT", "Много ударов"));
        Add("Ослабления", "Наложение или использование отрицательного эффекта.", ("VULNERABLE", "Уязвимость"), ("WEAK", "Слабость"), ("FRAIL", "Хрупкость"), ("POISON", "Яд"));
        Add("Архетипы", "Основные механические направления персонажей.", ("SHIV", "Заточки"), ("DISCARD", "Сброс"), ("CUNNING", "Хитрость"), ("STRENGTH", "Сила"), ("DEXTERITY", "Ловкость"), ("HP_LOSS", "Саморанение"), ("DOOM", "Гибель"), ("SOUL", "Души"), ("MINION", "Призыв"), ("STAR", "Звёзды"), ("FORGE", "Ковка"), ("COLORLESS_CREATION", "Бесцветное создание"), ("COLORLESS_GENERATOR", "Генератор бесцветных"), ("COLORLESS_PAYOFF", "Награда за создание"), ("COLORLESS_CONTROL", "Контроль случайных карт"), ("VOLATILE", "Нестабильность"), ("THORNS", "Ости"), ("LORDS_BLADE", "Клинок лорда"), ("STRIKE", "Удары"));
        Add("Сферы", "Создание, расход и усиление сфер.", ("ORB", "Сферы"), ("LIGHTNING", "Молния"), ("FROST", "Мороз"), ("DARK", "Тьма"), ("PLASMA", "Плазма"), ("GLASS", "Стекло"), ("FOCUS", "Фокус"), ("SLOT_EXPAND", "Ячейки сфер"), ("ORB_EVOKE", "Активация сфер"));
        foreach (var mechanic in new[] { "POISON", "SHIV", "DISCARD", "CUNNING", "EXHAUST", "VULNERABLE", "WEAK", "DOOM", "SOUL", "STAR", "FORGE", "MINION", "ORB", "VOLATILE", "STRENGTH", "DEXTERITY" })
        {
            var label = tags.First(x => x.Key == mechanic).Label;
            tags.Add(new AdvisorTag(mechanic + "_PRODUCER", "Генератор: " + label, "Связи", "Создаёт ресурс или эффект."));
            tags.Add(new AdvisorTag(mechanic + "_CONSUMER", "Потребитель: " + label, "Связи", "Получает выгоду от ресурса или эффекта."));
            tags.Add(new AdvisorTag(mechanic + "_AMPLIFIER", "Усилитель: " + label, "Связи", "Увеличивает силу ресурса или эффекта."));
        }
        foreach (var key in CompatibilityAxes.Where(key => tags.All(tag => !string.Equals(tag.Key, key, StringComparison.OrdinalIgnoreCase))))
            tags.Add(new AdvisorTag(key, Humanize(key, tags), "Расширенные оси", "Точная связь механики: генерация, потребление, усиление, условие или награда."));
        return tags.GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase).Select(x => x.First()).ToArray();
    }

    private static string Humanize(string key, IReadOnlyList<AdvisorTag> known)
    {
        var suffixes = new (string Suffix, string Prefix)[]
        {
            ("_PRODUCER", "Генератор"), ("_CONSUMER", "Потребитель"), ("_AMPLIFIER", "Усилитель"),
            ("_PAYOFF", "Награда"), ("_CONDITIONAL", "Условие"), ("_COST_ENABLER", "Снижение стоимости"),
            ("_TYPE", "Тип"), ("_REPLAY", "Повтор"), ("_SCALING", "Масштабирование")
        };
        foreach (var (suffix, prefix) in suffixes)
            if (key.EndsWith(suffix, StringComparison.Ordinal))
            {
                var root = key[..^suffix.Length];
                var rootLabel = known.FirstOrDefault(x => x.Key == root)?.Label ?? root.Replace('_', ' ').ToLowerInvariant();
                return prefix + ": " + rootLabel;
            }
        return key.Replace('_', ' ').ToLowerInvariant();
    }

    internal static bool Supports(string key) => ByKey.ContainsKey(key);
    internal static IReadOnlyList<string> RequiredCompatibilityAxes => CompatibilityAxes;
}

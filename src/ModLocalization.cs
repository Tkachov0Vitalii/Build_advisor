using System.Text.Json;
using Godot;

namespace BuildAdvisor;

internal enum AdvisorLanguage { Russian, English }

internal static class ModPaths
{
    public static string UserData(params string[] parts)
    {
        // SpecialFolder is resolved by .NET on every supported OS and also keeps the
        // scoring model testable outside a running Godot process.
        var root = System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root)) root = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), ".local", "share");
        var path = Path.Combine(root, "BuildAdvisor");
        foreach (var part in parts) path = Path.Combine(path, part);
        return path;
    }
}

internal static class ModLocalization
{
    private sealed record Settings(string Language);
    private static readonly string SettingsPath = ModPaths.UserData("settings.json");
    private static AdvisorLanguage current = Load();

    public static AdvisorLanguage Current => current;
    public static string ApiLanguage => current == AdvisorLanguage.English ? "eng" : "rus";
    public static event Action? Changed;

    // Longer phrases are replaced first so a short word cannot damage a complete translation.
    private static readonly (string Ru, string En)[] English =
    [
        // 6.18.0 take/skip scale and offline mode
        ("п.п. к победе против пропуска", "pp of win rate vs skipping"),
        ("Игроки в похожих ситуациях пропускали награду в", "Players in similar situations skipped this reward in"),
        ("% случаев", "% of cases"),
        ("Взять или пропустить", "Take or skip"),
        ("нужно не меньше", "needs at least"),
        ("лучшая карта", "best card"),
        ("п.п. к победе", "pp of win rate"),
        ("уверенность высокая", "evidence high"),
        ("уверенность средняя", "evidence medium"),
        ("уверенность низкая", "evidence low"),
        ("п.п.", "pp"),
        // 6.21.0 untranslated older texts found by a sweep of every string in src
        ("выборы в похожих колодах", "picks in similar decks"),
        ("пока недостаточно данных для общей оценки", "not enough data for an overall score yet"),
        ("общая оценка", "overall score"),
        ("реликвия с прямой синергией", "relic with direct synergy"),
        ("удаление базовой карты, если оно не лишает следующую важную покупку", "removing a starter card if it does not cost the next important purchase"),
        ("зелье перед элитой/боссом", "a potion before an elite/boss"),
        ("уже копий:", "copies already:"),
        ("В похожих билдах результат слабее", "Weaker result in similar builds"),
        ("ВЫСОКИЙ ПРИОРИТЕТ: дополнительная энергия разгружает дорогую колоду; карты других цветов — осознанный риск", "HIGH PRIORITY: extra energy relieves an expensive deck; off-color cards are a conscious risk"),
        ("Ветка события", "Event option"),
        ("Карты не прочитаны.", "Cards were not read."),
        ("Колода не прочитана — показана общая оценка карт.", "The deck was not read; overall card scores are shown."),
        ("Контекст колоды:", "Deck context:"),
        ("навыки/добор", "skills/draw"),
        ("Локальный анализ магазина: состояние забега не прочитано.", "Local shop analysis: the run state was not read."),
        ("Локальный анализ реликвий: состояние забега пока не прочитано.", "Local relic analysis: the run state has not been read yet."),
        ("Локальный анализ: состояние забега пока не прочитано.", "Local analysis: the run state has not been read yet."),
        ("Локальный совет по вариантам:", "Local advice on the options:"),
        ("Локальный совет по реликвиям:", "Local relic advice:"),
        ("Недостаточно похожих ранов", "Not enough similar runs"),
        ("Нейтральный вариант", "Neutral option"),
        ("Нет активного забега", "No active run"),
        ("Ожидание данных забега…", "Waiting for run data…"),
        ("Поправка с учётом текущей колоды:", "Adjustment for the current deck:"),
        ("Приоритет покупки: 1) закрыть", "Purchase priority: 1) cover"),
        ("Проверьте точные HP/золото/проклятия в тексте события: для таких веток общая статистика не является доказательством лучшего выбора.", "Check the exact HP/gold/curses in the event text: for such options general statistics do not prove the best choice."),
        ("Пропуск: разумен, если ни один вариант не закрывает текущую потребность или только размывает колоду.", "Skip: reasonable if no option covers the current need or it only dilutes the deck."),
        ("Слабый сигнал — мало выборок", "Weak signal: few samples"),
        ("Состояние забега не прочитано.", "The run state was not read."),
        ("признаков", "features"),
        ("Часто усиливает похожий билд", "Often strengthens a similar build"),
        ("берите только если награда окупает проклятие и есть способ удалить его", "take only if the reward pays for the curse and you can remove it"),
        ("высокий приоритет, если в колоде есть дорогие карты; проверьте цену/негативный эффект", "high priority if the deck has expensive cards; check the price/drawback"),
        ("данных достаточно", "enough data"),
        ("данных мало", "little data"),
        ("данных много", "plenty of data"),
        ("даёт быстрый урон для Act 1 и элит", "gives fast damage for Act 1 and elites"),
        ("даёт темп, если есть дорогие карты", "gives tempo if there are expensive cards"),
        ("закрывает дефицит защиты", "covers the defense gap"),
        ("блока)", "block)"),
        ("закрывает защиту и снижает риск перед элитами", "covers defense and lowers the risk before elites"),
        ("золото особенно ценно перед ближайшим магазином", "gold is especially valuable before the next shop"),
        ("маршрут и ближайшая угроза пока не прочитаны", "route and nearest threat have not been read yet"),
        ("не распознано", "not recognized"),
        ("нет данных", "no data"),
        ("низкий HP: избегайте риска без сильной компенсации", "low HP: avoid risk without strong compensation"),
        ("оцените немедленную цену и то, закрывает ли награда текущую потребность", "weigh the immediate cost and whether the reward covers the current need"),
        ("повышает стабильность розыгрыша ключевых карт", "makes key cards more consistent to play"),
        ("полезна при нехватке блока и низком HP", "useful when short on block and at low HP"),
        ("помогает с затяжными боями и боссом", "helps in long fights and against the boss"),
        ("предел", "cap"),
        ("рекомендуется, если улучшение «Вмятина → Пролом» даёт повторное наложение Уязвимости; особенно ценно при уже собранном направлении Уязвимости", "recommended if the Dent → Breach upgrade reapplies Vulnerable; especially valuable when the deck already builds around Vulnerable"),
        ("роль не определена по ID — оценивайте текст карты вручную", "role not determined by ID; judge the card text yourself"),
        ("сильнее до магазина или при ограниченном золоте", "stronger before a shop or when gold is short"),
        ("синергия/качество", "synergy/quality"),
        ("ситуативно: повторный выбор награды полезен, когда колода уже ищет конкретные карты; не закрывает дефицит немедленно", "situational: rerolling a reward helps when the deck already looks for specific cards; does not cover a gap right away"),
        ("смешанный билд", "mixed build"),
        ("сравните непосредственный эффект с ближайшей проблемой колоды", "compare the immediate effect with the deck's nearest problem"),
        ("сравните с ближайшей элитой", "compare with the nearest elite"),
        ("сравните с потерей HP и альтернативной наградой", "compare with the HP loss and the alternative reward"),
        ("средний приоритет: энергия сильна, но карты других цветов могут размыть уже выбранный архетип", "medium priority: energy is strong, but off-color cards can dilute the chosen archetype"),
        ("усиливает добор и стабильность ключевых ходов", "improves draw and the consistency of key turns"),
        ("ценность зависит от синергии и возможной цены", "value depends on synergy and the possible cost"),
        ("высокая", "high"),
        ("средняя", "medium"),
        ("низкая", "low"),
        ("⚠ смерть", "⚠ lethal"),
        // 6.22.0 archetypes
        ("Архетип сообщества: колода пока не похожа ни на один кластер.", "Community archetype: the deck does not resemble any cluster yet."),
        ("Архетип сообщества (офлайн, по ключевым картам):", "Community archetype (offline, by key cards):"),
        ("(такие колоды редко доживают до победы)", "(such decks rarely make it to a win)"),
        ("Архетип сообщества:", "Community archetype:"),
        ("Другие направления:", "Other directions:"),
        ("Развитого направления рядом нет: берите карты по потребностям колоды.", "No developed direction is close: take cards by deck needs."),
        ("Цель — ближайшее развитое направление:", "Target, the closest developed direction:"),
        ("ключевая карта архетипа", "archetype key card"),
        ("готовность", "readiness"),
        ("Уже есть:", "Already have:"),
        ("Ищите:", "Look for:"),
        ("реликвии:", "relics:"),
        ("гибрид", "hybrid"),
        ("доля", "share"),
        // 6.22.0 route
        ("Метки на карте: ожидаемый урон и HP после узла по средним данным Spire Codex для этого акта; у костров — отдых или улучшение. Это средние значения: сильная колода теряет меньше.", "Map labels: expected damage and HP after the node from Spire Codex averages for this act; campfires show rest or upgrade. These are averages: a strong deck loses less."),
        ("· ⚠ по средним потерям HP не хватает", "· ⚠ not enough HP at average damage"),
        ("(среднее Spire Codex для этого акта)", "(Spire Codex average for this act)"),
        ("хватает — улучшение карты.", "is enough: upgrade a card."),
        ("текущего HP.", "of current HP."),
        ("Опасные бои на пути:", "Dangerous fights on the path:"),
        ("Другие первые шаги:", "Other first steps:"),
        ("Маршрут до босса", "Route to the boss"),
        ("проверено путей:", "paths checked:"),
        ("Рекомендуемый:", "Recommended:"),
        ("Маршрут не прочитан.", "The route was not read."),
        ("Ожидаемый урон", "Expected damage"),
        ("HP к боссу", "HP at the boss"),
        ("Костёр: отдых", "Campfire: rest"),
        ("Костёр: HP", "Campfire: HP"),
        ("BUILD ADVISOR · маршрут", "BUILD ADVISOR · route"),
        ("ценность", "value"),
        ("(ценность ниже на", "(value lower by"),
        ("Без боя.", "No fight."),
        ("бой", "fight"), ("элита", "elite"), ("магазин", "shop"), ("сундук", "chest"), ("босс", "boss"), ("Древний", "Ancient"),
        ("улучш.", "upgrade"), ("путь", "path"),
        // 6.21.0 run review
        ("— это оценка на момент выбора, а не причина исхода.", "— an estimate at the time of the choice, not the cause of the result."),
        ("поражений", "losses"),
        ("Сравнение побед и поражений появится после 3 побед и 3 поражений с записанными советами", "The wins vs losses comparison appears after 3 wins and 3 losses with recorded advice"),
        ("Выборы не уступали совету по оценке мода.", "No choice fell short of the advice by the mod's estimate."),
        ("Выборов карт в этом забеге не записано.", "No card choices were recorded for this run."),
        ("По вашим забегам: совпадение с советом в победах", "Across your runs: agreement with advice in wins"),
        ("в поражениях", "in losses"),
        ("Всего по оценке мода:", "Total by the mod's estimate:"),
        ("Крупнейшие расхождения:", "Largest divergences:"),
        ("Наград с картами:", "Card rewards:"),
        ("совпало с советом:", "matched the advice:"),
        ("Записано забегов:", "Runs recorded:"),
        ("средний этаж", "average floor"),
        ("Разбор забега:", "Run review:"),
        ("BUILD ADVISOR · итог", "BUILD ADVISOR · summary"),
        ("Разбор забега: поражение", "Run review: defeat"),
        ("Разбор забега: победа", "Run review: victory"),
        ("сейчас побед", "now wins"),
        ("· побед ", "· wins "),
        ("пропуск вместо", "skip instead of"),
        ("вместо пропуск (", "instead of skip ("),
        ("пропусков", "skips"),
        ("вместо", "instead of"),
        // 6.21.0 relics
        ("Лифт реликвий смещён длиной забега (поздние реликвии получают выжившие), но при выборе из нескольких реликвий смещение у всех одинаковое.", "Relic lift is biased by run length (survivors get late relics), but among relics offered together the bias is shared."),
        ("Статистика сообщества недоступна — оценка по ролям реликвий и колоде.", "Community statistics are unavailable; scores use relic roles and the deck."),
        ("Разница меньше 0,5 п.п.: выбирайте по плану колоды.", "The difference is under 0.5 pp: choose by your deck plan."),
        ("в последнем акте осталось мало магазинов", "few shops remain in the last act"),
        ("защищает перед опасным участком", "protects before a dangerous stretch"),
        ("к следующей", "over the next one"),
        ("Разбор лучшей:", "Best relic breakdown:"),
        ("Реликвии не прочитаны.", "Relics were not read."),
        ("средняя стоимость", "average cost"),
        ("выживаемость", "survival"),
        ("экономика", "economy"),
        // 6.21.0 campfire
        ("Отдыхают в основном при низком HP, поэтому его лифт отрицательный; совет выше учитывает ваше HP и риск.", "Players mostly rest at low HP, so resting shows a negative lift; the advice above accounts for your HP and risk."),
        ("Совет: отдых и улучшение недоступны — сравните особые варианты по описанию.", "Advice: rest and upgrade are unavailable; compare the special options by their text."),
        ("— HP ниже трети, следующий тяжёлый бой опасен.", "— HP is below a third, the next hard fight is dangerous."),
        ("Подходящей карты для улучшения не найдено.", "No suitable card to upgrade was found."),
        ("Лучшее улучшение, если HP хватает:", "Best upgrade if HP allows:"),
        ("Статистика сообщества (справочно):", "Community statistics (for reference):"),
        ("балла против", "points vs"),
        ("у улучшения", "for upgrading"),
        ("у отдыха", "for resting"),
        ("Совет: отдых", "Advice: rest"),
        ("Совет: улучшить", "Advice: upgrade"),
        ("отдых восстановит", "rest restores"),
        ("риск следующих узлов", "risk of the next nodes"),
        ("HP не прочитано", "HP not read"),
        ("при низком HP", "at low HP"),
        ("BUILD ADVISOR · костёр", "BUILD ADVISOR · campfire"),
        ("Отдохнуть", "Rest"), ("Лечить", "Mend"), ("Копать", "Dig"), ("Тренироваться", "Train"), ("Высидеть", "Hatch"),
        ("Удвоить", "Clone"), ("Приготовить", "Cook"), ("Ковать", "Smith"), ("Раздуть пламя", "Kindle"),
        // 6.20.0 events, potions, score breakdown
        ("Лифт — насколько винрейт забегов, выбравших вариант, выше или ниже среднего по данным Spire Codex. Это корреляция: проверьте цену варианта (HP, золото, проклятия) для своей колоды.", "Lift shows how much higher or lower the win rate of runs that chose this option is, per Spire Codex. It is a correlation: check the option's cost (HP, gold, curses) for your deck."),
        ("Статистики по этому событию нет — показаны локальные подсказки.", "No statistics for this event; local hints are shown."),
        ("Ветки события:", "Event options:"),
        ("винрейт", "win rate"),
        ("выбирают", "chosen by"),
        ("реликвия", "relic"),
        ("брать: впереди опасный участок", "take: a dangerous stretch is ahead"),
        ("брать: есть свободный слот", "take: a slot is free"),
        ("можно пропустить: слабое зелье", "can skip: weak potion"),
        ("слоты заняты: не лучше текущих зелий", "slots full: not better than held potions"),
        ("заменить", "replace"),
        ("используют", "used in"),
        ("статистики нет", "no statistics"),
        ("Слоты зелий не прочитаны.", "Potion slots were not read."),
        ("Слоты зелий:", "Potion slots:"),
        ("BUILD ADVISOR · награды", "BUILD ADVISOR · rewards"),
        ("Зелья", "Potions"),
        ("База (статистика сообщества):", "Baseline (community statistics):"),
        ("Потребности колоды", "Deck needs"),
        ("Архетип и синергия", "Archetype and synergy"),
        ("Копии и размер колоды", "Copies and deck size"),
        ("Акт и босс", "Act and boss"),
        ("Улучшение", "Upgrade"),
        ("Личное и похожие ситуации", "Personal and similar situations"),
        ("Прочее", "Other"),
        ("Граница шкалы и округление:", "Scale limit and rounding:"),
        ("Итог:", "Result:"),
        ("п.п. к победе против пропуска", "pp of win rate vs skipping"),
        ("Лифт сообщества:", "Community lift:"),
        ("п.п. по", "pp over"),
        ("выборам", "picks"),
        ("ограничение другого архетипа", "other archetype cap"),
        ("похожие ситуации", "similar situations"),
        ("Статистика сообщества недоступна — оценка по каталогу карт и локальной модели.", "Community statistics are unavailable; scores use the card catalogue and the local model."),
        ("ваша поправка из tier-overrides.json", "your tier-overrides.json correction"),
        ("ваша поправка", "your correction"),
        ("улучшенная версия", "upgraded version"),
        ("⚠ СМЕРТЕЛЬНО — этот вариант убьёт персонажа: ", "⚠ LETHAL — this option will kill the character: "),
        ("Офлайн-режим: мод не обращается к сети и использует сохранённую статистику и локальную модель. Pick Coach и загрузка картинок отключаются.", "Offline mode: the mod makes no network requests and uses saved statistics and the local model. Pick Coach and image downloads are turned off."),
        ("Автовыбор", "Auto-pick"),
        ("Офлайн", "Offline"),
        ("после покупки останется", "left after buying:"),
        ("не хватает", "short by"),
        ("цена", "price"),
        ("Автовыбор на экранах событий отключён: мод не подтверждает выбор и не мешает выбрать несколько карт.", "Auto-pick is disabled on event screens: the mod does not confirm the choice or interfere with selecting multiple cards."),
        ("Высокая оценка означает лучший выбор именно для этой цели.", "A higher score means a better choice for this specific event action."),
        ("чем выше оценка, тем выгоднее преобразовать карту", "a higher score means this card is a better transformation target"),
        ("чем выше оценка, тем выгоднее заменить карту", "a higher score means this card is a better replacement target"),
        ("чем выше оценка, тем выгоднее удалить карту", "a higher score means this card is a better removal target"),
        ("приоритет улучшения без увеличения колоды", "upgrade priority without increasing deck size"),
        ("ценность усиления выбранной карты", "value of improving the selected card"),
        ("BUILD ADVISOR · выбор в событии", "BUILD ADVISOR · event card choice"),
        ("Выберите одну карту.", "Choose one card."),
        ("Выберите от", "Choose from"),
        ("до", "to"),
        ("карт.", "cards."),
        ("получение карт", "gain cards"),
        ("выбор карты из колоды", "choose a card from the deck"),
        ("преобразование", "transformation"),
        ("зачарование", "enchantment"),
        ("удаление", "removal"),
        ("Цель:", "Goal:"),
        ("Событие", "Event"),
        ("Набор", "Bundle"),
        ("Автоматически берёт карту только при высокой уверенности, явном преимуществе и когда пропуск хуже. Не выбирает спорные варианты.", "Automatically picks a card only with high confidence, a clear lead, and when skipping is worse. Ambiguous choices are never auto-picked."),
        ("Текущие рекомендации автоматически заменятся при появлении новой награды, события или магазина.", "The recommendations will update automatically when a new reward, event, or shop appears."),
        ("Список учитывает вашу колоду и выбранный архетип. Измените архетип сверху и нажмите «Обновить».", "The list reflects your deck and selected archetype. Change the archetype above and press Refresh."),
        ("Личный профиль: данных пока нет — он начнёт работать после нескольких выборов.", "Personal profile: no data yet — it will start working after several choices."),
        ("Если сеть недоступна, совет будет рассчитан по локальной модели.", "If the network is unavailable, advice will be calculated by the local model."),
        ("Направление колоды: вручную или автоматически", "Deck direction: manual or automatic"),
        ("Прочитать текущее состояние забега заново", "Read the current run state again"),
        ("Каталог карт загружается…", "Loading card catalog…"),
        ("Ожидание данных текущего забега…", "Waiting for current run data…"),
        ("Загрузка общей статистики карт…", "Loading aggregate card statistics…"),
        ("Нет карт с такими фильтрами.", "No cards match these filters."),
        ("Автовыбор лучшей карты", "Auto-pick best card"),
        ("Карты для поиска", "Card Search"),
        ("Анализ забега", "Run Analysis"),
        ("Искать карты", "Card Search"),
        ("Оценка вариантов", "Card options"),
        ("BUILD ADVISOR · карты", "BUILD ADVISOR · cards"),
        ("План следующих действий", "Next-action plan"),
        ("Вероятность пропуска", "Skip probability"),
        ("похожие решения чаще указывают на пропуск", "similar decisions more often indicate skipping"),
        ("похожие решения чаще указывают на выбор карты", "similar decisions more often indicate taking a card"),
        ("похожие ситуации", "similar situations"),
        ("Уверенность", "Confidence"),
        ("уверенный автовыбор", "confident auto-pick"),
        ("умеренная уверенность", "moderate confidence"),
        ("спорный выбор", "uncertain choice"),
        ("нет оценки", "not evaluated"),
        ("варианты не прочитаны", "options were not read"),
        ("мало данных", "limited data"),
        ("совет опирается в основном на локальную модель", "advice relies mainly on the local model"),
        ("варианты близки или пропуск остаётся конкурентным", "options are close or skipping remains competitive"),
        ("преимущество лучшего варианта", "best-option lead"),
        ("диапазон модели", "model range"),
        ("Сравнение действий", "Action comparison"),
        ("сохранить золото", "save gold"),
        ("не покупать карту", "do not buy a card"),
        ("купить", "buy"),
        ("костёр", "campfire"),
        ("отдых", "rest"),
        ("улучшение", "upgrade"),
        ("Риск следующих 3 узлов", "Risk over the next 3 nodes"),
        ("Прогноз ближайших 3 узлов", "Next-3-node forecast"),
        ("диапазон", "range"),
        ("уверенность", "confidence"),
        ("калиброванная", "calibrated"),
        ("ограниченная", "limited"),
        ("маршрут не выбран", "route not selected"),
        ("модель недоступна", "model unavailable"),
        ("Варианты", "Routes"),
        ("Следующий узел", "Next node"),
        ("Маршрут", "Route"),
        ("босс ещё неизвестен", "boss unknown"),
        ("маршрут прочитан; профиль босса ещё неизвестен", "route read; boss profile is still unknown"),
        ("Приоритеты", "Priorities"),
        ("Ближайший босс", "Upcoming boss"),
        ("Ближайшая угроза", "Nearest threat"),
        ("бой с несколькими целями требует темпа и устойчивости", "a multi-target fight requires tempo and resilience"),
        ("затяжной бой проверяет рост силы и стабильность колоды", "a long fight tests scaling and deck consistency"),
        ("помехи и давление требуют стабильных рук", "disruption and pressure require consistent hands"),
        ("универсальная подготовка к неизвестному профилю боя", "general preparation for an unknown fight profile"),
        ("массовый урон", "area damage"),
        ("не меняет колоду", "does not change the deck"),
        ("ценность карты относительно пропуска", "card value relative to skipping"),
        ("улучшает качество добора", "improves draw quality"),
        ("усиливает существующую карту без увеличения колоды", "improves an existing card without growing the deck"),
        ("снижает риск ближайшего участка", "reduces near-term risk"),
        ("сохраняет будущую гибкость", "preserves future flexibility"),
        ("вероятность пропуска", "skip probability"),
        ("оценка", "score"),
        ("отрыв", "lead"),
        ("нет очевидной цели", "no clear target"),
        ("решать по ближайшему бою", "decide based on the next fight"),
        ("сохранять золото на удаление слабой карты", "save gold to remove a weak card"),
        ("сохранять золото, если покупка не закрывает явную потребность", "save gold unless a purchase covers a clear need"),
        ("награда не прочитана", "reward was not read"),
        ("улучшить", "upgrade"),
        ("удалить", "remove"),
        ("взять", "take"),
        ("Личный профиль", "Personal profile"),
        ("Совет полезен", "Advice was useful"),
        ("Совет сомнителен", "Advice was questionable"),
        ("Любая редкость", "Any rarity"),
        ("Любая метка", "Any tier"),
        ("Все типы", "All types"),
        ("По оценке", "By score"),
        ("По стоимости", "By cost"),
        ("По названию", "By name"),
        ("Необычная", "Uncommon"),
        ("Обычная", "Common"),
        ("Редкая", "Rare"),
        ("Базовая", "Basic"),
        ("Особая", "Special"),
        ("Неизвестная", "Unknown"),
        ("Контекст не определён", "Context not identified"),
        ("Направление", "Direction"),
        ("Пропустить", "Skip"),
        ("пропустить", "skip"),
        ("Сейчас нужно", "Current need"),
        ("Сейчас важнее", "Current priority"),
        ("Колода", "Deck"),
        ("Реликвии", "Relics"),
        ("Реликвий", "relics"),
        ("реликвий", "relics"),
        ("Карты", "Cards"),
        ("карты", "cards"),
        ("карт", "cards"),
        ("Магазин", "Shop"),
        ("магазин", "shop"),
        ("Событие", "Event"),
        ("событие", "event"),
        ("стоимость", "cost"),
        ("золото", "gold"),
        ("этаж", "floor"),
        ("Архетип", "Archetype"),
        ("архетип", "archetype"),
        ("Выживаемость", "Survivability"),
        ("Масштабирование", "Scaling"),
        ("Стабильность", "Consistency"),
        ("Экономика", "Economy"),
        ("Темп", "Tempo"),
        ("Совет", "Advice"),
        ("Метки", "Tags"),
        ("Закрыть", "Close"),
        ("Обновить", "Refresh"),
        ("Найдено", "Found"),
        ("Атака", "Attack"),
        ("Навык", "Skill"),
        ("Талант", "Power"),
        ("Авто", "Auto"),
        ("Сила", "Strength"),
        ("Истощение", "Exhaust"),
        ("Саморанение", "Self-damage"),
        ("Уязвимость", "Vulnerable"),
        ("Блок", "Block"),
        ("Яд", "Poison"),
        ("Заточки", "Shivs"),
        ("Сброс", "Discard"),
        ("Молнии", "Lightning"),
        ("Мороз", "Frost"),
        ("Фокус", "Focus"),
        ("Нулевая стоимость", "Zero-cost"),
        ("Бесцветные карты", "Colorless Cards"),
        ("Звёзды", "Stars"),
        ("Кузня", "Forge"),
        ("Души", "Souls"),
        ("Гибель", "Doom"),
        ("Призыв", "Summons"),
        ("Синергия", "Synergy"),
        ("Дубликат", "Duplicate"),
        ("Большая колода", "Large deck"),
        ("Поздняя ценность", "Late-game value"),
        ("Лучший выбор", "Best choice"),
        ("Не рекомендуется", "Not recommended"),
        ("Закрывает слабость", "Covers a weakness"),
        ("быстрый урон", "front-loaded damage"),
        ("защита", "defense"),
        ("добор", "draw"),
        ("энергия", "energy"),
        ("урон", "damage"),
        ("уверенность", "confidence"),
        ("победы", "wins"),
        ("данные", "data"),
        ("обновлено вручную", "manually refreshed")
        ,("Сортировка: приоритет", "Sort: priority")
        ,("Сортировка: польза колоде", "Sort: deck value")
        ,("Сортировка: надёжность", "Sort: confidence")
        ,("Сортировка: редкость", "Sort: rarity")
        ,("Текущий забег ещё не прочитан. Закройте окно, откройте карту и повторите попытку.", "The current run has not been read yet. Close the window, open the map, and try again.")
        ,("Не удалось открыть каталог", "Could not open the catalog")
        ,("Нажмите «Искать карты», чтобы повторить.", "Press Card Search to try again.")
        ,("Справочник меток", "Tag Reference")
        ,("Поиск по названию или описанию…", "Search by name or description…")
        ,("Все категории", "All categories")
        ,("История изменений пока пуста.", "No recent changes yet.")
        ,("Последние изменения", "Recent changes")
        ,("Оценки — диагностические ориентиры, а не гарантия исхода боя. Низкий показатель показывает, что именно стоит исправлять следующей наградой или покупкой.", "Scores are diagnostic guidance, not a guarantee of combat results. A low metric shows what to improve with the next reward or purchase.")
        ,("Не удалось прочитать ID предложенных карт. Локальный совет появится, когда игра отдаст модели карт.", "Could not read the offered card IDs. Local advice will appear when the game exposes the card models.")
        ,("Сетевые данные недоступны — показан локальный совет.", "Network data is unavailable — showing local advice.")
        ,("База — общая сила карты по статистике и нашей модели. Поправки в скобках показывают влияние именно на текущую колоду.", "Base is the card's general strength from community statistics and our model. Parenthesized adjustments show its effect on this deck.")
        ,("Похожие победные колоды ещё проверяются…", "Checking similar winning decks…")
        ,("Похожая победная колода не найдена — использована базовая модель.", "No similar winning deck was found — using the base model.")
        ,("Найдена похожая победная колода", "Similar winning deck found")
        ,("сравнено вариантов", "options compared")
        ,("разумно, если лучший вариант не закрывает потребность.", "reasonable if the best option does not cover a need.")
        ,("обычно хуже лучшего варианта.", "usually worse than the best option.")
        ,("брали раньше", "picked before")
        ,("База", "Base")
        ,("поправка текущей колоды", "current deck adjustment")
        ,("нужна защита", "defense needed")
        ,("Команда:", "Team:"), ("Команда", "Team"), ("защита напарника", "teammate protection"), ("усиление напарника", "teammate boost")
        ,("атаки напарников", "teammates' attacks"), ("архетип напарника", "teammate's archetype")
        ,("роль:", "role:"), ("универсал", "all-rounder"), ("поддержка", "support"), ("ОЗ", "HP")
        ,("Латоносец", "Ironclad"), ("Безмолвная", "Silent"), ("Наследник", "Regent"), ("Призывающая", "Necrobinder")
        ,("усиление к поздней игре", "late-game scaling")
        ,("нужен добор", "draw needed")
        ,("даёт энергию", "provides energy")
        ,("дорогая при нехватке энергии", "expensive while energy-starved")
        ,("сложность разыгрывания", "play difficulty")
        ,("подготовка к боссу", "boss preparation")
        ,("синергия архетипа", "archetype synergy")
        ,("не подходит выбранному архетипу", "does not fit selected archetype")
        ,("конфликт с ручным архетипом", "conflicts with manual archetype")
        ,("похожие победные колоды", "similar winning decks")
        ,("ваша история выборов", "your choice history")
        ,("с учётом истории забегов", "using run history")
        ,("по структуре колоды", "from deck structure")
        ,("размер колоды", "deck size")
        ,("ценность в акте", "value in Act")
        ,("несколько структурных минусов", "multiple structural penalties")
        ,("дубликат, размер колоды и поздняя ценность усиливают друг друга", "duplicate, deck size, and late-game value compound")
        ,("нейтральна к текущим потребностям", "neutral to current needs")
        ,("граница шкалы", "score boundary")
        ,("ограничение другого архетипа", "off-archetype cap")
        ,("карта другого архетипа", "off-archetype card")
        ,("После выбора", "After pick")
        ,("закрывает", "covers")
        ,("ср. стоимость", "avg. cost")
        ,("архетип ещё не определён", "archetype not identified yet")
        ,("выбран вручную", "selected manually")
        ,("модель карты не найдена", "card model not found")
        ,("План магазина", "Shop plan")
        ,("Бюджет", "Budget")
        ,("Золото не прочитано.", "Gold was not read.")
        ,("Удаление: высокий приоритет — колода уже большая, базовая карта снижает качество добора.", "Removal: high priority — the deck is already large and a starter card reduces draw quality.")
        ,("Удаление: средний приоритет; не жертвуйте ключевой покупкой без явного слабого базового слота.", "Removal: medium priority; do not sacrifice a key purchase without a clearly weak starter slot.")
        ,("Карты (лучшее соотношение пользы и цены)", "Cards (best value for cost)")
        ,("Зелья: берите перед элитой/боссом, если они закрывают текущий риск; иначе сохраните золото на ключевую покупку.", "Potions: buy before an elite/boss when they cover the current risk; otherwise save gold for a key purchase.")
        ,("Корзина: нет уверенной покупки с прочитанной ценой в текущий бюджет.", "Cart: no confident purchase with a known price fits the budget.")
        ,("Корзина: лучше сохранить золото.", "Cart: saving gold is better.")
        ,("Оптимальная корзина по текущей модели", "Optimal cart for the current model")
        ,("останется", "remaining")
        ,("цена не прочитана", "price unavailable")
        ,("общая ценность по данным сообщества", "general value from community data")
        ,("решает дефицит энергии дорогой колоды", "solves an expensive deck's energy deficit")
        ,("поддерживает выживаемость при низком HP", "supports survival at low HP")
        ,("улучшает экономику следующих магазинов", "improves economy in future shops")
        ,("История: локально", "History: local")
        ,("забегов", "runs")
        ,("побед", "wins")
        ,("глобальный офлайн-приор", "global offline prior")
        ,("ориентир размера к этажу", "target size by floor")
        ,("Личные результаты используются только при достаточной повторяемости наблюдений.", "Personal results are used only when observations are sufficiently repeatable.")
        ,("решений, подтверждено выборов", "decisions, confirmed picks")
        ,("чаще выбирались", "most frequently picked")
        ,("история временно недоступна", "history is temporarily unavailable")
        ,("Профиль", "Profile")
        ,("атаки", "attacks")
        ,("навыки", "skills")
        ,("таланты", "powers")
        ,("блок", "block")
        ,("масштабирование", "scaling")
        ,("средняя стоимость", "average cost")
        ,("защита/выживаемость", "defense/survival")
        ,("масштабирование под босса", "boss scaling")
        ,("добор/консистентность", "draw/consistency")
        ,("дополнительная энергия", "additional energy")
        ,("синергия и качество карт", "synergy and card quality")
        ,("Статистика сообщества", "Community statistics")
        ,("Загрузка общей статистики…", "Loading aggregate statistics…")
        ,("Товары ещё загружаются — откройте совет через мгновение.", "Items are still loading — open advice again in a moment.")
        ,("у похожих победных билдов", "in similar winning builds")
        ,("Среди ближайших победных билдов этих реликвий пока нет.", "These relics were not found in the closest winning builds.")
        ,("совет будет рассчитан локально", "advice will be calculated locally")
        ,("Анализ", "Analysis")
        ,("реликвии", "relics")
        ,("контекст", "context")
        ,("неизвестно", "unknown")
        ,("копия", "copy")
        ,("ориентир", "target")
        ,("авто", "auto")
        ,("Ближайшая угроза: маршрут и ближайшая угроза пока не прочитаны.", "Nearest threat: the route and upcoming threat have not been read yet.")
        ,("Перебор копий", "Too many copies")
        ,("Вне синергии", "Off-synergy")
        ,("Эффективно", "Efficient")
        ,("Неэффективно", "Inefficient")
        ,("Дефект", "Defect")
        ,("Базовые роли", "Core roles")
        ,("Непосредственная роль карты.", "The card's immediate role.")
        ,("Структура", "Structure")
        ,("Положение карты в плане колоды.", "The card's place in the deck plan.")
        ,("Вердикт", "Verdict")
        ,("Итоговая практическая пометка.", "Final practical verdict.")
        ,("Кривая и размер", "Curve and size")
        ,("Стоимость, повторяемость и влияние на размер колоды.", "Cost, repeatability, and impact on deck size.")
        ,("Фаза", "Run phase")
        ,("Ценность относительно этапа забега.", "Value for the current stage of the run.")
        ,("Свойства", "Properties")
        ,("Особые правила розыгрыша.", "Special play rules.")
        ,("Ослабления", "Debuffs")
        ,("Наложение или использование отрицательного эффекта.", "Applies or exploits a negative effect.")
        ,("Архетипы", "Archetypes")
        ,("Основные механические направления персонажей.", "Core mechanical directions for each character.")
        ,("Сферы", "Orbs")
        ,("Создание, расход и усиление сфер.", "Orb generation, consumption, and amplification.")
        ,("Связи", "Relationships")
        ,("Создаёт ресурс или эффект.", "Creates a resource or effect.")
        ,("Получает выгоду от ресурса или эффекта.", "Benefits from a resource or effect.")
        ,("Увеличивает силу ресурса или эффекта.", "Amplifies a resource or effect.")
        ,("Расширенные оси", "Extended axes")
        ,("Точная связь механики: генерация, потребление, усиление, условие или награда.", "Precise mechanic link: generation, consumption, amplification, condition, or payoff.")
        ,("Ядро", "Core")
        ,("Ключевая карта", "Keystone")
        ,("Финишер", "Finisher")
        ,("Поддержание", "Sustain")
        ,("Мост архетипов", "Archetype bridge")
        ,("В архетипе", "Archetype fit")
        ,("Заполнитель", "Filler")
        ,("Рекомендуется", "Recommended")
        ,("Ситуативно", "Situational")
        ,("Стабильно", "Stable")
        ,("Нестабильно", "Unstable")
        ,("Сложно разыгрывать", "Hard to play")
        ,("X-стоимость", "X-cost")
        ,("Тяжёлая", "Heavy")
        ,("Много копий", "Many copies")
        ,("Полезная копия", "Useful copy")
        ,("Можно накапливать", "Stackable")
        ,("Размывает колоду", "Bloats deck")
        ,("Растёт с колодой", "Scales with deck")
        ,("Сохраняет энергию", "Carries energy")
        ,("Ранняя ценность", "Early value")
        ,("Взрывной эффект", "Burst")
        ,("Высокая отдача", "High output")
        ,("Низкая отдача", "Low output")
        ,("Стартовая", "Starter")
        ,("Врождённая", "Innate")
        ,("Сохранение", "Retain")
        ,("Бесплотная", "Ethereal")
        ,("Случайность", "Random")
        ,("Повторный розыгрыш", "Replay")
        ,("Возврат", "Return")
        ,("Поиск в стопке", "Pile search")
        ,("Создание карт", "Card generation")
        ,("Статусы", "Statuses")
        ,("Много ударов", "Multi-hit")
        ,("Слабость", "Weak")
        ,("Хрупкость", "Frail")
        ,("Хитрость", "Cunning")
        ,("Ловкость", "Dexterity")
        ,("Ковка", "Forge")
        ,("Бесцветное создание", "Colorless creation")
        ,("Генератор бесцветных", "Colorless generator")
        ,("Награда за создание", "Creation payoff")
        ,("Контроль случайных карт", "Random-card control")
        ,("Нестабильность", "Volatile")
        ,("Ости", "Thorns")
        ,("Клинок лорда", "Lord's Blade")
        ,("Удары", "Strikes")
        ,("Молния", "Lightning")
        ,("Тьма", "Dark")
        ,("Плазма", "Plasma")
        ,("Стекло", "Glass")
        ,("Ячейки сфер", "Orb slots")
        ,("Активация сфер", "Orb evoke")
        ,("Генератор", "Producer")
        ,("Потребитель", "Consumer")
        ,("Усилитель", "Amplifier")
        ,("Награда", "Payoff")
        ,("Условие", "Condition")
        ,("Снижение стоимости", "Cost enabler")
        ,("Тип", "Type")
        ,("Повтор", "Replay")
        ,("По площади", "Area damage")
        ,("Ослабление", "Debuff")
        ,("Лечение", "Healing")
        ,("золота", "gold")
    ];

    public static string Text(string? value) => Translate(value, current);

    internal static string Translate(string? value, AdvisorLanguage language)
    {
        if (string.IsNullOrEmpty(value) || language == AdvisorLanguage.Russian) return value ?? "";
        // Every dictionary key is Russian, so text without Cyrillic (server names in English,
        // numbers, already translated lines) needs no replacement pass at all.
        if (!value.Any(IsCyrillic)) return value;
        var result = value;
        foreach (var (ru, en) in EnglishByLength ??= English.OrderByDescending(x => x.Ru.Length).ToArray())
            if (result.Contains(ru, StringComparison.OrdinalIgnoreCase)) result = ReplaceWords(result, ru, en);
        return result;
    }

    // A key that starts or ends with a letter matches whole words only: "до" must not turn
    // "достаточно" into "toстаточно", "Тип" must not cut "архетип". Before 6.21 short keys
    // replaced fragments inside longer words.
    internal static string ReplaceWords(string text, string key, string replacement)
    {
        var checkStart = char.IsLetter(key[0]);
        var checkEnd = char.IsLetter(key[^1]);
        var builder = new System.Text.StringBuilder(text.Length);
        var position = 0;
        while (position < text.Length)
        {
            var index = text.IndexOf(key, position, StringComparison.OrdinalIgnoreCase);
            if (index < 0) break;
            var end = index + key.Length;
            var boundaryBefore = !checkStart || index == 0 || !char.IsLetter(text[index - 1]);
            var boundaryAfter = !checkEnd || end >= text.Length || !char.IsLetter(text[end]);
            builder.Append(text, position, index - position);
            builder.Append(boundaryBefore && boundaryAfter ? replacement : text.Substring(index, key.Length));
            position = end;
        }
        builder.Append(text, position, text.Length - position);
        return builder.ToString();
    }

    // Sorted once instead of on every call; Translate runs for each render and every second.
    private static (string Ru, string En)[]? EnglishByLength;
    private static bool IsCyrillic(char ch) => ch is >= '\u0400' and <= '\u04FF';

    public static void Set(AdvisorLanguage language)
    {
        if (current == language) return;
        current = language;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            var temporary = SettingsPath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(new Settings(language == AdvisorLanguage.English ? "en" : "ru")));
            File.Move(temporary, SettingsPath, true);
        }
        catch (Exception error) { GD.PrintErr("Build Advisor language settings: " + error.Message); }
        Changed?.Invoke();
    }

    private static AdvisorLanguage Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return AdvisorLanguage.Russian;
            var settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsPath));
            return string.Equals(settings?.Language, "en", StringComparison.OrdinalIgnoreCase) ? AdvisorLanguage.English : AdvisorLanguage.Russian;
        }
        catch { return AdvisorLanguage.Russian; }
    }
}

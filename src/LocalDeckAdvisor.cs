namespace BuildAdvisor.Integration;

// A deterministic fallback: the remote data ranks community outcomes, while this component
// explains the run the player is actually in and remains useful if the service is down.
internal static class LocalDeckAdvisor
{
    public static string Snapshot(RunContext c)
    {
        var roles = CountRoles(c);
        var needs = Needs(c, roles);
        return $"Контекст: {CharacterName(c.Character)} · A{c.Ascension} · Act {c.Act} · этаж {c.Floor}\n" +
               $"Колода: {c.Cards.Count} карт · реликвии: {c.Relics.Count} · HP {c.Hp}/{c.MaxHp} · золото {c.Gold}\n" +
               $"Профиль: атаки {c.AttackCards}, навыки {c.SkillCards}, таланты {c.PowerCards}; урон {roles.damage}, блок {roles.block}, добор {roles.draw}, масштабирование {roles.scale}, энергия {roles.energy}; средняя стоимость {c.AverageCost:0.0}\n" +
               "Сейчас нужно: " + string.Join(", ", needs) + ".";
    }

    public static string ForCards(RunContext? c, IEnumerable<string> offered, Func<string, string>? localize = null)
    {
        if (c is null) return "Локальный анализ: состояние забега пока не прочитано.";
        var roles = CountRoles(c); var needs = Needs(c, roles);
        var lines = offered.Distinct().Select(card =>
        {
            var role = Role(card);
            var fit = Fit(role, roles, needs, c);
            var why = role switch
            {
                "block" => "закрывает защиту и снижает риск перед элитами",
                "draw" => "повышает стабильность розыгрыша ключевых карт",
                "scale" => "помогает с затяжными боями и боссом",
                "energy" => "даёт темп, если есть дорогие карты",
                "damage" => "даёт быстрый урон для Act 1 и элит",
                _ => "роль не определена по ID — оценивайте текст карты вручную"
            };
            var duplicate = c.Cards.Count(x => SpireCodexId(x) == SpireCodexId(card));
            return $"{Name(card, localize)}: {fit} — {why}" + (duplicate > 0 ? $"; уже копий: {duplicate}" : "") + ".";
        });
        return Snapshot(c) + "\n\nЛокальный совет по вариантам:\n" + string.Join("\n", lines) +
               "\n\nПропуск: разумен, если ни один вариант не закрывает текущую потребность или только размывает колоду.";
    }

    // This is deliberately compact: it is blended with Spire Codex data, rather than replacing it.
    public static string SpireContext(RunContext? c, IEnumerable<string> offered, Func<string, string>? localize = null)
    {
        if (c is null) return "Колода не прочитана — показана общая оценка карт.";
        var roles = CountRoles(c); var needs = Needs(c, roles);
        var weights = offered.Distinct().Select(id =>
        {
            var bonus = SpireAdjustment(c, id);
            return $"{Name(id, localize)}: контекст {FormatBonus(bonus)} ({RoleLabel(Role(id))})";
        });
        return $"Контекст колоды: атаки {c.AttackCards} · навыки {c.SkillCards} · таланты {c.PowerCards}; урон {roles.damage} · защита {roles.block} · навыки/добор {roles.draw} · масштабирование {roles.scale} · энергия {roles.energy}; средняя стоимость {c.AverageCost:0.0}.\n" +
               "Сейчас важнее: " + string.Join(", ", needs) + ".\n" +
               "Поправка с учётом текущей колоды: " + string.Join("; ", weights) + ".";
    }

    public static int SpireAdjustment(RunContext c, string card)
    {
        var roles = CountRoles(c); var needs = Needs(c, roles); var role = Role(card);
        if (role == "damage" && needs.Contains("быстрый урон")) return 15;
        if (role == "block" && needs.Contains("защита/выживаемость")) return 18;
        if (role == "scale" && needs.Contains("масштабирование под босса")) return 14;
        if (role == "draw" && needs.Contains("добор/консистентность")) return 12;
        if (role == "energy" && needs.Any(x => x.StartsWith("дополнительная энергия", StringComparison.Ordinal))) return 18;
        return 0;
    }

    public static string ForRelics(RunContext? c, IEnumerable<string> offered, Func<string, string>? localize = null)
    {
        if (c is null) return "Локальный анализ реликвий: состояние забега пока не прочитано.";
        var lines = offered.Distinct().Select(id =>
        {
            var role = RelicRole(id);
            var verdict = role switch
            {
                "energy" => "высокий приоритет, если в колоде есть дорогие карты; проверьте цену/негативный эффект",
                "defense" => "полезна при нехватке блока и низком HP",
                "economy" => "сильнее до магазина или при ограниченном золоте",
                "consistency" => "усиливает добор и стабильность ключевых ходов",
                _ => "сравните непосредственный эффект с ближайшей проблемой колоды"
            };
            return $"{Name(id, localize)}: {verdict}.";
        });
        return Snapshot(c) + "\n\nЛокальный совет по реликвиям:\n" + string.Join("\n", lines);
    }

    public static string ForEvent(RunContext? c, IEnumerable<string> choices)
    {
        var intro = c is null ? "Состояние забега не прочитано." : Snapshot(c);
        var rows = choices.Select((choice, index) => $"{index + 1}. {choice} — {EventHint(choice, c)}");
        return intro + "\n\nВетки события:\n" + string.Join("\n", rows) +
               "\n\nПроверьте точные HP/золото/проклятия в тексте события: для таких веток общая статистика не является доказательством лучшего выбора.";
    }

    public static string ForShop(RunContext? c, IEnumerable<string> cards, IEnumerable<string> relics, Func<string, string>? localize = null)
    {
        var intro = c is null ? "Локальный анализ магазина: состояние забега не прочитано." : Snapshot(c);
        var needs = c is null ? [] : Needs(c, CountRoles(c));
        var cardLine = cards.Any() ? "Карты: " + string.Join(", ", cards.Take(12).Select(x => Name(x, localize) + " (" + Role(x) + ")")) : "Карты не прочитаны.";
        var relicLine = relics.Any() ? "Реликвии: " + string.Join(", ", relics.Take(12).Select(x => Name(x, localize) + " (" + RelicRole(x) + ")")) : "Реликвии не прочитаны.";
        return intro + "\n\n" + cardLine + "\n" + relicLine +
               "\n\nПриоритет покупки: 1) закрыть " + string.Join(" / ", needs.Take(2)) +
               "; 2) реликвия с прямой синергией; 3) удаление базовой карты, если оно не лишает следующую важную покупку; 4) зелье перед элитой/боссом.";
    }

    private static (int damage, int block, int draw, int scale, int energy) CountRoles(IEnumerable<string> cards)
    {
        var result = (damage: 0, block: 0, draw: 0, scale: 0, energy: 0);
        foreach (var card in cards) switch (Role(card))
        {
            case "damage": result.damage++; break; case "block": result.block++; break; case "draw": result.draw++; break;
            case "scale": result.scale++; break; case "energy": result.energy++; break;
        }
        return result;
    }
    private static (int damage, int block, int draw, int scale, int energy) CountRoles(RunContext context)
    {
        var result = CountRoles(context.Cards);
        // The game's CardType is more reliable than an ID/name heuristic for attacks and talents.
        result.damage = Math.Max(result.damage, context.AttackCards);
        result.scale = Math.Max(result.scale, context.PowerCards);
        return result;
    }
    private static IReadOnlyList<string> Needs(RunContext c, (int damage, int block, int draw, int scale, int energy) r)
    {
        var needs = new List<string>();
        if (r.damage < Math.Max(3, c.Act + 2)) needs.Add("быстрый урон");
        if (r.block < Math.Max(3, c.Act + 2) || c.Hp * 3 < c.MaxHp) needs.Add("защита/выживаемость");
        if (c.Act >= 2 && r.scale < 2) needs.Add("масштабирование под босса");
        if (c.Cards.Count >= 15 && r.draw < 2) needs.Add("добор/консистентность");
        var hasEnergy = r.energy > 0 || c.Relics.Any(x => RelicRole(x) == "energy");
        if (c.AverageCost >= 1.7m && !hasEnergy) needs.Add("дополнительная энергия (средняя стоимость " + c.AverageCost.ToString("0.0") + ")");
        if (needs.Count == 0) needs.Add("синергия и качество карт");
        return needs;
    }
    private static string Fit(string role, (int damage, int block, int draw, int scale, int energy) r, IReadOnlyList<string> needs, RunContext c)
    {
        var need = role == "damage" && needs.Contains("быстрый урон") || role == "block" && needs.Contains("защита/выживаемость") ||
                   role == "scale" && needs.Contains("масштабирование под босса") || role == "draw" && needs.Contains("добор/консистентность");
        return need ? "РЕКОМЕНДУЕТСЯ" : role == "unknown" ? "СИТУАТИВНО" : "НИЗКИЙ ПРИОРИТЕТ";
    }
    private static string Role(string id)
    {
        var x = SpireCodexId(id);
        if (Has(x, "BLOCK", "DEFEND", "SHIELD", "ARMOR", "WALL", "BARRIER", "GUARD", "PROTECT")) return "block";
        if (Has(x, "DRAW", "PREPARE", "INSIGHT", "MEDIT", "CONCENTR", "PLAN", "FORESIGHT")) return "draw";
        if (Has(x, "STRENGTH", "DEXTER", "RITUAL", "SCAL", "GROW", "INFLAME", "POWER")) return "scale";
        if (Has(x, "ENERGY", "ADRENAL", "RITUAL", "OFFERING")) return "energy";
        if (Has(x, "STRIKE", "SLASH", "BASH", "PUNCH", "RUPTURE", "BLOOD", "FIRE", "SMITE", "ATTACK")) return "damage";
        var kind = CardKnowledge.Get(id).Kind.ToUpperInvariant();
        if (kind.Contains("ATTACK")) return "damage";
        if (kind.Contains("POWER")) return "scale";
        if (kind.Contains("SKILL")) return "skill";
        return "unknown";
    }
    private static string RelicRole(string id)
    {
        var x = SpireCodexId(id);
        if (RelicKnowledge.GivesEnergy(x)) return "energy";
        if (RelicKnowledge.HelpsEconomy(x) || Has(x, "GOLD", "MEMBERSHIP", "COURIER")) return "economy";
        if (RelicKnowledge.HelpsSurvival(x)) return "defense";
        if (Has(x, "DRAW", "BAG", "LANTERN", "TOY", "BELL", "CONSIST")) return "consistency";
        return "other";
    }
    private static string EventHint(string text, RunContext? c)
    {
        var x = text.ToUpperInvariant();
        // The three Ancient artefacts are an explicit strategic choice, not a generic event.
        // These names are checked in Russian and canonical English so the advice survives locale changes.
        if (x.Contains("ENERGY_ICON", StringComparison.Ordinal) || HasWord(x, "ПРИЗМАТИЧЕСКИЙ САМОЦВЕТ", "PRISMATIC GEM", "ЭНЕРГ"))
        {
            var expensive = c is not null && c.AverageCost >= 1.5m;
            return expensive
                ? "ВЫСОКИЙ ПРИОРИТЕТ: дополнительная энергия разгружает дорогую колоду; карты других цветов — осознанный риск"
                : "средний приоритет: энергия сильна, но карты других цветов могут размыть уже выбранный архетип";
        }
        if (HasWord(x, "ОБЛОМОК ПЛОТИНЫ", "DAM", "REWARD", "НАГРАДН"))
            return "ситуативно: повторный выбор награды полезен, когда колода уже ищет конкретные карты; не закрывает дефицит немедленно";
        if (HasWord(x, "ДРЕВНИЙ ЗУБ", "ANCIENT TOOTH", "ВМЯТИН", "ПРОЛОМ"))
            return "рекомендуется, если улучшение «Вмятина → Пролом» даёт повторное наложение Уязвимости; особенно ценно при уже собранном направлении Уязвимости";
        if (HasWord(x, "РЕЛИК", "RELIC")) return "ценность зависит от синергии и возможной цены";
        if (HasWord(x, "ПРОКЛЯТ", "CURSE")) return "берите только если награда окупает проклятие и есть способ удалить его";
        if (HasWord(x, "ЗОЛОТ", "GOLD")) return c is not null && c.Gold < 100 ? "золото особенно ценно перед ближайшим магазином" : "сравните с потерей HP и альтернативной наградой";
        if (HasWord(x, "HP", "ЗДОРОВ", "HEAL", "ИСЦЕЛ")) return c is not null && c.Hp * 3 < c.MaxHp ? "низкий HP: избегайте риска без сильной компенсации" : "сравните с ближайшей элитой";
        return "оцените немедленную цену и то, закрывает ли награда текущую потребность";
    }
    // Event option text is matched by words, not substrings: "DAM" used to fire on every
    // "DAMAGE" and "ПОЛУЧИТ" on every "получите". Cyrillic tokens are stems and match a word
    // prefix (ПРОКЛЯТ → проклятие); Latin tokens must be whole words with an optional plural S.
    private static bool HasWord(string value, params string[] tokens) => tokens.Any(token =>
    {
        var cyrillic = token.Any(ch => ch is >= 'А' and <= 'я' or 'Ё' or 'ё');
        var pattern = @"(?<![\p{L}\p{N}_])" + System.Text.RegularExpressions.Regex.Escape(token) + (cyrillic ? "" : @"S?(?![\p{L}\p{N}_])");
        return System.Text.RegularExpressions.Regex.IsMatch(value, pattern, System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    });
    private static bool Has(string value, params string[] tokens) => tokens.Any(value.Contains);
    private static string SpireCodexId(string id) => Data.SpireCodexMetricsClient.NormalizeId(id);
    private static string CharacterName(string id) => id switch { "silent" => "Безмолвная", "ironclad" => "Латник", "defect" => "Дефект", _ => id };
    private static string Name(string id, Func<string, string>? localize) => localize?.Invoke(id) ?? id;
    private static string FormatBonus(int value) => value > 0 ? $"+{value}" : "0";
    private static string RoleLabel(string role) => role switch { "damage" => "атака", "block" => "защита", "draw" => "добор", "skill" => "навык", "scale" => "талант/масштабирование", "energy" => "энергия", _ => "не распознано" };
}

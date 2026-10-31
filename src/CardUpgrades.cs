using System.Text.Json;

namespace BuildAdvisor.Data;

// What upgrading a card changes, from the Spire Codex catalogue "upgrade" object.
// "cost" is the new absolute cost; numeric fields are deltas ("+2" or 2).
internal sealed record CardUpgrade(int? Cost, int Damage, int Block, int Draw, int Energy,
    bool RemovesExhaust, bool AddsInnate, bool AddsRetain, int OtherEffects, int Scaling = 0);

internal static class CardUpgrades
{
    private static readonly HashSet<string> Known = new(StringComparer.OrdinalIgnoreCase)
        { "cost", "damage", "block", "cards", "energy", "remove_exhaust", "add_innate", "add_retain", "description_changed" };
    // Permanent stat gains scale the whole fight, so they are valued above other effects.
    private static readonly HashSet<string> ScalingKeys = new(StringComparer.OrdinalIgnoreCase) { "strength", "strengthpower", "dexterity", "dexteritypower", "focus", "focuspower" };

    public static CardUpgrade? Parse(JsonElement row)
    {
        if (!row.TryGetProperty("upgrade", out var upgrade) || upgrade.ValueKind != JsonValueKind.Object) return null;
        int Delta(string name) => upgrade.TryGetProperty(name, out var value) ? ToInt(value) ?? 0 : 0;
        bool Flag(string name) => upgrade.TryGetProperty(name, out var value) && (ToInt(value) ?? 0) != 0;
        int? cost = upgrade.TryGetProperty("cost", out var costValue) ? ToInt(costValue) : null;
        var other = upgrade.EnumerateObject().Count(property => !Known.Contains(property.Name) && !ScalingKeys.Contains(property.Name));
        var scaling = upgrade.EnumerateObject().Where(property => ScalingKeys.Contains(property.Name)).Sum(property => Math.Max(0, ToInt(property.Value) ?? 1));
        return new CardUpgrade(cost, Delta("damage"), Delta("block"), Delta("cards"), Delta("energy"),
            Flag("remove_exhaust"), Flag("add_innate"), Flag("add_retain"), other, scaling);
    }

    private static int? ToInt(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number when value.TryGetInt32(out var number) => number,
        JsonValueKind.String when int.TryParse(value.GetString()?.Trim().TrimStart('+'), out var parsed) => parsed,
        JsonValueKind.True => 1,
        _ => null
    };

    // The card as it plays after the upgrade: the deck needs, cost curve and duplicate model see
    // the real numbers of an upgraded copy instead of the base card.
    public static CardDefinition Upgraded(CardDefinition card)
    {
        if (card.Upgrade is not { } upgrade) return card;
        return card with
        {
            Cost = upgrade.Cost is { } cost && card.Cost >= 0 ? cost : card.Cost,
            Damage = card.Damage + Math.Max(0, upgrade.Damage),
            Block = card.Block + Math.Max(0, upgrade.Block),
            Draw = card.Draw + Math.Max(0, upgrade.Draw),
            Energy = card.Energy + Math.Max(0, upgrade.Energy)
        };
    }

    // Value of the upgrade itself on the 0-100 advice scale. One energy is worth about as much as
    // the strongest stat bonuses; flags and other effects (Vulnerable, Strength, Forge…) add a
    // smaller fixed amount. Unknown catalogue data still counts as a small upgrade.
    public static decimal Value(CardDefinition card)
    {
        if (card.Upgrade is not { } upgrade) return 3m;
        var costCut = upgrade.Cost is { } cost && card.Cost > 0 ? Math.Max(0, card.Cost - cost) : 0;
        var value = costCut * 7m +
                    Math.Min(8m, upgrade.Damage * .6m) + Math.Min(8m, upgrade.Block * .7m) +
                    upgrade.Draw * 5m + upgrade.Energy * 6m +
                    (upgrade.RemovesExhaust ? 5m : 0m) + (upgrade.AddsInnate ? 3m : 0m) + (upgrade.AddsRetain ? 2m : 0m) +
                    Math.Min(6m, upgrade.OtherEffects * 3m) + Math.Min(10m, upgrade.Scaling * 5m);
        return Math.Clamp(value, 2m, 15m);
    }
}

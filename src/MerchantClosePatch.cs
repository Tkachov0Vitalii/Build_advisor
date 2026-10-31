using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;
using BuildAdvisor.UI;

namespace BuildAdvisor.Integration;

[HarmonyPatch(typeof(NMerchantInventory), "Close")]
public static class MerchantClosePatch
{
    [HarmonyPostfix]
    public static void AfterClose(Node __instance)
    {
        MerchantPatch.Invalidate();
        AdviceOverlay.SetRefreshAction(null);
        AdviceOverlay.ClearShopBadges(__instance);
        AdviceOverlay.Remove(__instance);
    }
}

// 6.17.0: a purchase changes gold, deck and the remaining slots. The plan used to keep the
// state from the moment the shop opened until the player pressed "Refresh".
[HarmonyPatch(typeof(NMerchantInventory), "OnPurchaseCompleted")]
public static class MerchantPurchasePatch
{
    [HarmonyPostfix]
    public static void AfterPurchase(Node __instance)
    {
        if (GodotObject.IsInstanceValid(__instance) && __instance.IsInsideTree()) MerchantPatch.AfterOpen(__instance);
    }
}

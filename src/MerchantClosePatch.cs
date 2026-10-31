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

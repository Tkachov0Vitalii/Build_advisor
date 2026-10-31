using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using BuildAdvisor.UI;

namespace BuildAdvisor.Integration;

// A reward card node can be re-used in the combat hand.  Remove the annotation at the moment
// the player confirms any reward, rather than relying on visibility changes of the screen.
[HarmonyPatch(typeof(NCardRewardSelectionScreen), "SelectCard")]
internal static class CardRewardSelectBadgeCleanup
{
    [HarmonyPostfix]
    private static void AfterSelect(Node __instance) => AdviceOverlay.ClearBadges(__instance);
}

[HarmonyPatch(typeof(NCardRewardSelectionScreen), "OptionSelected")]
internal static class CardRewardOptionBadgeCleanup
{
    [HarmonyPostfix]
    private static void AfterOption(Node __instance) => AdviceOverlay.ClearBadges(__instance);
}

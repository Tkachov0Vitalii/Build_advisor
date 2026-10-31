using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using BuildAdvisor.UI;

namespace BuildAdvisor.Integration;

[ModInitializer("Initialize")]
public static class BuildAdvisorMod
{
    public static void Initialize()
    {
        new Harmony("build-advisor.sts2").PatchAll(typeof(BuildAdvisorMod).Assembly);
        // The map HUD patch installs the overlay after Godot has completed its node setup.
        Log.Info(BuildAdvisorRegression.Run()
            ? "Build Advisor loaded; regression matrix passed."
            : "Build Advisor loaded; regression matrix failed: " + BuildAdvisorRegression.LastError);
    }
}

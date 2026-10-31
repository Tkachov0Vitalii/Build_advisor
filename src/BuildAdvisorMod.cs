using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using BuildAdvisor.UI;

namespace BuildAdvisor.Integration;

[ModInitializer("Initialize")]
public static class BuildAdvisorMod
{
    public static IReadOnlyList<string> SkippedPatches { get; private set; } = [];

    public static void Initialize()
    {
        // Each patch class is installed independently. A game update that renames one hooked
        // method must disable only that feature, not every Build Advisor patch.
        var harmony = new Harmony("build-advisor.sts2");
        var skipped = new List<string>();
        var patchTypes = AccessTools.GetTypesFromAssembly(typeof(BuildAdvisorMod).Assembly)
            .Where(type => type.GetCustomAttributes(typeof(HarmonyPatch), false).Length > 0);
        foreach (var type in patchTypes)
        {
            try { harmony.CreateClassProcessor(type).Patch(); }
            catch (Exception exception)
            {
                skipped.Add(type.Name);
                Log.Warn($"Build Advisor: patch {type.Name} skipped: {(exception as TargetInvocationException)?.InnerException?.Message ?? exception.Message}");
            }
        }
        SkippedPatches = skipped;
        TierOverrides.WriteExampleIfMissing();

        // The map HUD patch installs the overlay after Godot has completed its node setup.
        bool passed;
        try { passed = BuildAdvisorRegression.Run(); }
        catch (Exception exception) { passed = false; Log.Warn("Build Advisor regression matrix crashed: " + exception.Message); }
        var patchSummary = skipped.Count == 0 ? "all patches installed" : $"{skipped.Count} patch(es) skipped: {string.Join(", ", skipped)}";
        Log.Info(passed
            ? $"Build Advisor {ModVersion.Current} loaded; {patchSummary}; regression matrix passed."
            : $"Build Advisor {ModVersion.Current} loaded; {patchSummary}; regression matrix failed: " + BuildAdvisorRegression.LastError);
    }
}

internal static class ModVersion
{
    public const string Current = "6.22.3";
}

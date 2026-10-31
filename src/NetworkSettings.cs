using System.Text.Json;

namespace BuildAdvisor;

// Offline mode: no request leaves the game. Cards, metrics and relics come from the disk cache
// (stale copies are accepted), Pick Coach and card images are skipped, and the local model
// covers everything else. The choice persists in network.json.
internal static class NetworkSettings
{
    private sealed record Saved(bool Offline);
    private static readonly string SavePath = ModPaths.UserData("network.json");
    private static bool? offline;

    public static event Action? Changed;

    public static bool Offline
    {
        get => offline ??= Load();
        set
        {
            if (Offline == value) return;
            offline = value;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SavePath)!);
                File.WriteAllText(SavePath + ".tmp", JsonSerializer.Serialize(new Saved(value)));
                File.Move(SavePath + ".tmp", SavePath, true);
            }
            catch { /* the setting still applies to this session */ }
            Changed?.Invoke();
        }
    }

    // Thrown by network clients instead of sending a request; their existing fallbacks
    // (stale disk cache, local advice) handle it like any other network failure.
    public static void ThrowIfOffline()
    {
        if (Offline) throw new InvalidOperationException("Build Advisor offline mode");
    }

    private static bool Load()
    {
        try { return File.Exists(SavePath) && JsonSerializer.Deserialize<Saved>(File.ReadAllText(SavePath))?.Offline == true; }
        catch { return false; }
    }
}

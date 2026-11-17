using System.Text.Json;

namespace BuildAdvisor.Data;

// 7.5: community statistics for the game version being played.
// Spire Codex metrics without a version mix every patch since early access. After v0.111.1
// (Major Update #3) that matters: Demon Form had lift −5.1 pp on a10:v0.107.1 and +9.5 pp on
// a10:v0.111.0. The API accepts a game version as a bracket ("v0.111.0") or a composite
// ("a10:v0.111.0"); /api/runs/versions lists the versions with a stats slice ("stat_versions").
// A version bracket the snapshot does not carry yet answers with all runs and no data
// (data_through null, lift_n 0), so every candidate is probed before it is used.
internal static class StatsVersion
{
    // A version slice needs at least this many A10 runs before it replaces the mixed statistics.
    internal const int MinimumRuns = 20_000;
    // Older versions tried when the played one has no slice yet (closest content first).
    internal const int OlderFallbacks = 1;

    private static readonly System.Net.Http.HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static Task<string?>? resolving;

    // The running game's version (ReleaseInfoManager), set by BuildAdvisorMod.Initialize.
    // Null outside the game: tests and offline tools keep the unversioned brackets.
    internal static string? GameVersion { get; set; }
    // The Spire Codex version slice in use (e.g. "v0.111.0"), null until resolved or when none fits.
    internal static string? InUse { get; private set; }

    internal static (int Major, int Minor, int Patch)? Parse(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return null;
        var text = version.Trim().TrimStart('v', 'V');
        if (text.Contains('-')) return null;                     // release candidates, beta tags
        var parts = text.Split('.');
        if (parts.Length != 3) return null;
        return int.TryParse(parts[0], out var a) && int.TryParse(parts[1], out var b) && int.TryParse(parts[2], out var c) ? (a, b, c) : null;
    }

    // Versions to probe, best first: the same major.minor at or below the played patch (a
    // hotfix or the main-branch release of a beta keeps the content), newest first; then the
    // newest older versions. Never a newer version than the one being played.
    internal static IReadOnlyList<string> Candidates(string? game, IEnumerable<string> statVersions)
    {
        if (Parse(game) is not { } played) return [];
        var known = statVersions.Select(v => (Text: v, Parsed: Parse(v))).Where(x => x.Parsed is not null)
            .Select(x => (x.Text, Version: x.Parsed!.Value)).Distinct().ToArray();
        var same = known.Where(x => x.Version.Major == played.Major && x.Version.Minor == played.Minor && x.Version.Patch <= played.Patch)
            .OrderByDescending(x => x.Version.Patch).Select(x => x.Text);
        var older = known.Where(x => x.Version.Major < played.Major || (x.Version.Major == played.Major && x.Version.Minor < played.Minor))
            .OrderByDescending(x => x.Version).Take(OlderFallbacks).Select(x => x.Text);
        return same.Concat(older).ToArray();
    }

    // "a10" → "a10:v0.111.0", "all" → "v0.111.0". Other brackets (wr*) have too few runs per
    // version and stay as they are.
    internal static string Apply(string bracket, string? version) => version is null ? bracket : bracket switch
    {
        "all" => version,
        "a10" => "a10:" + version,
        _ => bracket
    };

    internal static string Apply(string bracket) => Apply(bracket, InUse);

    // A probe answer is a real slice only with a data date, enough runs and measured lifts.
    internal static bool IsUsable(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return false;
        if (!root.TryGetProperty("data_through", out var through) || through.ValueKind != JsonValueKind.String) return false;
        if (!root.TryGetProperty("total_runs", out var runs) || !runs.TryGetInt32(out var total) || total < MinimumRuns) return false;
        if (!root.TryGetProperty("rows", out var rows) || rows.ValueKind != JsonValueKind.Array) return false;
        return rows.EnumerateArray().Any(row => row.TryGetProperty("lift_n", out var n) && n.TryGetInt32(out var count) && count > 0);
    }

    internal static IReadOnlyList<string> StatVersions(JsonElement root) =>
        root.TryGetProperty("stat_versions", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToArray()
            : [];

    // Resolves once per session (a day-old disk answer is reused, also offline). Waits at most
    // `wait` so a slow network never holds the advice back: until resolved, mixed statistics.
    internal static async Task<string?> ResolveAsync(TimeSpan wait)
    {
        if (GameVersion is null || BuildAdvisor.Integration.BuildAdvisorRegression.IsRunning) return null;
        resolving ??= ResolveCoreAsync();
        var finished = await Task.WhenAny(resolving, Task.Delay(wait));
        return finished == resolving ? await resolving : null;
    }

    private static async Task<string?> ResolveCoreAsync()
    {
        await Gate.WaitAsync();
        try
        {
            var disk = ModPaths.UserData($"stats-version-{GameVersion}.txt");
            if (File.Exists(disk) && (NetworkSettings.Offline || DateTime.UtcNow - File.GetLastWriteTimeUtc(disk) < TimeSpan.FromHours(24)))
            {
                var saved = (await File.ReadAllTextAsync(disk)).Trim();
                return InUse = saved.Length == 0 ? null : saved;
            }
            NetworkSettings.ThrowIfOffline();
            var versionsJson = await Http.GetStringAsync("https://spire-codex.com/api/runs/versions");
            var candidates = await Task.Run(() =>
            {
                using var document = JsonDocument.Parse(versionsJson);
                return Candidates(GameVersion, StatVersions(document.RootElement));
            });
            string? chosen = null;
            foreach (var candidate in candidates)
            {
                var json = await Http.GetStringAsync($"https://spire-codex.com/api/runs/metrics/relics?bracket={Uri.EscapeDataString(Apply("a10", candidate))}");
                var usable = await Task.Run(() => { using var document = JsonDocument.Parse(json); return IsUsable(document.RootElement); });
                if (usable) { chosen = candidate; break; }
            }
            Directory.CreateDirectory(Path.GetDirectoryName(disk)!);
            await File.WriteAllTextAsync(disk, chosen ?? "");
            return InUse = chosen;
        }
        catch (Exception)
        {
            // Any failure keeps the mixed statistics; the next session tries again.
            return InUse = null;
        }
        finally { Gate.Release(); }
    }
}

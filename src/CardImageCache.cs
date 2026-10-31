using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Godot;

namespace BuildAdvisor.UI;

internal static class CardImageCache
{
    private static readonly System.Net.Http.HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };
    private static readonly SemaphoreSlim Downloads = new(4, 4);
    private static readonly ConcurrentDictionary<string, Texture2D> Memory = new(StringComparer.OrdinalIgnoreCase);
    private const int MemoryLimit = 128;
    private static readonly ConcurrentDictionary<string, byte> Unsupported = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> UrlGates = new(StringComparer.OrdinalIgnoreCase);

    // M8: card portraits come from the game's own resources (offline, no download, matches the
    // installed game version). The Spire Codex image remains the fallback for unknown IDs.
    private static Dictionary<string, string>? portraitPaths;
    private static readonly Dictionary<string, Texture2D?> GameTextures = new(StringComparer.OrdinalIgnoreCase);

    internal static Texture2D? GamePortrait(string? cardId)
    {
        if (string.IsNullOrWhiteSpace(cardId)) return null;
        var key = BuildAdvisor.Data.SpireCodexMetricsClient.NormalizeId(cardId);
        if (GameTextures.TryGetValue(key, out var known)) return known;
        Texture2D? texture = null;
        try
        {
            portraitPaths ??= BuildPortraitIndex();
            if (portraitPaths.TryGetValue(key, out var path) && ResourceLoader.Exists(path))
                texture = ResourceLoader.Load<Texture2D>(path);
        }
        catch (Exception error) { GD.Print("Build Advisor game portrait: " + error.Message); }
        if (GameTextures.Count >= MemoryLimit * 2) GameTextures.Clear();
        GameTextures[key] = texture;
        return texture;
    }

    private static Dictionary<string, string> BuildPortraitIndex()
    {
        var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var card in MegaCrit.Sts2.Core.Models.ModelDb.AllCards)
        {
            try
            {
                if (card is null || !card.HasPortrait || string.IsNullOrWhiteSpace(card.PortraitPath)) continue;
                index[BuildAdvisor.Data.SpireCodexMetricsClient.NormalizeId(card.Id.Entry)] = card.PortraitPath;
            }
            catch { /* one broken model must not hide the other portraits */ }
        }
        return index;
    }

    public static async Task LoadIntoAsync(TextureRect target, string? cardId, string? url)
    {
        var portrait = GamePortrait(cardId);
        if (portrait is not null)
        {
            if (GodotObject.IsInstanceValid(target)) target.Texture = portrait;
            return;
        }
        await LoadIntoAsync(target, url);
    }

    public static async Task LoadIntoAsync(TextureRect target, string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        if (Unsupported.ContainsKey(url)) return;
        try
        {
            if (Memory.TryGetValue(url, out var ready))
            {
                if (GodotObject.IsInstanceValid(target)) target.Texture = ready;
                return;
            }
            var urlGate = UrlGates.GetOrAdd(url, _ => new SemaphoreSlim(1, 1));
            await urlGate.WaitAsync();
            try
            {
                if (Memory.TryGetValue(url, out ready))
                {
                    if (GodotObject.IsInstanceValid(target)) target.Texture = ready;
                    return;
                }
                await Downloads.WaitAsync();
                try
                {
                if (!Memory.TryGetValue(url, out ready))
                {
                    var path = DiskPath(url);
                    if (!File.Exists(path))
                    {
                        if (NetworkSettings.Offline) return;   // only already downloaded art is shown offline
                        var bytes = await Http.GetByteArrayAsync(url);
                        if (!IsStaticWebp(bytes)) { Unsupported[url] = 0; return; }
                        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                        await File.WriteAllBytesAsync(path, bytes);
                    }
                    else
                    {
                        var bytes = await File.ReadAllBytesAsync(path);
                        if (!IsStaticWebp(bytes)) { Unsupported[url] = 0; return; }
                    }
                    var image = Image.LoadFromFile(path);
                    if (image is null || image.IsEmpty()) return;
                    ready = ImageTexture.CreateFromImage(image);
                    // The search can show every card in the game. Textures on screen stay alive
                    // through their TextureRect; the cache itself is dropped past a fixed size
                    // instead of keeping every artwork for the whole session. Disk cache remains.
                    if (Memory.Count >= MemoryLimit) Memory.Clear();
                    Memory[url] = ready;
                }
                }
                finally { Downloads.Release(); }
            }
            finally { urlGate.Release(); }
            if (GodotObject.IsInstanceValid(target)) target.Texture = ready;
        }
        catch (Exception error)
        {
            GD.PrintErr("Build Advisor card image: " + error.GetType().Name + " " + error.Message);
        }
    }

    private static bool IsStaticWebp(byte[] bytes)
    {
        if (bytes.Length < 16 || bytes[0] != (byte)'R' || bytes[1] != (byte)'I' || bytes[2] != (byte)'F' || bytes[3] != (byte)'F' || bytes[8] != (byte)'W' || bytes[9] != (byte)'E' || bytes[10] != (byte)'B' || bytes[11] != (byte)'P') return false;
        var limit = Math.Min(bytes.Length - 3, 96);
        for (var i = 12; i < limit; i++)
            if (bytes[i] == (byte)'A' && bytes[i + 1] == (byte)'N' && bytes[i + 2] == (byte)'I' && bytes[i + 3] == (byte)'M') return false;
        return true;
    }

    private static string DiskPath(string url)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)));
        return Path.Combine(ProjectSettings.GlobalizePath("user://"), "build-advisor", "card-images", hash + ".webp");
    }
}

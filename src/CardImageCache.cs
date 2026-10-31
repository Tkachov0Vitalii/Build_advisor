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
    private static readonly ConcurrentDictionary<string, byte> Unsupported = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> UrlGates = new(StringComparer.OrdinalIgnoreCase);

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

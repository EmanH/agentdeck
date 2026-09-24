using System.Text;
using System.Text.Json;
using SkiaSharp;
using Svg.Skia;

namespace AgentDeck.Core;

/// <summary>
/// The Fluent Emoji icon library (wwwroot/icons/fluent.json, shared with the web UI). Icons are referenced by
/// name, e.g. "rocket". Renders to SKPicture for the Stream Deck and hands out unique defaults.
/// </summary>
static class IconLibrary
{
    static readonly Lazy<(int size, string[] featured, Dictionary<string, string> bodies)> Data = new(Load);
    static readonly Dictionary<string, SKPicture?> Pictures = [];

    static (int, string[], Dictionary<string, string>) Load()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "wwwroot", "icons", "fluent.json");
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = doc.RootElement;
            var bodies = root.GetProperty("icons").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "");
            var featured = root.GetProperty("categories").GetProperty("Featured").EnumerateArray().Select(e => e.GetString()!).ToArray();
            return (root.GetProperty("size").GetInt32(), featured, bodies);
        }
        catch (Exception ex)
        {
            Log.Error("load icon library", ex);
            return (32, [], []);
        }
    }

    public static bool Exists(string? name) => name != null && Data.Value.bodies.ContainsKey(name);

    /// <summary>First featured icon not in <paramref name="taken"/> (so each item gets its own), cycling if all are used.</summary>
    public static string UniqueDefault(IEnumerable<string?> taken, int salt = 0)
    {
        var featured = Data.Value.featured;
        if (featured.Length == 0) return "";
        var used = taken.Where(t => t != null).ToHashSet()!;
        var free = featured.Where(f => !used.Contains(f)).ToArray();
        return free.Length > 0 ? free[0] : featured[salt % featured.Length];
    }

    public static SKPicture? Picture(string? name)
    {
        if (name == null) return null;
        lock (Pictures)
        {
            if (Pictures.TryGetValue(name, out var cached)) return cached;
            SKPicture? picture = null;
            if (Data.Value.bodies.TryGetValue(name, out var body))
            {
                int s = Data.Value.size;
                var svg = $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{s}\" height=\"{s}\" viewBox=\"0 0 {s} {s}\">{body}</svg>";
                try
                {
                    using var stream = new MemoryStream(Encoding.UTF8.GetBytes(svg));
                    picture = new SKSvg().Load(stream);
                }
                catch (Exception ex) { Log.Error($"render icon {name}", ex); }
            }
            return Pictures[name] = picture;
        }
    }
}

using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentDeck.Core;

public sealed record ModelOption(string Id, string Label, string[]? Efforts = null, string? DefaultEffort = null);

/// <summary>What one agent CLI supports right now. Efforts on a model override the agent-wide list.</summary>
public sealed record AgentOptions(ModelOption[] Models, string[] Efforts, string? DefaultModel);

/// <summary>
/// Discovers, at runtime, which models and thinking levels each installed agent CLI supports, so workflow
/// pickers stay current as the CLIs update. All probes are local (no model calls):
///   Claude: `claude --help` (effort list and model aliases).
///   Codex:  ~/.codex/models_cache.json (the CLI's own cache of models and per-model efforts).
///   Grok:   `grok models`, and the error for an invalid --reasoning-effort (in -p mode), which lists the valid levels.
/// </summary>
static partial class AgentCatalog
{
    static readonly object Gate = new();
    static Dictionary<string, AgentOptions> _cache = [];
    static Task? _refresh;

    public static event Action? Changed;

    public static Dictionary<string, AgentOptions> Current { get { lock (Gate) return new(_cache); } }

    /// <summary>Re-probe in the background (coalesces concurrent requests).</summary>
    public static void Refresh()
    {
        lock (Gate)
        {
            if (_refresh is { IsCompleted: false }) return;
            _refresh = Task.Run(() =>
            {
                var results = new Dictionary<string, AgentOptions>();
                Probe(results, "claude", Claude);
                Probe(results, "codex", Codex);
                Probe(results, "grok", Grok);
                lock (Gate) _cache = results;
                Log.Info("Agent options: " + string.Join("; ", results.Select(r =>
                    $"{r.Key} {r.Value.Models.Length} models, efforts {string.Join('/', r.Value.Efforts)}")));
                Changed?.Invoke();
            });
        }
    }

    static void Probe(Dictionary<string, AgentOptions> into, string agent, Func<AgentOptions> probe)
    {
        try { into[agent] = probe(); }
        catch (Exception ex) { Log.Error($"probe {agent}", ex); }
    }

    static AgentOptions Claude()
    {
        var help = Run("claude", "--help");
        var effortMatch = Regex.Match(help, @"--effort\s+<level>[\s\S]*?\(([a-z,\s]+)\)");
        var efforts = effortMatch.Success ? SplitList(effortMatch.Groups[1].Value) : [];
        // The --model help names the current aliases in quotes, e.g. 'fable', 'opus', or 'sonnet'.
        var modelHelp = Regex.Match(help, @"--model\s+<model>([\s\S]*?)(?:\n\s*-{1,2}[a-z]|\z)").Groups[1].Value;
        var aliases = QuotedWord().Matches(modelHelp).Select(m => m.Groups[1].Value)
            .Where(a => !a.Contains('-')) // skip full names like 'claude-fable-5' given as examples
            .Distinct().Select(a => new ModelOption(a, char.ToUpper(a[0]) + a[1..])).ToArray();
        return new AgentOptions(aliases, efforts, null);
    }

    static AgentOptions Codex()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "models_cache.json");
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        var models = new List<ModelOption>();
        foreach (var m in doc.RootElement.GetProperty("models").EnumerateArray())
        {
            if (m.TryGetProperty("visibility", out var vis) && vis.GetString() != "list") continue;
            var efforts = m.TryGetProperty("supported_reasoning_levels", out var levels)
                ? levels.EnumerateArray().Select(l => l.ValueKind == JsonValueKind.Object ? l.GetProperty("effort").GetString()! : l.GetString()!).ToArray()
                : null;
            models.Add(new ModelOption(m.GetProperty("slug").GetString()!,
                m.TryGetProperty("display_name", out var dn) ? dn.GetString()! : m.GetProperty("slug").GetString()!,
                efforts, m.TryGetProperty("default_reasoning_level", out var d) ? d.GetString() : null));
        }
        var all = models.SelectMany(m => m.Efforts ?? []).Distinct().ToArray();
        string? configured = null;
        var config = Path.Combine(Path.GetDirectoryName(path)!, "config.toml");
        if (File.Exists(config))
        {
            var match = Regex.Match(File.ReadAllText(config), @"(?m)^\s*model\s*=\s*""([^""]+)""");
            if (match.Success) configured = match.Groups[1].Value;
        }
        return new AgentOptions([.. models], all, configured);
    }

    static AgentOptions Grok()
    {
        string? defaultModel = null;
        var models = new List<ModelOption>();
        foreach (var line in Run("grok", "models").Split('\n'))
        {
            var m = Regex.Match(line, @"^\s*([*-])\s+(\S+)(\s+\(default\))?");
            if (!m.Success) continue;
            models.Add(new ModelOption(m.Groups[2].Value, m.Groups[2].Value));
            if (m.Groups[1].Value == "*" || m.Groups[3].Success) defaultModel = m.Groups[2].Value;
        }
        // Grok only validates --reasoning-effort in print mode (-p); an invalid level is rejected up front,
        // before any request, with the list of valid ones.
        var error = Run("grok", "--reasoning-effort __probe__ -p __probe__");
        var levels = Regex.Match(error, @"use one of:\s*([a-z,\s]+)");
        var efforts = levels.Success ? SplitList(levels.Groups[1].Value) : [];
        return new AgentOptions([.. models], efforts, defaultModel);
    }

    static string[] SplitList(string list) =>
        list.Split([',', ' ', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries).Where(w => w != "or").ToArray();

    /// <summary>Run a CLI and return stdout+stderr (with a timeout, so a hung CLI can't stall the app).</summary>
    static string Run(string exe, string args)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(20_000)) { try { p.Kill(true); } catch (Exception) { } }
        return stdout.Result + "\n" + stderr.Result;
    }

    [GeneratedRegex(@"'([a-z0-9][a-z0-9.\-]*)'")]
    private static partial Regex QuotedWord();
}

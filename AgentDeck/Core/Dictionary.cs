using System.Text.Json;

namespace AgentDeck.Core;

/// <summary>
/// Custom dictation vocabulary (names, jargon, product terms), kept in %APPDATA%\AgentDeck\dictionary.json.
/// Sent to Soniox as context terms (better recognition) and to the cleanup model (fix near-misses).
/// </summary>
sealed class DictionaryStore
{
    public const int MaxTerms = 500, MaxTermLength = 80;
    const int MaxTotalChars = 8000; // Soniox context allows ~10,000 characters in total

    static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AgentDeck", "dictionary.json");

    readonly object _gate = new();
    List<string> _terms;

    public event Action? Changed;

    public DictionaryStore()
    {
        try { _terms = File.Exists(FilePath) ? JsonSerializer.Deserialize<List<string>>(File.ReadAllText(FilePath)) ?? [] : []; }
        catch (Exception ex) { Log.Error("load dictionary", ex); _terms = []; }
    }

    public string[] Snapshot() { lock (_gate) return [.. _terms]; }

    /// <summary>Replace the whole list (trimmed, de-duplicated case-insensitively, within size limits).</summary>
    public void Save(IEnumerable<string> terms)
    {
        var clean = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int total = 0;
        foreach (var raw in terms)
        {
            var term = raw.Trim();
            if (term.Length == 0 || term.Length > MaxTermLength || !seen.Add(term)) continue;
            if (clean.Count >= MaxTerms || total + term.Length > MaxTotalChars) break;
            clean.Add(term);
            total += term.Length;
        }
        lock (_gate) _terms = clean;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(clean, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { Log.Error("save dictionary", ex); }
        Changed?.Invoke();
    }
}

using System.Text.Json;

namespace AgentDeck.Core;

public sealed record Transcript(DateTime At, string Text);

/// <summary>The 10 most recent dictation transcripts, newest first, kept in %APPDATA%\AgentDeck\transcripts.json.</summary>
sealed class TranscriptStore
{
    const int Keep = 10;
    static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AgentDeck", "transcripts.json");

    readonly object _gate = new();
    readonly List<Transcript> _items;

    public event Action? Changed;

    /// <summary>Environment.TickCount64 when the latest transcript arrived (0 = none this run).</summary>
    public long LastAddedTicks { get; private set; }

    public TranscriptStore()
    {
        try { _items = File.Exists(FilePath) ? JsonSerializer.Deserialize<List<Transcript>>(File.ReadAllText(FilePath)) ?? [] : []; }
        catch (Exception ex) { Log.Error("load transcripts", ex); _items = []; }
    }

    public Transcript[] Snapshot() { lock (_gate) return [.. _items]; }
    public Transcript? Latest { get { lock (_gate) return _items.FirstOrDefault(); } }

    public void Add(string text)
    {
        lock (_gate)
        {
            _items.Insert(0, new Transcript(DateTime.Now, text));
            if (_items.Count > Keep) _items.RemoveRange(Keep, _items.Count - Keep);
            LastAddedTicks = Environment.TickCount64;
        }
        Save();
        Changed?.Invoke();
    }

    void Save()
    {
        try
        {
            string json;
            lock (_gate) json = JsonSerializer.Serialize(_items);
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, json);
        }
        catch (Exception ex) { Log.Error("save transcripts", ex); }
    }
}

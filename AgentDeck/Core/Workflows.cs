using System.Text.Json;

namespace AgentDeck.Core;

/// <summary>Saved instructions plus the agent to run them in. Belongs to one project and runs in its folder.</summary>
public sealed class Workflow
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string ProjectId { get; set; } = "";
    public string Name { get; set; } = "";
    public AgentKind Agent { get; set; } = AgentKind.Claude;
    public string Instructions { get; set; } = "";
    public string Color { get; set; } = "#a855f7";
    public string Icon { get; set; } = "";
    public string? Model { get; set; }   // null = the agent's default
    public string? Effort { get; set; }  // thinking level; null = the agent's default
}

/// <summary>Workflows persisted to %APPDATA%\AgentDeck\workflows.json. Thread-safe reads via Snapshot().</summary>
sealed class WorkflowStore
{
    static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AgentDeck", "workflows.json");
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    readonly object _gate = new();
    readonly List<Workflow> _items;

    public event Action? Changed;

    public WorkflowStore()
    {
        try { _items = File.Exists(FilePath) ? JsonSerializer.Deserialize<List<Workflow>>(File.ReadAllText(FilePath), Json) ?? [] : []; }
        catch (Exception ex) { Log.Error("load workflows", ex); _items = []; }

        // Give workflows from before icons existed their own icon.
        var missing = _items.Where(w => !IconLibrary.Exists(w.Icon)).ToList();
        foreach (var w in missing) w.Icon = IconLibrary.UniqueDefault(_items.Select(x => x.Icon));
        if (missing.Count > 0) Persist();
    }

    public Workflow[] Snapshot() { lock (_gate) return [.. _items]; }
    public Workflow[] ForProject(string? projectId) { lock (_gate) return _items.Where(w => w.ProjectId == projectId).ToArray(); }

    /// <summary>Remove a deleted project's workflows.</summary>
    public void RemoveProject(string projectId)
    {
        int removed;
        lock (_gate) removed = _items.RemoveAll(w => w.ProjectId == projectId);
        if (removed > 0) Persist();
    }
    public string NextIcon() { lock (_gate) return IconLibrary.UniqueDefault(_items.Select(w => w.Icon)); }
    public Workflow? Get(string id) { lock (_gate) return _items.Find(w => w.Id == id); }

    /// <summary>Create (null id, in <paramref name="projectId"/>) or update a workflow (keeps its project).</summary>
    public void Save(string? id, string projectId, string name, AgentKind agent, string instructions, string color,
                     string icon, string? model, string? effort)
    {
        lock (_gate)
        {
            var w = id == null ? null : _items.Find(x => x.Id == id);
            if (w == null) _items.Add(w = new Workflow { ProjectId = projectId });
            w.Name = name;
            w.Agent = agent;
            w.Instructions = instructions;
            w.Color = color;
            w.Icon = IconLibrary.Exists(icon) ? icon : IconLibrary.UniqueDefault(_items.Select(x => x.Icon));
            w.Model = model;
            w.Effort = effort;
        }
        Persist();
    }

    public void Remove(string id)
    {
        lock (_gate) _items.RemoveAll(w => w.Id == id);
        Persist();
    }

    public void Move(string id, int newIndex)
    {
        lock (_gate)
        {
            var w = _items.Find(x => x.Id == id);
            if (w == null) return;
            _items.Remove(w);
            _items.Insert(Math.Clamp(newIndex, 0, _items.Count), w);
        }
        Persist();
    }

    void Persist()
    {
        try
        {
            string json;
            lock (_gate) json = JsonSerializer.Serialize(_items, Json);
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, json);
        }
        catch (Exception ex) { Log.Error("save workflows", ex); }
        Changed?.Invoke();
    }
}

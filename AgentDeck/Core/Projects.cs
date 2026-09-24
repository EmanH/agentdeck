using System.Text.Json;

namespace AgentDeck.Core;

public sealed class Project
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string Color { get; set; } = "#3b82f6";
    public string Icon { get; set; } = "";
}

/// <summary>Projects persisted to %APPDATA%\AgentDeck\projects.json. Thread-safe reads via Snapshot().</summary>
sealed class ProjectStore
{
    public static readonly string[] Palette =
        ["#3b82f6", "#22c55e", "#f59e0b", "#ef4444", "#a855f7", "#ec4899", "#14b8a6", "#f97316", "#eab308", "#64748b"];

    sealed class Data
    {
        public List<Project> Projects { get; set; } = [];
        public string? SelectedId { get; set; }
    }

    static readonly string FilePath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AgentDeck", "projects.json");
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    readonly object _gate = new();
    readonly Data _data;

    public ProjectStore()
    {
        try { _data = File.Exists(FilePath) ? JsonSerializer.Deserialize<Data>(File.ReadAllText(FilePath)) ?? new() : new(); }
        catch (Exception ex) { Log.Error("load projects", ex); _data = new(); }
        _data.SelectedId ??= _data.Projects.FirstOrDefault()?.Id;

        // Give projects from before icons existed their own icon.
        bool migrated = false;
        foreach (var p in _data.Projects.Where(p => !IconLibrary.Exists(p.Icon)))
        {
            p.Icon = IconLibrary.UniqueDefault(_data.Projects.Select(x => x.Icon));
            migrated = true;
        }
        if (migrated) Save();
    }

    public string NextIcon() { lock (_gate) return IconLibrary.UniqueDefault(_data.Projects.Select(p => p.Icon)); }

    public Project[] Snapshot() { lock (_gate) return [.. _data.Projects]; }
    public string? SelectedId { get { lock (_gate) return _data.SelectedId; } }
    public Project? Selected { get { lock (_gate) return _data.Projects.Find(p => p.Id == _data.SelectedId); } }
    public Project? Get(string id) { lock (_gate) return _data.Projects.Find(p => p.Id == id); }

    public string NextColor() { lock (_gate) return Palette[_data.Projects.Count % Palette.Length]; }

    public Project Add(string name, string path, string color, string icon)
    {
        var project = new Project { Name = name, Path = path, Color = color, Icon = icon };
        lock (_gate) { _data.Projects.Add(project); _data.SelectedId = project.Id; }
        Save();
        return project;
    }

    public void Update(string id, string name, string color, string icon)
    {
        lock (_gate)
        {
            var p = _data.Projects.Find(x => x.Id == id);
            if (p == null) return;
            p.Name = name;
            p.Color = color;
            p.Icon = icon;
        }
        Save();
    }

    public void Remove(string id)
    {
        lock (_gate)
        {
            int index = _data.Projects.FindIndex(p => p.Id == id);
            if (index < 0) return;
            _data.Projects.RemoveAt(index);
            if (_data.SelectedId == id)
                _data.SelectedId = _data.Projects.Count == 0 ? null : _data.Projects[Math.Min(index, _data.Projects.Count - 1)].Id;
        }
        Save();
    }

    public void Move(string id, int newIndex)
    {
        lock (_gate)
        {
            var p = _data.Projects.Find(x => x.Id == id);
            if (p == null) return;
            _data.Projects.Remove(p);
            _data.Projects.Insert(Math.Clamp(newIndex, 0, _data.Projects.Count), p);
        }
        Save();
    }

    public bool Select(string id)
    {
        lock (_gate)
        {
            if (_data.SelectedId == id || _data.Projects.All(p => p.Id != id)) return false;
            _data.SelectedId = id;
        }
        Save();
        return true;
    }

    void Save()
    {
        try
        {
            string json;
            lock (_gate) json = JsonSerializer.Serialize(_data, Json);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, json);
        }
        catch (Exception ex) { Log.Error("save projects", ex); }
    }
}

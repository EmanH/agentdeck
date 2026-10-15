using System.Text;
using System.Text.Json;

namespace AgentDeck.Core;

/// <summary>Which agent conversation file a terminal is writing. <see cref="Path"/> is null until the agent creates it.</summary>
public sealed record TranscriptRef(string Agent, string SessionId, string? Path, string Source);

/// <summary>One normalized transcript item. Kind is text, tool_use or tool_result.</summary>
public sealed record TranscriptEntry(DateTimeOffset? Timestamp, string Role, string Kind, string Text, string? ToolName = null);

public sealed record TranscriptActivity(string? LastAssistantText, string? LastToolName, string? LastToolDetail, DateTimeOffset? LastActivityAt);

/// <summary>
/// Finds and reads the conversation files Claude Code and Grok write, so the control API can show what each
/// terminal's agent is saying. AgentDeck starts both with <c>--session-id</c>, which names the file. When the
/// conversation moves to a new file (/clear, /resume, the agent restarted in the same terminal, or an agent typed
/// into a plain shell) the newest prompt typed into the terminal is looked up in the project's recent files.
/// <list type="bullet">
/// <item>Claude: %USERPROFILE%\.claude\projects\&lt;cwd with non-alphanumerics as '-'&gt;\&lt;id&gt;.jsonl</item>
/// <item>Grok: %USERPROFILE%\.grok\sessions\&lt;url-encoded cwd&gt;\&lt;id&gt;\chat_history.jsonl (no per-message
/// timestamps), plus prompt_history.jsonl per cwd listing each prompt with its session id.</item>
/// </list>
/// Thread-safe; does file IO, so call it off the UI thread.
/// </summary>
sealed class AgentTranscripts(SessionManager sessions, Func<string, string?> projectPath)
{
    static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    static readonly string ClaudeProjects = Path.Combine(Home, ".claude", "projects");
    static readonly string GrokSessions = Path.Combine(Home, ".grok", "sessions");

    const long DetectEveryMs = 4000;
    const int MinMatchChars = 10;              // shorter input ("yes", "1", "claude") could match any conversation
    static readonly TimeSpan InputSlack = TimeSpan.FromSeconds(5); // clock/rounding allowance between typing and the agent's timestamp
    const long PromptScanBytes = 512 * 1024;   // tail of a file searched for the terminal's recent prompts
    const long MessageScanBytes = 32L << 20;   // most a /messages call reads backwards
    const long ActivityScanBytes = 8L << 20;
    const int TextChars = 300, ToolDetailChars = 160, MessageChars = 2000;

    readonly object _gate = new();
    readonly Dictionary<string, (long Length, DateTime Write, List<Prompt> Prompts)> _prompts = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, (long Length, DateTime Write, TranscriptActivity Activity)> _activity = new(StringComparer.OrdinalIgnoreCase);

    public static bool Supports(AgentKind agent) => agent is AgentKind.Claude or AgentKind.Grok;

    public Session? Get(int id) => sessions.Get(id);

    sealed record Prompt(string SessionId, string Path, DateTimeOffset? At, string Text);

    // --- locating -----------------------------------------------------------------

    /// <summary>The session's transcript: the one it was launched with, or a newer one its recent input points to.</summary>
    public TranscriptRef? Find(Session s)
    {
        if (!Supports(s.Agent)) return null;
        var agent = s.Agent;
        lock (_gate)
        {
            if (s.Transcript is { } t && t.Agent != Name(agent)) s.Transcript = null; // the shell switched agents
            long now = Environment.TickCount64;
            if (now - s.TranscriptCheckedTicks < DetectEveryMs) return s.Transcript;
            s.TranscriptCheckedTicks = now;
            try
            {
                // Launched with a known id: resolve its path once the agent has created the file.
                if (s.LaunchTranscriptId is { } launchId && (s.Transcript == null || (s.Transcript.Path == null && s.Transcript.SessionId == launchId)))
                    s.Transcript = new TranscriptRef(Name(agent), launchId, PathForId(agent, launchId, s), "launch");
                if (Detect(s, agent) is { } found && found.SessionId != s.Transcript?.SessionId)
                {
                    Log.Info($"Session {s.Id} transcript -> {found.Path}");
                    s.Transcript = found;
                }
            }
            catch (Exception ex) { Log.Error($"session {s.Id} transcript lookup", ex); }
            if (_prompts.Count > 1000) _prompts.Clear();
            if (_activity.Count > 1000) _activity.Clear();
            return s.Transcript;
        }
    }

    static string Name(AgentKind agent) => agent.ToString().ToLowerInvariant();

    string Cwd(Session s)
    {
        var path = projectPath(s.ProjectId);
        return path != null && Directory.Exists(path) ? path : Home; // same fallback as SessionManager.Create
    }

    string? PathForId(AgentKind agent, string id, Session s)
    {
        if (agent == AgentKind.Claude)
        {
            // The expected folder first; Claude shortens very long folder names, so then any project folder.
            foreach (var dir in SafeDirs(ClaudeProjects).Prepend(ClaudeDir(Cwd(s))))
            {
                var file = Path.Combine(dir, id + ".jsonl");
                if (File.Exists(file)) return file;
            }
            return null;
        }
        foreach (var dir in GrokDirs(Cwd(s)).Concat(SafeDirs(GrokSessions)))
        {
            var file = Path.Combine(dir, id, "chat_history.jsonl");
            if (File.Exists(file)) return file;
        }
        return null;
    }

    static string ClaudeDir(string cwd) =>
        Path.Combine(ClaudeProjects, new string(cwd.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray()));

    /// <summary>Grok's per-cwd folders: the url-encoded path, sometimes with a \\?\ prefix.</summary>
    static IEnumerable<string> GrokDirs(string cwd)
    {
        var want = Path.TrimEndingDirectorySeparator(cwd);
        foreach (var dir in SafeDirs(GrokSessions))
        {
            var decoded = Uri.UnescapeDataString(Path.GetFileName(dir));
            if (decoded.StartsWith(@"\\?\", StringComparison.Ordinal)) decoded = decoded[4..];
            if (string.Equals(Path.TrimEndingDirectorySeparator(decoded), want, StringComparison.OrdinalIgnoreCase)) yield return dir;
        }
    }

    static IEnumerable<string> SafeDirs(string dir)
    {
        try { return Directory.Exists(dir) ? Directory.GetDirectories(dir) : []; }
        catch (Exception) { return []; }
    }

    /// <summary>
    /// The conversation holding the newest substantial line typed into this terminal, sent at or after it was
    /// typed (so an older conversation with the same words can't match). Keeps the current one when that line is
    /// in it; never takes a conversation another terminal owns. Null when nothing matches yet (the agent may not
    /// have written the prompt down yet).
    /// </summary>
    TranscriptRef? Detect(Session s, AgentKind agent)
    {
        var inputs = sessions.RecentInputs(s)
            .Select(i => (Text: Normalize(i.Text), At: new DateTimeOffset(i.At) - InputSlack))
            .Where(i => i.Text.Length >= MinMatchChars).Reverse().ToArray();
        if (inputs.Length == 0) return null;

        var claimed = sessions.Snapshot().Where(o => o.Id != s.Id)
            .SelectMany(o => new[] { o.LaunchTranscriptId, o.Transcript?.SessionId })
            .OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var since = inputs.Min(i => i.At);
        var prompts = agent == AgentKind.Claude ? ClaudePrompts(s, since) : GrokPrompts(s, since);

        foreach (var input in inputs)
        {
            var hits = prompts.Where(p => p.At >= input.At && (p.Text.Contains(input.Text, StringComparison.Ordinal)
                                          || (p.Text.Length >= MinMatchChars && input.Text.Contains(p.Text, StringComparison.Ordinal)))).ToList();
            if (hits.Count == 0) continue;
            if (s.Transcript is { } current && hits.Any(h => h.SessionId == current.SessionId)) return current;
            var best = hits.Where(h => !claimed.Contains(h.SessionId)).OrderByDescending(h => h.At ?? DateTimeOffset.MinValue).FirstOrDefault();
            return best == null ? null : new TranscriptRef(Name(agent), best.SessionId, best.Path, "detected");
        }
        return null;
    }

    List<Prompt> ClaudePrompts(Session s, DateTimeOffset since)
    {
        var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ClaudeDir(Cwd(s)) };
        if (s.Transcript?.Path is { } p) dirs.Add(Path.GetDirectoryName(p)!);
        var result = new List<Prompt>();
        foreach (var dir in dirs.Where(Directory.Exists))
            foreach (var file in new DirectoryInfo(dir).EnumerateFiles("*.jsonl").Where(f => f.LastWriteTimeUtc >= since.UtcDateTime))
                result.AddRange(CachedPrompts(file, ReadClaudePrompts).Where(pr => pr.At >= since));
        return result;
    }

    List<Prompt> GrokPrompts(Session s, DateTimeOffset since)
    {
        var result = new List<Prompt>();
        foreach (var dir in GrokDirs(Cwd(s)))
        {
            var file = new FileInfo(Path.Combine(dir, "prompt_history.jsonl"));
            if (file.Exists && file.LastWriteTimeUtc >= since.UtcDateTime)
                result.AddRange(CachedPrompts(file, ReadGrokPrompts).Where(pr => pr.At >= since));
        }
        return result;
    }

    List<Prompt> CachedPrompts(FileInfo file, Func<string, List<Prompt>> read)
    {
        if (_prompts.TryGetValue(file.FullName, out var c) && c.Length == file.Length && c.Write == file.LastWriteTimeUtc) return c.Prompts;
        var prompts = read(file.FullName);
        _prompts[file.FullName] = (file.Length, file.LastWriteTimeUtc, prompts);
        return prompts;
    }

    static List<Prompt> ReadClaudePrompts(string path)
    {
        var id = Path.GetFileNameWithoutExtension(path);
        var result = new List<Prompt>();
        foreach (var line in LinesBackward(path, PromptScanBytes))
        {
            if (!line.Contains("\"user\"") && !line.Contains("queue-operation")) continue;
            if (Parse(line) is not { } doc) continue;
            using (doc)
            {
                var o = doc.RootElement;
                var at = Time(o);
                string? text = Str(o, "type") switch
                {
                    "user" when !Bool(o, "isMeta") && !Bool(o, "isSidechain") && !Bool(o, "isCompactSummary") => ClaudeUserText(o),
                    "queue-operation" => Str(o, "content"), // typed while the agent was busy
                    _ => null,
                };
                if (at != null && !string.IsNullOrWhiteSpace(text)) result.Add(new Prompt(id, path, at, Normalize(text)));
            }
        }
        return result;
    }

    static List<Prompt> ReadGrokPrompts(string path)
    {
        var dir = Path.GetDirectoryName(path)!;
        var result = new List<Prompt>();
        foreach (var line in LinesBackward(path, PromptScanBytes))
        {
            if (Parse(line) is not { } doc) continue;
            using (doc)
            {
                var o = doc.RootElement;
                if (Str(o, "session_id") is { } id && Str(o, "prompt") is { Length: > 0 } text)
                    result.Add(new Prompt(id, Path.Combine(dir, id, "chat_history.jsonl"), Time(o), Normalize(text)));
            }
        }
        return result;
    }

    static string Normalize(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

    // --- reading ------------------------------------------------------------------

    /// <summary>The latest assistant text, tool call and activity time. Cached until the file changes.</summary>
    public TranscriptActivity? Activity(TranscriptRef t)
    {
        if (t.Path is not { } path || !File.Exists(path)) return null;
        var info = new FileInfo(path);
        lock (_gate)
            if (_activity.TryGetValue(path, out var c) && c.Length == info.Length && c.Write == info.LastWriteTimeUtc) return c.Activity;

        string? text = null, tool = null, detail = null;
        DateTimeOffset? at = null;
        foreach (var (e, _) in Entries(t, ActivityScanBytes))
        {
            at ??= e.Timestamp;
            if (text == null && e.Role == "assistant" && e.Kind == "text") text = Truncate(e.Text, TextChars);
            if (tool == null && e.Kind == "tool_use") { tool = e.ToolName; detail = e.Text; }
            if (text != null && tool != null) break;
        }
        // Grok's chat history has no timestamps: the file's last write stands in.
        var activity = new TranscriptActivity(text, tool, detail, at ?? new DateTimeOffset(info.LastWriteTimeUtc));
        lock (_gate) _activity[path] = (info.Length, info.LastWriteTimeUtc, activity);
        return activity;
    }

    /// <summary>The last <paramref name="limit"/> entries, oldest first.</summary>
    public List<TranscriptEntry> Messages(TranscriptRef t, int limit)
    {
        if (t.Path is not { } path || !File.Exists(path)) return [];
        var picked = new List<(TranscriptEntry Entry, string? CallId)>();
        var unnamed = new HashSet<string>(); // results whose call we haven't reached yet (reading backwards)
        var names = new Dictionary<string, string>();
        int extra = 0;
        foreach (var (entry, callId) in Entries(t, MessageScanBytes))
        {
            if (entry.Kind == "tool_use" && callId != null) { names[callId] = entry.ToolName ?? ""; unnamed.Remove(callId); }
            if (picked.Count < limit)
            {
                picked.Add((entry, callId));
                if (entry.Kind == "tool_result" && callId != null) unnamed.Add(callId);
            }
            else if (unnamed.Count == 0 || ++extra > 200) break; // a little further back to name the oldest results
        }
        picked.Reverse();
        return picked.Select(p => p.Entry with
        {
            Text = Truncate(p.Entry.Text, MessageChars),
            ToolName = p.Entry.Kind == "tool_result" && p.CallId != null && names.TryGetValue(p.CallId, out var n) ? n : p.Entry.ToolName,
        }).ToList();
    }

    /// <summary>Normalized entries with their tool call ids, newest first.</summary>
    static IEnumerable<(TranscriptEntry Entry, string? CallId)> Entries(TranscriptRef t, long maxBytes)
    {
        foreach (var line in LinesBackward(t.Path!, maxBytes))
        {
            if (Parse(line) is not { } doc) continue;
            List<(TranscriptEntry, string?)> items;
            using (doc) items = t.Agent == "grok" ? GrokEntries(doc.RootElement) : ClaudeEntries(doc.RootElement);
            for (int i = items.Count - 1; i >= 0; i--) yield return items[i]; // a line's items are in order
        }
    }

    static List<(TranscriptEntry, string?)> ClaudeEntries(JsonElement o)
    {
        var list = new List<(TranscriptEntry, string?)>();
        var type = Str(o, "type");
        if (type is not ("user" or "assistant") || Bool(o, "isMeta") || Bool(o, "isSidechain")) return list;
        if (!o.TryGetProperty("message", out var m) || !m.TryGetProperty("content", out var content)) return list;
        var at = Time(o);
        if (Bool(o, "isCompactSummary"))
        {
            list.Add((new TranscriptEntry(at, "user", "text", "[earlier conversation compacted into a summary]"), null));
            return list;
        }
        if (content.ValueKind == JsonValueKind.String)
        {
            if (CleanUserText(content.GetString()) is { } text) list.Add((new TranscriptEntry(at, type, "text", text), null));
            return list;
        }
        if (content.ValueKind != JsonValueKind.Array) return list;
        foreach (var b in content.EnumerateArray())
        {
            switch (Str(b, "type"))
            {
                case "text" when Str(b, "text") is { } text && (type == "assistant" ? text.Trim() : CleanUserText(text)) is { Length: > 0 } clean:
                    list.Add((new TranscriptEntry(at, type, "text", clean), null));
                    break;
                case "image":
                    list.Add((new TranscriptEntry(at, type, "text", "[image]"), null));
                    break;
                case "tool_use":
                    var name = Str(b, "name") ?? "tool";
                    list.Add((new TranscriptEntry(at, "assistant", "tool_use", b.TryGetProperty("input", out var input) ? ToolDetail(input) : "", name), Str(b, "id")));
                    break;
                case "tool_result":
                    var result = b.TryGetProperty("content", out var rc) ? ContentText(rc) : "";
                    if (Bool(b, "is_error")) result = "[error] " + result;
                    list.Add((new TranscriptEntry(at, "tool", "tool_result", Truncate(result, MessageChars)), Str(b, "tool_use_id")));
                    break;
            }
        }
        return list;
    }

    static string? ClaudeUserText(JsonElement o)
    {
        if (!o.TryGetProperty("message", out var m) || !m.TryGetProperty("content", out var c)) return null;
        if (c.ValueKind == JsonValueKind.String) return c.GetString();
        if (c.ValueKind != JsonValueKind.Array) return null;
        var texts = c.EnumerateArray().Where(b => Str(b, "type") == "text").Select(b => Str(b, "text")).OfType<string>().ToList();
        return texts.Count > 0 ? string.Join('\n', texts) : null;
    }

    /// <summary>Slash commands as "/name args"; Claude's bookkeeping (command output, caveats) dropped.</summary>
    static string? CleanUserText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim();
        if (text.StartsWith("<command-name>", StringComparison.Ordinal))
            return $"{Between(text, "<command-name>", "</command-name>")} {Between(text, "<command-args>", "</command-args>")}".Trim();
        if (text.StartsWith("<local-command-", StringComparison.Ordinal) || text.StartsWith("<system-reminder>", StringComparison.Ordinal)) return null;
        return text;
    }

    static List<(TranscriptEntry, string?)> GrokEntries(JsonElement o)
    {
        var list = new List<(TranscriptEntry, string?)>();
        switch (Str(o, "type"))
        {
            case "user" when !o.TryGetProperty("synthetic_reason", out _):
                var text = o.TryGetProperty("content", out var uc) ? ContentText(uc) : "";
                // The real prompt is wrapped in <user_query>; other user items are injected context.
                if (text.Contains("<user_query>")) list.Add((new TranscriptEntry(null, "user", "text", Between(text, "<user_query>", "</user_query>").Trim()), null));
                else if (!text.TrimStart().StartsWith('<') && text.Trim().Length > 0) list.Add((new TranscriptEntry(null, "user", "text", text.Trim()), null));
                break;
            case "assistant":
                if (o.TryGetProperty("content", out var ac) && ContentText(ac).Trim() is { Length: > 0 } said)
                    list.Add((new TranscriptEntry(null, "assistant", "text", said), null));
                if (o.TryGetProperty("tool_calls", out var calls) && calls.ValueKind == JsonValueKind.Array)
                    foreach (var call in calls.EnumerateArray())
                    {
                        var detail = "";
                        if (Str(call, "arguments") is { } args && Parse(args) is { } argDoc) using (argDoc) detail = ToolDetail(argDoc.RootElement);
                        list.Add((new TranscriptEntry(null, "assistant", "tool_use", detail, Str(call, "name") ?? "tool"), Str(call, "id")));
                    }
                break;
            case "tool_result":
                var result = o.TryGetProperty("content", out var rc) ? ContentText(rc) : "";
                list.Add((new TranscriptEntry(null, "tool", "tool_result", Truncate(result, MessageChars)), Str(o, "tool_call_id")));
                break;
        }
        return list;
    }

    /// <summary>A one-line summary of a tool call's input: its description, path, command or query.</summary>
    static string ToolDetail(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object) return "";
        string[] keys = ["description", "file_path", "path", "command", "pattern", "url", "query", "prompt", "skill", "subject", "target_file"];
        foreach (var key in keys)
            if (Str(input, key) is { Length: > 0 } v) return Truncate(OneLine(v), ToolDetailChars);
        foreach (var p in input.EnumerateObject())
            if (p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() is { Length: > 0 } v) return Truncate(OneLine(v), ToolDetailChars);
        return "";
    }

    static string ContentText(JsonElement c) => c.ValueKind switch
    {
        JsonValueKind.String => c.GetString() ?? "",
        JsonValueKind.Array => string.Join('\n', c.EnumerateArray().Select(b =>
            b.ValueKind == JsonValueKind.String ? b.GetString()
            : Str(b, "type") == "image" ? "[image]"
            : Str(b, "text")).OfType<string>()),
        _ => "",
    };

    // --- helpers ------------------------------------------------------------------

    /// <summary>
    /// A JSONL file's lines from the end, reading at most <paramref name="maxBytes"/> (big transcripts stay cheap).
    /// Opened with full sharing: the agent keeps writing while we read. A half-written last line fails to parse
    /// and is skipped by the callers.
    /// </summary>
    static IEnumerable<string> LinesBackward(string path, long maxBytes)
    {
        FileStream fs;
        try { fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); }
        catch (IOException) { yield break; }
        catch (UnauthorizedAccessException) { yield break; }
        using (fs)
        {
            long pos = fs.Length, floor = Math.Max(0, pos - maxBytes);
            var buf = new byte[64 * 1024];
            var parts = new List<byte[]>(); // the line being assembled, latest piece first
            while (pos > floor)
            {
                int n = (int)Math.Min(buf.Length, pos - floor);
                pos -= n;
                fs.Position = pos;
                fs.ReadExactly(buf, 0, n);
                int end = n;
                for (int i = n - 1; i >= 0; i--)
                {
                    if (buf[i] != (byte)'\n') continue;
                    parts.Add(buf[(i + 1)..end]);
                    if (Join(parts) is { Length: > 0 } line) yield return line;
                    parts.Clear();
                    end = i;
                }
                if (end > 0) parts.Add(buf[..end]);
            }
            if (pos == 0 && Join(parts) is { Length: > 0 } first) yield return first; // a partial line at the floor is dropped
        }
    }

    static string Join(List<byte[]> parts)
    {
        var bytes = new byte[parts.Sum(p => p.Length)];
        int at = 0;
        for (int i = parts.Count - 1; i >= 0; i--) { parts[i].CopyTo(bytes, at); at += parts[i].Length; }
        return Encoding.UTF8.GetString(bytes).Trim();
    }

    static JsonDocument? Parse(string json)
    {
        try { return JsonDocument.Parse(json); }
        catch (JsonException) { return null; }
    }

    static string? Str(JsonElement o, string name) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static bool Bool(JsonElement o, string name) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    static DateTimeOffset? Time(JsonElement o) =>
        DateTimeOffset.TryParse(Str(o, "timestamp"), System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var t) ? t : null;

    static string Between(string text, string open, string close)
    {
        int a = text.IndexOf(open, StringComparison.Ordinal);
        if (a < 0) return "";
        a += open.Length;
        int b = text.IndexOf(close, a, StringComparison.Ordinal);
        return b < 0 ? text[a..] : text[a..b];
    }

    static string OneLine(string s) => string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}

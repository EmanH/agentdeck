using System.Text;
using AgentDeck.Native;

namespace AgentDeck.Core;

public enum AgentKind { Shell, Claude, Codex, Grok }

public sealed class Session
{
    public required int Id { get; init; }
    public required string ProjectId { get; init; }
    public AgentKind Agent { get; set; }
    public string DefaultName { get; set; } = "";
    public string? Title { get; set; }     // useful terminal title (e.g. Claude Code's task title)
    public string? Summary { get; set; }   // AI two-word summary of recent input
    public long LastOutputTicks;           // Environment.TickCount64 of the last output
    public bool Done { get; set; }         // finished work the user hasn't looked at yet (the star)
    internal bool AwaitingWork;            // user submitted something; watching for work then quiet
    internal long WorkStartTicks;          // first output after that submit (0 = none yet)
    internal PseudoConsole Pty = null!;
    internal readonly StringBuilder Line = new();
    internal readonly List<string> Inputs = [];
    internal CancellationTokenSource? SummaryCts;

    public string Label => Title ?? Summary ?? DefaultName;
    public bool Busy => Environment.TickCount64 - Interlocked.Read(ref LastOutputTicks) < 1500;
}

/// <summary>Owns all live terminals. Events fire on background threads.</summary>
sealed class SessionManager
{
    public event Action<Session, string>? Output;
    public event Action<Session>? Exited;
    public event Action? Changed;
    /// <summary>A terminal worked after the user submitted something and has now gone quiet.</summary>
    public event Action<Session>? Finished;

    // Agents redraw a spinner/timer the whole time they (or their background agents) are working, so
    // "submitted, then output for a while, then silence" means finished or waiting for the user.
    const int MinWorkMs = 4000, QuietMs = 4000;

    readonly OpenAiClient openai;
    readonly Timer _finishTimer;

    public SessionManager(OpenAiClient openai)
    {
        this.openai = openai;
        _finishTimer = new Timer(_ => CheckFinished(), null, 500, 500);
    }

    const string SummaryInstructions =
        "You label terminal sessions for a tiny button. Given the user's recent commands or prompts, reply with " +
        "exactly two words in Title Case describing the task (e.g. \"Login Bug\", \"Deploy Script\"). No punctuation.";

    readonly object _gate = new();
    readonly List<Session> _sessions = [];
    int _nextId = 1;

    public Session[] Snapshot() { lock (_gate) return [.. _sessions]; }
    public Session[] ForProject(string projectId) { lock (_gate) return _sessions.Where(s => s.ProjectId == projectId).ToArray(); }
    public Session? Get(int id) { lock (_gate) return _sessions.Find(s => s.Id == id); }

    /// <param name="name">Label until the agent sets a title (e.g. the workflow's name).</param>
    /// <param name="prompt">Initial prompt: the agent starts with it already submitted.</param>
    /// <param name="model">Model override (null = the agent's default).</param>
    /// <param name="effort">Thinking level override (null = the agent's default).</param>
    public Session Create(Project project, AgentKind agent, string? name = null, string? prompt = null,
                          string? model = null, string? effort = null)
    {
        var cwd = Directory.Exists(project.Path) ? project.Path : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var exe = agent.ToString().ToLowerInvariant();
        var command = agent == AgentKind.Shell ? "powershell.exe -NoLogo"
            : prompt != null ? AgentWithPromptCommand(exe, prompt, AgentArgs(agent, model, effort))
            : $"powershell.exe -NoLogo -NoExit -Command {exe}"; // back to a shell when the agent quits

        Session session;
        lock (_gate)
        {
            session = new Session { Id = _nextId++, ProjectId = project.Id, Agent = agent };
            session.DefaultName = name ?? NextDefaultName(project.Id, agent);
            session.LastOutputTicks = Environment.TickCount64;
            _sessions.Add(session);
        }
        try
        {
            session.Pty = new PseudoConsole(command, cwd, 120, 30);
        }
        catch
        {
            lock (_gate) _sessions.Remove(session);
            throw;
        }
        session.Pty.Output += data =>
        {
            long now = Environment.TickCount64;
            Interlocked.Exchange(ref session.LastOutputTicks, now);
            if (session.AwaitingWork && Interlocked.Read(ref session.WorkStartTicks) == 0)
                Interlocked.Exchange(ref session.WorkStartTicks, now);
            Output?.Invoke(session, data);
        };
        session.Pty.Exited += () =>
        {
            lock (_gate) _sessions.Remove(session);
            session.SummaryCts?.Cancel();
            Exited?.Invoke(session);
            Changed?.Invoke();
        };
        session.Pty.Start();
        Log.Info($"Session {session.Id} started: {agent} in {cwd}");
        Changed?.Invoke();
        return session;
    }

    /// <summary>
    /// PowerShell command that starts the agent with an initial prompt (all three CLIs take one as their first
    /// argument). The prompt travels via a temp file so the command line stays short, and is escaped for
    /// Windows argv parsing: PowerShell 5.1 doesn't escape embedded quotes when calling native programs.
    /// </summary>
    static string AgentWithPromptCommand(string exe, string prompt, string args)
    {
        var dir = Path.Combine(Log.Dir, "prompts");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, $"{Guid.NewGuid():N}.txt");
        File.WriteAllText(file, prompt, new UTF8Encoding(false));
        var quoted = "'" + file.Replace("'", "''") + "'";
        var script =
            $"$p = [IO.File]::ReadAllText({quoted}); Remove-Item -LiteralPath {quoted}; " +
            "$p = $p -replace '(\\\\*)\"', '$1$1\\\"' -replace '(\\\\+)$', '$1$1'; " +
            $"& {exe} {args} $p";
        return "powershell.exe -NoLogo -NoExit -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
    }

    /// <summary>Each CLI's own flags for model and thinking level. Values are restricted to a safe charset.</summary>
    static string AgentArgs(AgentKind agent, string? model, string? effort)
    {
        static bool Safe(string? v) => !string.IsNullOrEmpty(v) && System.Text.RegularExpressions.Regex.IsMatch(v, @"^[A-Za-z0-9._:\[\]\-]+$");
        var args = new List<string>();
        if (Safe(model)) args.Add(agent == AgentKind.Claude ? $"--model {model}" : $"-m {model}");
        if (Safe(effort))
            args.Add(agent switch
            {
                AgentKind.Claude => $"--effort {effort}",
                AgentKind.Codex => $"-c model_reasoning_effort={effort}",
                _ => $"--reasoning-effort {effort}",
            });
        return string.Join(' ', args);
    }

    string NextDefaultName(string projectId, AgentKind agent)
    {
        var name = agent.ToString();
        int n = _sessions.Count(s => s.ProjectId == projectId && s.Agent == agent) + 1;
        return n == 1 ? name : $"{name} {n}";
    }

    public void Write(int id, string data)
    {
        var s = Get(id);
        if (s == null) return;
        s.Pty.Write(data);
        TrackInput(s, data);
    }

    void CheckFinished()
    {
        long now = Environment.TickCount64;
        foreach (var s in Snapshot())
        {
            long start = Interlocked.Read(ref s.WorkStartTicks);
            if (!s.AwaitingWork || start == 0) continue;
            long last = Interlocked.Read(ref s.LastOutputTicks);
            if (now - last < QuietMs) continue;
            s.AwaitingWork = false;
            Interlocked.Exchange(ref s.WorkStartTicks, 0);
            if (last - start >= MinWorkMs) Finished?.Invoke(s); // quick replies don't count
        }
    }

    /// <summary>The user looked at this terminal: clear its star.</summary>
    public void MarkSeen(int id)
    {
        var s = Get(id);
        if (s is not { Done: true }) return;
        s.Done = false;
        Changed?.Invoke();
    }

    public void Resize(int id, int cols, int rows) => Get(id)?.Pty.Resize(cols, rows);
    public void Close(int id) => Get(id)?.Pty.Dispose();

    public void CloseProject(string projectId)
    {
        foreach (var s in ForProject(projectId)) s.Pty.Dispose();
    }

    public void CloseAll()
    {
        foreach (var s in Snapshot()) s.Pty.Dispose();
    }

    public void SetTitle(int id, string title)
    {
        var s = Get(id);
        if (s == null) return;
        var useful = UsefulTitle(title);
        if (useful == s.Title) return;
        s.Title = useful;
        Changed?.Invoke();
    }

    /// <summary>Turn a terminal title into a short label, or null if it's just a program name or path.</summary>
    static string? UsefulTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        var t = title.Trim();
        int start = 0;
        while (start < t.Length && !char.IsLetterOrDigit(t[start])) start++; // drop "✳ " / spinner glyphs
        t = t[start..].Trim();
        if (t.Length < 2) return null;
        var lower = t.ToLowerInvariant();
        string[] boring = ["claude", "claude code", "codex", "grok", "grok build", "windows powershell", "powershell",
                           "administrator: windows powershell"];
        if (boring.Contains(lower) || lower.EndsWith(".exe") || t.Contains('\\') || t.Contains(":/") || t.StartsWith('/'))
            return null;
        var words = t.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', words.Take(3));
    }

    /// <summary>Reconstruct typed lines from raw terminal input (best effort) for agent detection and summaries.</summary>
    void TrackInput(Session s, string data)
    {
        if (s.Done) { s.Done = false; Changed?.Invoke(); } // typing into it counts as seen

        // Enter, or a bare digit (agent menus such as permission prompts), starts a unit of work.
        if (data.Contains('\r') || (data.Length == 1 && char.IsDigit(data[0])))
        {
            s.AwaitingWork = true;
            Interlocked.Exchange(ref s.WorkStartTicks, 0);
        }

        if (data.Contains("\x1b[200~"))
            data = data.Replace("\x1b[200~", "").Replace("\x1b[201~", ""); // bracketed paste
        else if (data.Contains('\x1b'))
            return; // arrow keys and other escape sequences

        foreach (var ch in data)
        {
            if (ch is '\r' or '\n') CommitLine(s);
            else if (ch is '\x7f' or '\b') { if (s.Line.Length > 0) s.Line.Length--; }
            else if (ch >= ' ') s.Line.Append(ch);
        }
    }

    void CommitLine(Session s)
    {
        var line = s.Line.ToString().Trim();
        s.Line.Clear();
        if (line.Length == 0) return;
        lock (_gate)
        {
            s.Inputs.Add(line.Length > 300 ? line[..300] : line);
            if (s.Inputs.Count > 8) s.Inputs.RemoveAt(0);
        }

        // Typing "claude" / "codex" / "grok" in a plain shell turns it into that agent's session.
        if (s.Agent == AgentKind.Shell)
        {
            var first = line.Split(' ')[0].ToLowerInvariant();
            AgentKind? detected = first switch { "claude" => AgentKind.Claude, "codex" => AgentKind.Codex, "grok" => AgentKind.Grok, _ => null };
            if (detected is { } agent)
            {
                s.Agent = agent;
                lock (_gate) s.DefaultName = NextDefaultName(s.ProjectId, agent);
                Changed?.Invoke();
            }
        }

        if (s.Title == null && openai.Enabled && line.Length >= 3) ScheduleSummary(s);
    }

    void ScheduleSummary(Session s)
    {
        s.SummaryCts?.Cancel();
        var cts = s.SummaryCts = new CancellationTokenSource();
        string[] inputs;
        lock (_gate) inputs = [.. s.Inputs];
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(1200, cts.Token); // wait for a burst of input to settle
                var prompt = $"Agent: {s.Agent}\nRecent input, oldest first:\n" + string.Join('\n', inputs.Select(i => "- " + i));
                var words = await openai.RespondAsync(SummaryInstructions, prompt, TimeSpan.FromSeconds(6), cts.Token);
                words = string.Join(' ', words.Trim('"', '.', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(3));
                if (words.Length > 0 && !cts.IsCancellationRequested)
                {
                    s.Summary = words;
                    Changed?.Invoke();
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log.Error("summary", ex); }
        });
    }
}

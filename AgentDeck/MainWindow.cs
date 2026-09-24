using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using AgentDeck.Core;
using AgentDeck.Deck;
using AgentDeck.Dictation;
using AgentDeck.Native;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace AgentDeck;

/// <summary>
/// The app shell: a native window hosting the web UI (sidebar, tabs, xterm.js panes), the bridge between
/// that UI and the terminals, plus the Stream Deck, dictation, global shortcut and tray icon.
/// Closing the window hides it to the tray; everything keeps running until Quit.
/// </summary>
sealed class MainWindow : Window, IDeckActions
{
    const int ChromeRgb = 0x161616;
    static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    readonly WebView2 _web = new() { DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 12, 12, 12) };
    readonly ProjectStore _projects = new();
    readonly TranscriptStore _transcripts = new();
    readonly WorkflowStore _workflows = new();
    readonly OpenAiClient _openai = new(Env.Get("OPENAI_API_KEY"));
    readonly SessionManager _sessions;
    readonly DictationService _dictation;
    readonly DeckController _deck;
    readonly TrayIcon _tray;
    readonly ModifierHotkey _hotkey;
    readonly ConcurrentDictionary<string, int> _activeSession = new();
    readonly Dictionary<int, StringBuilder> _pendingOutput = [];
    readonly List<string> _outbox = [];
    readonly DispatcherTimer _flushTimer;
    bool _webReady, _stateQueued, _quitting, _hideWhenReady;
    IntPtr _hwnd;

    public MainWindow(bool startHidden)
    {
        Title = "AgentDeck";
        Width = 1400;
        Height = 860;
        MinWidth = 640;
        MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x16, 0x16, 0x16));
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
        if (File.Exists(iconPath)) Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri(iconPath));
        Content = _web;

        _sessions = new SessionManager(_openai);
        _sessions.Output += OnSessionOutput;
        _sessions.Exited += s => Dispatcher.BeginInvoke(() => Post(new { t = "exited", id = s.Id }));
        _sessions.Changed += QueueState;
        _sessions.Finished += s => Dispatcher.BeginInvoke(() => OnSessionFinished(s));
        Activated += (_, _) =>
        {
            // Coming back to the window counts as seeing the terminal on screen.
            if (_projects.SelectedId is { } pid && ActiveSessionId(pid) is { } sid) _sessions.MarkSeen(sid);
        };

        _transcripts.Changed += () => Dispatcher.BeginInvoke(PushTranscripts);
        _dictation = new DictationService(Env.Get("SONIOX_API_KEY"), _openai, _transcripts, CaptureDictationTarget, DeliverDictationAsync);
        _workflows.Changed += () => Dispatcher.BeginInvoke(PushWorkflows);
        AgentCatalog.Changed += () => Dispatcher.BeginInvoke(PushAgentOptions);
        AgentCatalog.Refresh();
        _deck = new DeckController(_projects, _sessions, _dictation, _transcripts, _workflows, this);
        _hotkey = new ModifierHotkey(() => Dispatcher.BeginInvoke(_dictation.Toggle));
        _tray = new TrayIcon(ShowFromTray, Quit);

        FirstRunSetup();

        _flushTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(8), DispatcherPriority.Normal, (_, _) => FlushOutput(), Dispatcher);
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            Input.UseDarkTitleBar(_hwnd, ChromeRgb);
        };
        Loaded += async (_, _) => await InitWebAsync();

        // WebView2 only initialises inside a shown window, so a background start shows it minimised and
        // off the taskbar, then hides it once the UI has loaded.
        if (startHidden)
        {
            _hideWhenReady = true;
            ShowInTaskbar = false;
            ShowActivated = false;
            WindowState = WindowState.Minimized;
        }
        Show();
    }

    static void FirstRunSetup()
    {
        var marker = Path.Combine(Log.Dir, "first-run.done");
        if (File.Exists(marker)) return;
        TrayIcon.SetStartWithWindows(true);
        File.WriteAllText(marker, DateTime.Now.ToString("O"));
    }

    // --- window lifecycle -------------------------------------------------------

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_quitting)
        {
            e.Cancel = true; // close = back to the tray; terminals keep running
            Hide();
        }
        base.OnClosing(e);
    }

    public void ShowFromTray()
    {
        ShowInTaskbar = true;
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Input.BringToFront(new WindowInteropHelper(this).Handle);
        Activate();
        _web.Focus();
    }

    void Quit()
    {
        int open = _sessions.Snapshot().Length;
        if (open > 0)
        {
            ShowFromTray();
            var answer = MessageBox.Show(this, $"Quit AgentDeck and close {open} running terminal{(open == 1 ? "" : "s")}?",
                                         "AgentDeck", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (answer != MessageBoxResult.OK) return;
        }
        _quitting = true;
        Log.Info("Quitting");
        _flushTimer.Stop();
        _hotkey.Dispose();
        _deck.Dispose();
        _sessions.CloseAll();
        _tray.Dispose();
        Close();
        Application.Current.Shutdown();
    }

    // --- web UI -----------------------------------------------------------------

    async Task InitWebAsync()
    {
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(Log.Dir, "WebView2"));
            await _web.EnsureCoreWebView2Async(env);
            var core = _web.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false; // no F5 reload / Ctrl+F find inside the terminal
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDevToolsEnabled = Debugger.IsAttached;
            core.SetVirtualHostNameToFolderMapping("agentdeck.example", Path.Combine(AppContext.BaseDirectory, "wwwroot"),
                                                   CoreWebView2HostResourceAccessKind.Allow);
            core.PermissionRequested += (_, e) =>
            {
                if (e.PermissionKind == CoreWebView2PermissionKind.ClipboardRead) e.State = CoreWebView2PermissionState.Allow;
            };
            core.WebMessageReceived += (_, e) => OnWebMessage(e.WebMessageAsJson);
            core.Navigate("https://agentdeck.example/index.html");
        }
        catch (Exception ex)
        {
            Log.Error("WebView2 init", ex);
            MessageBox.Show($"Could not start the terminal view:\n{ex.Message}", "AgentDeck");
        }
    }

    void Post(object message)
    {
        var json = JsonSerializer.Serialize(message, JsonOpts);
        if (!_webReady) { _outbox.Add(json); return; }
        _web.CoreWebView2.PostWebMessageAsJson(json);
    }

    void QueueState()
    {
        if (_stateQueued) return;
        _stateQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _stateQueued = false;
            PushState();
        });
    }

    void PushState()
    {
        var sessions = _sessions.Snapshot().OrderBy(s => s.Id)
            .Select(s => new { s.Id, s.ProjectId, Agent = s.Agent.ToString().ToLowerInvariant(), s.Label, s.Done });
        Post(new
        {
            t = "state",
            projects = _projects.Snapshot(),
            selected = _projects.SelectedId,
            sessions,
            palette = ProjectStore.Palette,
        });
    }

    /// <summary>An agent (or command) finished: chime, and star it unless it's the terminal on screen right now.</summary>
    void OnSessionFinished(Session session)
    {
        if (_sessions.Get(session.Id) == null) return; // closed meanwhile
        Chime.Play();
        bool onScreen = IsActive && session.ProjectId == _projects.SelectedId && ActiveSessionId(session.ProjectId) == session.Id;
        Log.Info($"Session {session.Id} finished ({session.Label}){(onScreen ? " on screen" : "")}");
        if (onScreen) return;
        session.Done = true;
        QueueState();
    }

    void PushWorkflows() =>
        Post(new
        {
            t = "workflows",
            items = _workflows.Snapshot().Select(w => new
            {
                w.Id, w.ProjectId, w.Name, Agent = w.Agent.ToString().ToLowerInvariant(), w.Instructions, w.Color, w.Icon, w.Model, w.Effort,
            }),
        });

    void PushAgentOptions() => Post(new { t = "agentOptions", agents = AgentCatalog.Current });

    void PushTranscripts() =>
        Post(new { t = "transcripts", items = _transcripts.Snapshot().Select(x => new { at = x.At.ToString("O"), x.Text }) });

    void OnSessionOutput(Session session, string data)
    {
        lock (_pendingOutput)
        {
            if (!_pendingOutput.TryGetValue(session.Id, out var sb)) _pendingOutput[session.Id] = sb = new StringBuilder();
            sb.Append(data);
        }
    }

    /// <summary>Batch terminal output into one message per session every few ms (much faster than per chunk).</summary>
    void FlushOutput()
    {
        if (!_webReady) return;
        List<(int id, string data)> batch;
        lock (_pendingOutput)
        {
            if (_pendingOutput.Count == 0) return;
            batch = _pendingOutput.Select(kv => (kv.Key, kv.Value.ToString())).ToList();
            _pendingOutput.Clear();
        }
        foreach (var (id, data) in batch) Post(new { t = "output", id, data });
    }

    void OnWebMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var m = doc.RootElement;
            string Str(string name) => m.GetProperty(name).GetString() ?? "";
            int Int(string name) => m.GetProperty(name).GetInt32();
            string? Opt(string name) =>
                m.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } x ? x : null;

            switch (Str("t"))
            {
                case "ready":
                    _webReady = true;
                    foreach (var queued in _outbox) _web.CoreWebView2.PostWebMessageAsJson(queued);
                    _outbox.Clear();
                    PushState();
                    PushTranscripts();
                    PushWorkflows();
                    PushAgentOptions();
                    if (_hideWhenReady)
                    {
                        _hideWhenReady = false;
                        Hide();
                        WindowState = WindowState.Normal;
                        ShowActivated = true;
                    }
                    break;
                case "input": _sessions.Write(Int("id"), Str("data")); break;
                case "resize": _sessions.Resize(Int("id"), Int("cols"), Int("rows")); break;
                case "title": _sessions.SetTitle(Int("id"), Str("title")); break;
                case "close": _sessions.Close(Int("id")); break;
                case "focus":
                    var focused = _sessions.Get(Int("id"));
                    if (focused != null)
                    {
                        _activeSession[focused.ProjectId] = focused.Id;
                        _sessions.MarkSeen(focused.Id);
                    }
                    break;
                case "newSession":
                    var agent = Enum.Parse<AgentKind>(Str("agent"), ignoreCase: true);
                    var projectId = m.TryGetProperty("projectId", out var pid) ? pid.GetString() : null;
                    int? relativeTo = m.TryGetProperty("relativeTo", out var rel) && rel.ValueKind == JsonValueKind.Number ? rel.GetInt32() : null;
                    StartSession(projectId, agent, m.TryGetProperty("placement", out var pl) ? pl.GetString() ?? "tab" : "tab", relativeTo);
                    break;
                case "selectProject":
                    if (_projects.Select(Str("id"))) PushState();
                    break;
                case "addProject": AddProject(); break;
                case "saveProject":
                    var id = m.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                    var icon = IconLibrary.Exists(Opt("icon")) ? Opt("icon")! : _projects.NextIcon();
                    if (id == null) _projects.Add(Str("name"), Str("path"), Str("color"), icon);
                    else _projects.Update(id, Str("name"), Str("color"), icon);
                    PushState();
                    break;
                case "removeProject":
                    _sessions.CloseProject(Str("id"));
                    _workflows.RemoveProject(Str("id"));
                    _projects.Remove(Str("id"));
                    PushState();
                    break;
                case "moveProject":
                    _projects.Move(Str("id"), Int("index"));
                    PushState();
                    break;
                case "saveWorkflow":
                    var wfId = m.TryGetProperty("id", out var wfIdEl) ? wfIdEl.GetString() : null;
                    var wfProject = Opt("projectId") ?? _projects.SelectedId;
                    if (wfId == null && _projects.Get(wfProject ?? "") == null) break; // new workflows need a project
                    _workflows.Save(wfId, wfProject!, Str("name"), Enum.Parse<AgentKind>(Str("agent"), ignoreCase: true), Str("instructions"),
                                    Str("color"), Opt("icon") ?? "", Opt("model"), Opt("effort"));
                    break;
                case "refreshAgentOptions": AgentCatalog.Refresh(); break;
                case "deleteWorkflow": _workflows.Remove(Str("id")); break;
                case "moveWorkflow": _workflows.Move(Str("id"), Int("index")); break;
                case "runWorkflow": RunWorkflowNow(Str("id")); break;
                case "openFolder":
                    var project = _projects.Get(Str("id"));
                    if (project != null && Directory.Exists(project.Path)) Process.Start("explorer.exe", project.Path);
                    break;
                case "openUrl":
                    var uri = Str("uri");
                    if (uri.StartsWith("http://") || uri.StartsWith("https://"))
                        Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error($"web message {json[..Math.Min(json.Length, 200)]}", ex);
        }
    }

    /// <summary>Open the workflow's agent in its own project with its instructions already submitted.</summary>
    void RunWorkflowNow(string workflowId)
    {
        var workflow = _workflows.Get(workflowId);
        if (workflow == null || _projects.Get(workflow.ProjectId) is not { } project) return;
        if (_projects.Select(project.Id)) PushState();
        Log.Info($"Running workflow {workflow.Name} in {project.Name} ({workflow.Agent}, model {workflow.Model ?? "default"}, effort {workflow.Effort ?? "default"})");
        if (StartSession(project.Id, workflow.Agent, "tab", null, workflow.Name, workflow.Instructions, workflow.Model, workflow.Effort) is { } session)
        {
            Post(new { t = "activate", id = session.Id });
            ShowFromTray();
        }
    }

    Session? StartSession(string? projectId, AgentKind agent, string placement, int? relativeTo,
                          string? name = null, string? prompt = null, string? model = null, string? effort = null)
    {
        var project = projectId != null ? _projects.Get(projectId) : _projects.Selected;
        if (project == null) return null;
        try
        {
            var session = _sessions.Create(project, agent, name, prompt, model, effort);
            _activeSession[project.Id] = session.Id;
            Post(new { t = "created", id = session.Id, projectId = project.Id, agent = agent.ToString().ToLowerInvariant(), placement, relativeTo });
            PushState();
            return session;
        }
        catch (Exception ex)
        {
            Log.Error($"start {agent}", ex);
            MessageBox.Show(this, $"Couldn't start {agent}:\n{ex.Message}", "AgentDeck");
            return null;
        }
    }

    void AddProject()
    {
        ShowFromTray();
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Choose a project folder" };
        if (dialog.ShowDialog(this) != true) return;
        var path = dialog.FolderName;
        Post(new { t = "projectDialog", mode = "add", path, name = Path.GetFileName(path.TrimEnd('\\')), color = _projects.NextColor(), icon = _projects.NextIcon() });
    }

    /// <summary>The terminal on screen when dictation starts, if AgentDeck is the app in front (any thread).</summary>
    int? CaptureDictationTarget()
    {
        if (!Input.IsForeground(_hwnd) || _projects.SelectedId is not { } pid) return null;
        var target = ActiveSessionId(pid);
        if (target != null) Log.Info($"Dictation target: session {target}");
        return target;
    }

    /// <summary>
    /// Dictation dictated from an AgentDeck terminal goes straight back into that terminal (no clipboard,
    /// wherever focus is now), and is submitted with Enter if the user has moved away from it. Otherwise
    /// it's pasted into whatever app has focus.
    /// </summary>
    async Task DeliverDictationAsync(int? target, string text, bool submitRequested)
    {
        if (target is int id && _sessions.Get(id) is { } session)
        {
            bool onScreen = Input.IsForeground(_hwnd) && _projects.SelectedId == session.ProjectId &&
                            ActiveSessionId(session.ProjectId) == id;
            bool submit = submitRequested || !onScreen;
            Log.Info($"Dictation delivered to session {id}{(submit ? " + Enter" : "")}");
            await Dispatcher.InvokeAsync(() => Post(new { t = "pasteInto", id, text, submit }));
            return;
        }
        await PasteAsync(text);
        if (submitRequested)
        {
            await Task.Delay(200); // gap so the app sees the paste and the Enter separately
            Input.Tap(Input.VK_RETURN);
        }
    }

    async Task PasteAsync(string text)
    {
        await Dispatcher.InvokeAsync(() =>
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try { Clipboard.SetText(text); return; }
                catch (System.Runtime.InteropServices.COMException) { Thread.Sleep(30); } // clipboard briefly locked by another app
            }
        });
        await Task.Run(() => Input.WaitForModifiersReleased()); // Ctrl+Shift+Win still held would make Ctrl+V into Win+V
        await Task.Delay(50);
        Input.Tap(Input.VK_V, Input.VK_CONTROL);
    }

    // --- IDeckActions (called from the deck thread) -----------------------------

    public void SelectProject(string projectId) => Dispatcher.BeginInvoke(() =>
    {
        if (_projects.Select(projectId)) PushState();
        ShowFromTray(); // also reopens the window if it's hidden in the tray
    });

    public void ActivateSession(int sessionId) => Dispatcher.BeginInvoke(() =>
    {
        var session = _sessions.Get(sessionId);
        if (session == null) return;
        _projects.Select(session.ProjectId);
        _activeSession[session.ProjectId] = session.Id;
        _sessions.MarkSeen(session.Id);
        PushState();
        Post(new { t = "activate", id = session.Id });
        ShowFromTray();
    });

    public void RunWorkflow(string workflowId) => Dispatcher.BeginInvoke(() => RunWorkflowNow(workflowId));

    public void NewWorkflow() => Dispatcher.BeginInvoke(() =>
    {
        ShowFromTray();
        Post(new { t = "workflowEditor" });
    });

    public void PasteLastTranscript()
    {
        if (_transcripts.Latest is { } last) _ = PasteAsync(last.Text);
    }

    public void CloseSession(int sessionId) => Dispatcher.BeginInvoke(() => _sessions.Close(sessionId));

    public void Launch(AgentKind agent) => Dispatcher.BeginInvoke(() =>
    {
        if (StartSession(null, agent, "tab", null) is { } session)
        {
            Post(new { t = "activate", id = session.Id });
            ShowFromTray();
        }
    });

    void IDeckActions.AddProject() => Dispatcher.BeginInvoke(AddProject);
    /// <summary>Deck Enter: mid-dictation it stops and submits after the paste; otherwise a plain Enter.</summary>
    public void PressEnter()
    {
        if (!_dictation.StopAndSubmit()) Input.Tap(Input.VK_RETURN);
    }
    public void ToggleDictation() => _dictation.Toggle();
    public int? ActiveSessionId(string projectId) => _activeSession.TryGetValue(projectId, out var id) ? id : null;
}

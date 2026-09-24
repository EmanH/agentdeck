using AgentDeck.Core;
using AgentDeck.Dictation;
using OpenMacroBoard.SDK;
using SkiaSharp;

namespace AgentDeck.Deck;

/// <summary>What the deck can ask the app to do. Implementations marshal to the UI thread.</summary>
interface IDeckActions
{
    void SelectProject(string projectId);
    void ActivateSession(int sessionId);
    void CloseSession(int sessionId);
    void PasteLastTranscript();
    void RunWorkflow(string workflowId);
    void NewWorkflow();
    void Launch(AgentKind agent);
    void AddProject();
    void PressEnter();
    void ToggleDictation();
    int? ActiveSessionId(string projectId);
}

/// <summary>
/// Keeps the Stream Deck in sync with the app at ~30 fps, redrawing only keys whose content changed.
/// Normal layout (5x3): left column = projects (bottom-left becomes "more" past three), keys 14/15 =
/// Enter/Mic, the rest = the selected project's terminals then "+ agent" launchers. "More" opens a
/// temporary project picker across the deck with a back key.
/// </summary>
sealed class DeckController : IDisposable
{
    static readonly int[] ProjectKeys = [0, 5, 10];
    static readonly int[] SlotKeys = [1, 2, 3, 4, 6, 7, 8, 9, 11];
    static readonly int[] PickerKeys = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 11, 12];
    const int MoreKey = 10, RepasteKey = 11, WorkflowsKey = 12, EnterKey = 13, MicKey = 14, KeyCount = 15;
    const int RepasteMs = 10_000; // "paste again" shows this long after a transcript (or its last press)
    const int HoldMs = 800, HoldVisibleAfterMs = 150, PickerTimeoutMs = 10_000;
    static readonly AgentKind[] LaunchAgents = [AgentKind.Claude, AgentKind.Codex, AgentKind.Grok];

    readonly ProjectStore _projects;
    readonly SessionManager _sessions;
    readonly DictationService _dictation;
    readonly TranscriptStore _transcripts;
    readonly WorkflowStore _workflows;
    readonly IDeckActions _actions;
    long _repastePressedAt;
    readonly Thread _thread;
    volatile bool _stop;

    IMacroBoard? _board;
    long _nextConnectAttempt;
    volatile KeyView[] _views = [];
    long _enterPressedAt = -1;

    // Press-and-hold tracking, per key.
    readonly object _keyGate = new();
    readonly long[] _downAt = Enumerable.Repeat(-1L, KeyCount).ToArray();
    readonly KeyView?[] _heldView = new KeyView?[KeyCount];

    // Project picker ("more") and workflow list modes; both close after a few idle seconds.
    volatile bool _pickerOpen, _workflowsOpen;
    int _pickerPage, _workflowPage;
    long _pickerTouched;

    /// <param name="Press">Fires immediately on key-down.</param>
    /// <param name="Hold">Extra long-press action, fired if the key is still down after <see cref="HoldMs"/>.</param>
    readonly record struct KeyView(string Signature, Action<SKCanvas> Draw, Action? Press = null, Action? Hold = null);

    public DeckController(ProjectStore projects, SessionManager sessions, DictationService dictation,
                          TranscriptStore transcripts, WorkflowStore workflows, IDeckActions actions)
    {
        _projects = projects;
        _sessions = sessions;
        _dictation = dictation;
        _transcripts = transcripts;
        _workflows = workflows;
        _actions = actions;
        _thread = new Thread(Loop) { IsBackground = true, Name = "deck" };
        _thread.Start();
    }

    void Loop()
    {
        var shown = new string?[KeyCount];
        while (!_stop)
        {
            long frameStart = Environment.TickCount64;
            try
            {
                if (_board == null && frameStart >= _nextConnectAttempt)
                {
                    Connect();
                    Array.Clear(shown);
                }
                if (_board is { } board)
                {
                    if (!board.IsConnected) throw new IOException("Stream Deck disconnected");
                    FireHolds(frameStart);
                    if (frameStart - _pickerTouched > PickerTimeoutMs) _pickerOpen = _workflowsOpen = false;

                    var views = BuildViews();
                    _views = views;
                    for (int i = 0; i < KeyCount && i < board.Keys.Count; i++)
                    {
                        if (views[i].Signature == shown[i]) continue;
                        board.SetKeyBitmap(i, KeyArt.Render(views[i].Draw));
                        shown[i] = views[i].Signature;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("deck", ex);
                DisposeBoard();
                _nextConnectAttempt = Environment.TickCount64 + 3000;
            }
            int wait = 33 - (int)(Environment.TickCount64 - frameStart);
            if (wait > 0) Thread.Sleep(wait);
        }
    }

    void Connect()
    {
        try
        {
            var board = StreamDeckSharp.StreamDeck.OpenDevice();
            board.SetBrightness(70);
            board.KeyStateChanged += (_, e) => OnKey(e.Key, e.IsDown);
            _board = board;
            Log.Info($"Stream Deck connected ({board.Keys.Count} keys)");
        }
        catch (Exception ex)
        {
            if (_nextConnectAttempt == 0) Log.Info($"No Stream Deck yet ({ex.GetType().Name}: {ex.Message}); retrying.");
            _nextConnectAttempt = Environment.TickCount64 + 3000;
        }
    }

    // --- input --------------------------------------------------------------------

    void OnKey(int key, bool down)
    {
        var views = _views;
        if (key >= KeyCount || key >= views.Length) return;
        long now = Environment.TickCount64;
        _pickerTouched = now;
        Action? fire = null;

        lock (_keyGate)
        {
            if (down)
            {
                // Press always acts immediately, so taps are never lost; a hold is an extra gesture on top.
                var view = views[key];
                if (key == EnterKey) _enterPressedAt = now;
                fire = view.Press;
                if (view.Hold != null) { _downAt[key] = now; _heldView[key] = view; }
            }
            else
            {
                _downAt[key] = -1; // released before the hold fired
                _heldView[key] = null;
            }
        }
        if (down) Log.Info($"Deck key {key + 1}: {views[key].Signature.Split('|')[0]}");
        fire?.Invoke();
    }

    void FireHolds(long now)
    {
        var fire = new List<Action>();
        lock (_keyGate)
        {
            for (int k = 0; k < KeyCount; k++)
            {
                if (_downAt[k] < 0 || now - _downAt[k] < HoldMs) continue;
                if (_heldView[k]?.Hold is { } hold) fire.Add(hold);
                _downAt[k] = -1; // the release that follows is ignored
                _heldView[k] = null;
            }
        }
        foreach (var action in fire) action();
    }

    /// <summary>0..1 progress of an in-flight hold on this key, or 0 if none (or too short to show yet).</summary>
    float HoldProgress(int key, long now)
    {
        lock (_keyGate)
        {
            if (_downAt[key] < 0) return 0;
            float held = now - _downAt[key] - HoldVisibleAfterMs;
            return Math.Clamp(held / (HoldMs - HoldVisibleAfterMs), 0, 1);
        }
    }

    // --- layout -------------------------------------------------------------------

    KeyView[] BuildViews()
    {
        var views = new KeyView[KeyCount];
        for (int i = 0; i < KeyCount; i++) views[i] = new KeyView("blank", _ => { });
        long now = Environment.TickCount64;
        var projects = _projects.Snapshot();
        var selectedId = _projects.SelectedId;

        if (_pickerOpen && projects.Length > ProjectKeys.Length) BuildPicker(views, projects, selectedId);
        else
        {
            _pickerOpen = false;
            BuildProjectColumn(views, projects, selectedId);
            var project = projects.FirstOrDefault(p => p.Id == selectedId);
            if (_workflowsOpen && project != null) BuildWorkflowList(views, project);
            else
            {
                _workflowsOpen = false;
                BuildSessions(views, project, now);
                BuildRepaste(views, now);
                if (project != null)
                    views[WorkflowsKey] = new KeyView("workflows", KeyArt.WorkflowsButton, OpenWorkflows);
            }
        }

        // Enter with a short press animation.
        float? enterAnim = _enterPressedAt < 0 ? null : (now - _enterPressedAt) / 450f;
        if (enterAnim >= 1) { _enterPressedAt = -1; enterAnim = null; }
        views[EnterKey] = new KeyView(enterAnim is { } e ? $"e|{(int)(e * 30)}" : "e", c => KeyArt.Enter(c, enterAnim), _actions.PressEnter);

        // Mic: static when idle, animated otherwise.
        var state = _dictation.State;
        float t = (now - _dictation.StateSinceTicks) / 1000f;
        float level = _dictation.Level;
        views[MicKey] = new KeyView(state == DictationState.Idle ? "m|idle" : $"m|{state}|{now / 33}",
            c => KeyArt.Mic(c, state, t, level), _actions.ToggleDictation);
        return views;
    }

    KeyView ProjectView(Project p, bool selected, Action press)
    {
        var sessions = _sessions.ForProject(p.Id);
        int count = sessions.Length;
        bool hasDone = !selected && sessions.Any(s => s.Done); // star other projects with finished work
        return new KeyView($"p|{p.Id}|{p.Name}|{p.Color}|{selected}|{count}|{hasDone}",
            c => KeyArt.Project(c, p, selected, count, hasDone), press);
    }

    void BuildProjectColumn(KeyView[] views, Project[] projects, string? selectedId)
    {
        if (projects.Length == 0)
        {
            views[ProjectKeys[0]] = new KeyView("add", KeyArt.AddProject, _actions.AddProject);
            return;
        }

        Project[] column;
        if (projects.Length <= ProjectKeys.Length) column = projects;
        else
        {
            // Two projects plus "more": the first project, then the selected one (or the second).
            int selectedIndex = Array.FindIndex(projects, p => p.Id == selectedId);
            column = [projects[0], selectedIndex >= 1 ? projects[selectedIndex] : projects[1]];
            var hidden = projects.Except(column).Take(4).Select(p => p.Color).ToArray();
            views[MoreKey] = new KeyView($"more|{string.Join(',', hidden)}", c => KeyArt.MoreProjects(c, hidden), OpenPicker);
        }
        for (int k = 0; k < column.Length; k++)
        {
            var p = column[k];
            views[ProjectKeys[k]] = ProjectView(p, p.Id == selectedId, () => _actions.SelectProject(p.Id));
        }
    }

    void BuildSessions(KeyView[] views, Project? project, long now)
    {
        if (project == null) return;
        var color = KeyArt.ParseColor(project.Color);
        int? activeId = _actions.ActiveSessionId(project.Id);
        int slot = 0;
        foreach (var s in _sessions.ForProject(project.Id).OrderBy(s => s.Id))
        {
            if (slot >= SlotKeys.Length) break;
            int key = SlotKeys[slot++];
            bool active = s.Id == activeId;
            float pulse = s.Busy ? 0.5f + 0.5f * MathF.Sin(now / 160f) : -1;
            int pulseStep = pulse < 0 ? -1 : (int)(pulse * 8);
            float hold = HoldProgress(key, now);
            bool done = s.Done;
            var session = s;
            views[key] = new KeyView(
                $"s|{s.Id}|{s.Agent}|{s.Label}|{project.Color}|{active}|{pulseStep}|{(int)(hold * 40)}|{done}",
                c => { KeyArt.Session(c, session, color, active, pulse, done); KeyArt.HoldRing(c, hold); },
                Press: () => _actions.ActivateSession(session.Id),
                Hold: () => _actions.CloseSession(session.Id));
        }
        foreach (var agent in LaunchAgents)
        {
            if (slot >= SlotKeys.Length) break;
            views[SlotKeys[slot++]] = new KeyView($"l|{agent}", c => KeyArt.Launcher(c, agent), () => _actions.Launch(agent));
        }
    }

    /// <summary>For a few seconds after a transcript, key 13 (left of Enter) pastes it again.</summary>
    void BuildRepaste(KeyView[] views, long now)
    {
        long since = Math.Max(_transcripts.LastAddedTicks, _repastePressedAt);
        if (since == 0 || _transcripts.Latest == null) return;
        float remaining = 1 - (now - since) / (float)RepasteMs;
        if (remaining <= 0) return;
        views[RepasteKey] = new KeyView($"repaste|{(int)(remaining * 60)}", c => KeyArt.Repaste(c, remaining), () =>
        {
            _repastePressedAt = Environment.TickCount64; // each press restarts the countdown
            _actions.PasteLastTranscript();
        });
    }

    void BuildPicker(KeyView[] views, Project[] projects, string? selectedId)
    {
        views[MoreKey] = new KeyView("back", KeyArt.Back, () => _pickerOpen = false);

        // Twelve keys fit every project up to twelve; beyond that the last key pages.
        bool paged = projects.Length > PickerKeys.Length;
        int perPage = paged ? PickerKeys.Length - 1 : PickerKeys.Length;
        int pages = (projects.Length + perPage - 1) / perPage;
        _pickerPage %= pages;
        var page = projects.Skip(_pickerPage * perPage).Take(perPage).ToArray();
        for (int i = 0; i < page.Length; i++)
        {
            var p = page[i];
            views[PickerKeys[i]] = ProjectView(p, p.Id == selectedId, () =>
            {
                _pickerOpen = false;
                _actions.SelectProject(p.Id);
            });
        }
        if (paged)
            views[PickerKeys[^1]] = new KeyView($"next|{_pickerPage}", KeyArt.NextPage, () => _pickerPage = (_pickerPage + 1) % pages);
    }

    /// <summary>The project's workflows across the terminal keys; the Workflows key becomes Back. Paged if many.</summary>
    void BuildWorkflowList(KeyView[] views, Project project)
    {
        views[WorkflowsKey] = new KeyView("back", KeyArt.Back, () => _workflowsOpen = false);

        var workflows = _workflows.ForProject(project.Id);
        // Every workflow plus a "+ New" key; if that overflows, the last key pages instead.
        int total = workflows.Length + 1;
        bool paged = total > SlotKeys.Length;
        int perPage = paged ? SlotKeys.Length - 1 : SlotKeys.Length;
        int pages = (total + perPage - 1) / perPage;
        _workflowPage %= pages;
        int first = _workflowPage * perPage;
        for (int i = 0; i < perPage && first + i < total; i++)
        {
            int key = SlotKeys[i];
            if (first + i == workflows.Length)
            {
                views[key] = new KeyView("wf-new", KeyArt.NewWorkflow, () => { _workflowsOpen = false; _actions.NewWorkflow(); });
                continue;
            }
            var w = workflows[first + i];
            views[key] = new KeyView($"wf|{w.Id}|{w.Name}|{w.Agent}|{w.Color}", c => KeyArt.Workflow(c, w), () =>
            {
                _workflowsOpen = false;
                _actions.RunWorkflow(w.Id);
            });
        }
        if (paged)
            views[SlotKeys[^1]] = new KeyView($"wf-next|{_workflowPage}", KeyArt.NextPage, () => _workflowPage = (_workflowPage + 1) % pages);
    }

    void OpenWorkflows()
    {
        _workflowPage = 0;
        _pickerTouched = Environment.TickCount64;
        _workflowsOpen = true;
    }

    void OpenPicker()
    {
        _pickerPage = 0;
        _pickerTouched = Environment.TickCount64;
        _pickerOpen = true;
    }

    void DisposeBoard()
    {
        try { _board?.Dispose(); } catch (Exception) { }
        _board = null;
    }

    public void Dispose()
    {
        _stop = true;
        _thread.Join(1000);
        try { _board?.ClearKeys(); } catch (Exception) { }
        DisposeBoard();
    }
}

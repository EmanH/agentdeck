using AgentDeck.Core;
using AgentDeck.Dictation;
using AgentDeck.Native;
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
    void OpenUrl(string url);
    int? ActiveSessionId(string projectId);
}

/// <summary>
/// Keeps the Stream Deck in sync with the app at ~30 fps, redrawing only keys whose content changed.
/// Normal layout (5x3): left column = projects (bottom-left becomes "more" past three), keys 14/15 =
/// Enter/Mic, the rest = the selected project's terminals then "+ agent" launchers. "More" opens a
/// temporary project picker across the deck with a back key. Key 12 (or Paste again, briefly after a transcript)
/// opens the editor tools: every key becomes a drawing-app shortcut until its Back key is pressed.
/// </summary>
sealed class DeckController : IDisposable
{
    static readonly int[] ProjectKeys = [0, 5, 10];
    static readonly int[] SlotKeys = [1, 2, 3, 4, 6, 7, 8, 9, 11];
    const int RevenueKey = 9; // right column, second from bottom; replaces that session slot when the revenue feed is on
    static readonly int[] PickerKeys = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 11, 12];
    const int MoreKey = 10, RepasteKey = 11, WorkflowsKey = 12, EnterKey = 13, MicKey = 14, KeyCount = 15;
    const int RepasteMs = 10_000; // "paste again" shows this long after a transcript (or its last press)
    const int HoldMs = 800, HoldVisibleAfterMs = 150, PickerTimeoutMs = 10_000;
    static readonly AgentKind[] LaunchAgents = [AgentKind.Claude, AgentKind.Grok, AgentKind.OpenCode, AgentKind.Codex];
    const int EditorFlashMs = 280;

    /// <summary>Editor mode: each key presses its shortcut in whatever app has focus, auto-repeating while held. A 3x3 block on the left for a left thumb; Back completes it bottom-right.</summary>
    static readonly (int Key, KeyArt.EditorTool Tool)[] EditorKeys =
    [
        (0, KeyArt.EditorTool.Image), (1, KeyArt.EditorTool.Connect), (2, KeyArt.EditorTool.Metadata),
        (5, KeyArt.EditorTool.Ungroup), (6, KeyArt.EditorTool.Select), (7, KeyArt.EditorTool.Undo),
        (10, KeyArt.EditorTool.Erase), (11, KeyArt.EditorTool.Box),
    ];

    readonly ProjectStore _projects;
    readonly SessionManager _sessions;
    readonly DictationService _dictation;
    readonly TranscriptStore _transcripts;
    readonly WorkflowStore _workflows;
    readonly IDeckActions _actions;
    readonly RevenueFeed _revenue;
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
    // Keys down whose view repeats or wants the release, and when each repeats next.
    readonly KeyView?[] _pressedView = new KeyView?[KeyCount];
    readonly long[] _nextRepeat = new long[KeyCount];
    readonly (bool down, long at)?[] _lastEvent = new (bool, long)?[KeyCount];
    Action? _unsubscribe; // detaches our key handler from the current board

    // Project picker ("more") and workflow list modes; both close after a few idle seconds.
    volatile bool _pickerOpen, _workflowsOpen;
    int _pickerPage, _workflowPage;
    long _pickerTouched;

    // Revenue: the green ripple after pressing a payment card, then the payment-detail screen.
    const int RippleMs = 1300, DetailOpensAfterMs = 420, DetailTimeoutMs = 45_000, CelebrateMs = 2400;
    long _rippleAt = -1;
    volatile RevenueEvent? _detail;
    long _detailAt;

    // Editor mode stays until Back; no idle timeout.
    volatile bool _editorOpen;
    readonly long[] _editorPressedAt = new long[KeyCount];
    volatile int _editorLastKey = -1; // gets the gold highlight

    /// <param name="Press">Fires immediately on key-down.</param>
    /// <param name="Hold">Extra long-press action, fired if the key is still down after <see cref="HoldMs"/>.</param>
    /// <param name="Repeat">Fired over and over while the key is held, like keyboard auto-repeat.</param>
    /// <param name="Release">Fired on key-up.</param>
    readonly record struct KeyView(string Signature, Action<SKCanvas> Draw, Action? Press = null, Action? Hold = null,
                                   Action? Repeat = null, Action? Release = null);

    public DeckController(ProjectStore projects, SessionManager sessions, DictationService dictation,
                          TranscriptStore transcripts, WorkflowStore workflows, RevenueFeed revenue, IDeckActions actions)
    {
        _projects = projects;
        _sessions = sessions;
        _dictation = dictation;
        _transcripts = transcripts;
        _workflows = workflows;
        _actions = actions;
        _revenue = revenue;
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
                    if (_detail != null && frameStart - Math.Max(_pickerTouched, _detailAt) > DetailTimeoutMs) _detail = null;

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
            // The library can revive a board we've already dropped after a USB blip; only the current
            // board's events count, otherwise every press arrives twice.
            EventHandler<KeyEventArgs> handler = (_, e) => { if (ReferenceEquals(board, _board)) OnKey(e.Key, e.IsDown); };
            board.KeyStateChanged += handler;
            _unsubscribe = () => board.KeyStateChanged -= handler;
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
        Action? fire = null;

        lock (_keyGate)
        {
            // Safety net: a second identical event for the same key within 60 ms is a duplicate, not a person.
            if (_lastEvent[key] is var (wasDown, at) && wasDown == down && now - at < 60) return;
            _lastEvent[key] = (down, now);
            _pickerTouched = now;

            if (down)
            {
                // Press always acts immediately, so taps are never lost; a hold is an extra gesture on top.
                var view = views[key];
                if (key == EnterKey) _enterPressedAt = now;
                fire = view.Press;
                if (view.Hold != null) { _downAt[key] = now; _heldView[key] = view; }
                if (view.Repeat != null || view.Release != null)
                {
                    _pressedView[key] = view;
                    _nextRepeat[key] = now + RepeatDelayMs();
                }
            }
            else
            {
                _downAt[key] = -1; // released before the hold fired
                _heldView[key] = null;
                fire = _pressedView[key]?.Release; // the view that was pressed, even if the layout changed since
                _pressedView[key] = null;
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
            for (int k = 0; k < KeyCount; k++)
            {
                if (_pressedView[k]?.Repeat is not { } repeat || now < _nextRepeat[k]) continue;
                fire.Add(repeat);
                _nextRepeat[k] = now + RepeatIntervalMs();
            }
        }
        foreach (var action in fire) action();
    }

    // Auto-repeat follows the Windows keyboard settings (delay 0..3 = 250..1000 ms; speed 0..31 = ~2.5..30 per second).
    static int RepeatDelayMs() => (System.Windows.Forms.SystemInformation.KeyboardDelay + 1) * 250;
    static int RepeatIntervalMs() => (int)(1000 / (2.5f + System.Windows.Forms.SystemInformation.KeyboardSpeed * 27.5f / 31));

    /// <summary>Release every key still down (the deck went away mid-press), so no synthetic key stays stuck.</summary>
    void ReleaseAll()
    {
        var fire = new List<Action>();
        lock (_keyGate)
        {
            for (int k = 0; k < KeyCount; k++)
            {
                if (_pressedView[k]?.Release is { } release) fire.Add(release);
                _pressedView[k] = null;
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

        if (_detail is { } detail && now >= _detailAt)
        {
            BuildPaymentDetail(views, detail, now);
            ApplyRevenueEffects(views, now);
            return views;
        }
        if (_editorOpen)
        {
            BuildEditor(views, now);
            return views;
        }
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
                BuildRevenue(views, now);
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
        ApplyRevenueEffects(views, now);
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
            // Two projects plus "more". Projects are most recent first, so normally that's the current project and
            // the previous one; the selected project is always shown, whatever the order.
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
        var slots = _revenue.Enabled ? SlotKeys.Where(k => k != RevenueKey).ToArray() : SlotKeys;
        int slot = 0;
        foreach (var s in _sessions.ForProject(project.Id)) // tab order
        {
            if (slot >= slots.Length) break;
            int key = slots[slot++];
            bool active = s.Id == activeId;
            float pulse = s.Busy ? 0.5f + 0.5f * MathF.Sin(now / 160f) : -1;
            int pulseStep = pulse < 0 ? -1 : (int)(pulse * 8);
            float hold = HoldProgress(key, now);
            bool done = s.Done;
            var session = s;
            views[key] = new KeyView(
                $"s|{s.Id}|{s.Agent}|{s.Label}|{s.Icon}|{project.Color}|{active}|{pulseStep}|{(int)(hold * 40)}|{done}",
                c => { KeyArt.Session(c, session, color, active, pulse, done); KeyArt.HoldRing(c, hold); },
                Press: () => _actions.ActivateSession(session.Id),
                Hold: () => _actions.CloseSession(session.Id));
        }
        foreach (var agent in LaunchAgents)
        {
            if (slot >= slots.Length) break;
            views[slots[slot++]] = new KeyView($"l|{agent}", c => KeyArt.Launcher(c, agent), () => _actions.Launch(agent));
        }
    }

    /// <summary>Airwallex: MRR and subscribers at rest; a pulsing card per new payment/subscriber, opened (and dismissed) by a press.</summary>
    void BuildRevenue(KeyView[] views, long now)
    {
        if (!_revenue.Enabled) return;
        var unseen = _revenue.Unseen();
        if (unseen.Length == 0)
        {
            var st = _revenue.Stats;
            views[RevenueKey] = new KeyView($"rev|{st?.Mrr}|{st?.Currency}|{st?.Subscribers}",
                c => KeyArt.RevenueStats(c, st?.Mrr, st?.Currency, st?.Subscribers),
                () => _actions.OpenUrl("https://www.airwallex.com/app/billing/subscriptions"));
            return;
        }
        var e = unseen[0];
        float pulse = 0.5f + 0.5f * MathF.Sin(now / 220f);
        views[RevenueKey] = new KeyView($"revev|{e.Key}|{unseen.Length}|{(int)(pulse * 8)}",
            c => KeyArt.RevenueEvent(c, e.Kind, e.Label, e.Amount, e.Currency, unseen.Length, pulse),
            () => OpenPayment(_revenue.Pop()));
    }

    /// <summary>Green ripple out from the revenue key, then the detail screen fades in behind it.</summary>
    void OpenPayment(RevenueEvent? e)
    {
        if (e == null) return;
        long now = Environment.TickCount64;
        _rippleAt = now;
        _detailAt = now + DetailOpensAfterMs;
        _pickerTouched = now;
        _detail = e;
    }

    static (float x, float y) KeyCentre(int key) => (key % 5 * 84 + 36, key / 5 * 84 + 36); // 72 px keys, ~12 px gaps

    /// <summary>Distance from the revenue key, 0..1 across the deck.</summary>
    static float DistanceFromRevenueKey(int key)
    {
        var (x, y) = KeyCentre(key);
        var (ox, oy) = KeyCentre(RevenueKey);
        return MathF.Sqrt((x - ox) * (x - ox) + (y - oy) * (y - oy)) / 346f;
    }

    /// <summary>Layers the press ripple (green) and the arrival celebration (gold) over whatever the keys show.</summary>
    void ApplyRevenueEffects(KeyView[] views, long now)
    {
        if (!_revenue.Enabled) return;
        float ripple = _rippleAt < 0 ? -1 : (now - _rippleAt) / (float)RippleMs;
        if (ripple >= 1) { _rippleAt = -1; ripple = -1; }
        long since = now - _revenue.LastCelebrationTicks;
        float party = _revenue.LastCelebrationTicks < 0 || since > CelebrateMs ? -1 : since / (float)CelebrateMs;
        if (ripple < 0 && party < 0) return;

        var green = new SKColor(52, 211, 153);
        var gold = new SKColor(255, 201, 64);
        for (int k = 0; k < views.Length; k++)
        {
            float d = DistanceFromRevenueKey(k);
            float wash = 0, ring = 0, gWash = 0, gRing = 0;
            if (ripple >= 0)
            {
                // A ring that sweeps the deck in ~60% of the time, leaving a wash behind that fades out.
                float front = ripple * 1.7f;
                float fade = ripple < 0.55f ? 1 : 1 - (ripple - 0.55f) / 0.45f;
                ring = MathF.Exp(-MathF.Pow((d - front) / 0.16f, 2)) * fade;
                wash = (d <= front ? 0.55f : 0) * fade + ring * 0.6f;
            }
            if (party >= 0)
            {
                // Two soft gold pulses rolling out from the revenue key.
                for (int pulse = 0; pulse < 2; pulse++)
                {
                    float p = party * 1.5f - pulse * 0.35f;
                    if (p is < 0 or > 1) continue;
                    float r = MathF.Exp(-MathF.Pow((d - p * 1.3f) / 0.15f, 2)) * (1 - p);
                    gRing = Math.Max(gRing, r);
                    gWash = Math.Max(gWash, r * 0.35f);
                }
            }
            float burst = party >= 0 && k == RevenueKey ? Math.Min(1, party * 1.6f) : -1;
            if (wash + ring + gWash + gRing < 0.02f && burst < 0) continue;

            var base_ = views[k];
            int step = (int)(Math.Max(ripple, party) * 60);
            views[k] = base_ with
            {
                Signature = $"{base_.Signature}|fx{step}",
                Draw = c =>
                {
                    base_.Draw(c);
                    if (gWash + gRing > 0.01f) KeyArt.Glow(c, gold, gWash, gRing);
                    if (burst > 0) KeyArt.Burst(c, burst, green);
                    if (wash + ring > 0.01f) KeyArt.Glow(c, green, wash, ring);
                },
            };
        }
    }

    /// <summary>
    /// The payment on the deck: amount, where it's from, how it was paid, who, when, and the business totals.
    /// Tiles fade in rippling out from the revenue key. Back (top-left) or 45 idle seconds closes it.
    /// </summary>
    void BuildPaymentDetail(KeyView[] views, RevenueEvent e, long now)
    {
        var st = _revenue.Stats;
        var green = new SKColor(52, 211, 153);
        var blue = new SKColor(76, 194, 255);
        var purple = new SKColor(168, 85, 247);
        var gold = new SKColor(255, 201, 64);
        string? cur = e.Currency ?? st?.Currency;

        void Tile(int key, string caption, string? value, string? sub, SKColor accent, bool big = false)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            views[key] = new KeyView($"pd|{key}|{caption}|{value}|{sub}", c => KeyArt.DetailTile(c, caption, value, sub, accent, big));
        }

        string title = e.Kind switch { "subscriber" => "New subscriber", "intent" => "Checkout", _ => "Payment" };
        views[0] = new KeyView("pd-back", KeyArt.Back, () => _detail = null);
        Tile(1, title, e.Amount is { } a ? KeyArt.Money(a, cur) : e.Kind == "subscriber" ? "Welcome!" : "—", cur, green, big: true);
        Tile(2, "Country", CountryName(e.Country) ?? e.Country, e.Country?.ToUpperInvariant(), blue);
        Tile(3, "Paid with", e.Method, Join(e.Funding, e.CardCountry != null && e.CardCountry != e.Country ? $"card from {e.CardCountry}" : null), blue);
        views[4] = new KeyView("pd-open", KeyArt.OpenExternal, () => { _detail = null; _actions.OpenUrl(e.Url); });
        Tile(5, "Customer", e.Customer ?? e.Email ?? e.Label, e.Customer != null ? e.Email : null, purple);
        Tile(6, "When", Ago(e.At), DateTimeOffset.FromUnixTimeMilliseconds(e.At).ToLocalTime().ToString("ddd h:mm tt"), purple);
        Tile(7, "For", e.Description ?? e.OrderId, e.Description != null ? e.OrderId : null, purple);
        Tile(8, "Risk", e.Risk is { } r ? r.ToString("0.#") : null, "Airwallex score", purple);
        if (st != null)
        {
            Tile(9, "Last 24 h", st.Today is { } t ? KeyArt.Money(t, st.Currency) : null, st.TodayCount is { } n ? $"{n} payment{(n == 1 ? "" : "s")}" : null, gold);
            Tile(10, "Subscribers", st.Subscribers.ToString(), "active", gold);
            Tile(11, "MRR", KeyArt.Money(st.Mrr, st.Currency), st.Currency, gold, big: true);
            Tile(12, "Last 30 days", st.Month is { } m ? KeyArt.Money(m, st.Currency) : null, st.MonthCount is { } mc ? $"{mc} payments" : null, gold);
        }
        var more = _revenue.Unseen().Length;
        if (more > 0)
            views[14] = new KeyView($"pd-next|{more}", c => KeyArt.DetailTile(c, "Next", $"{more} more", null, green),
                () => OpenPayment(_revenue.Pop()));

        // Fade the tiles in, rippling out from the revenue key.
        float elapsed = now - _detailAt;
        for (int k = 0; k < views.Length; k++)
        {
            float appear = Math.Clamp((elapsed - DistanceFromRevenueKey(k) * 260) / 220f, 0, 1);
            if (appear >= 1) continue;
            var v = views[k];
            views[k] = v with
            {
                Signature = $"{v.Signature}|in{(int)(appear * 12)}",
                Draw = c =>
                {
                    using var layer = new SKPaint { Color = SKColors.White.WithAlpha((byte)(appear * 255)) };
                    c.SaveLayer(layer);
                    c.Translate(36, 36);
                    c.Scale(0.85f + 0.15f * appear);
                    c.Translate(-36, -36);
                    v.Draw(c);
                    c.Restore();
                },
            };
        }
    }

    static string? Join(params string?[] parts) =>
        parts.Where(p => !string.IsNullOrWhiteSpace(p)).ToArray() is { Length: > 0 } list ? string.Join(" · ", list) : null;

    static string? CountryName(string? code)
    {
        if (code is not { Length: 2 }) return null;
        try { return new System.Globalization.RegionInfo(code).EnglishName; } catch { return null; }
    }

    static string Ago(long unixMs)
    {
        var span = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeMilliseconds(unixMs);
        return span.TotalMinutes < 1 ? "Just now"
            : span.TotalHours < 1 ? $"{(int)span.TotalMinutes} min ago"
            : span.TotalDays < 1 ? $"{(int)span.TotalHours} h ago"
            : $"{(int)span.TotalDays} d ago";
    }

    /// <summary>For a few seconds after a transcript, key 13 (left of Enter) pastes it again.</summary>
    void BuildRepaste(KeyView[] views, long now)
    {
        long since = Math.Max(_transcripts.LastAddedTicks, _repastePressedAt);
        float remaining = since == 0 || _transcripts.Latest == null ? 0 : 1 - (now - since) / (float)RepasteMs;
        if (remaining <= 0)
        {
            views[RepasteKey] = new KeyView("editor", KeyArt.EditorButton, () => _editorOpen = true);
            return;
        }
        views[RepasteKey] = new KeyView($"repaste|{(int)(remaining * 60)}", c => KeyArt.Repaste(c, remaining), () =>
        {
            _repastePressedAt = Environment.TickCount64; // each press restarts the countdown
            _actions.PasteLastTranscript();
        });
    }

    void BuildEditor(KeyView[] views, long now)
    {
        foreach (var (key, tool) in EditorKeys)
        {
            // Lit while held, fading out after release.
            float flash = _pressedView[key] != null ? 1
                : _editorPressedAt[key] == 0 ? 0 : Math.Max(0, 1 - (now - _editorPressedAt[key]) / (float)EditorFlashMs);
            int k = key;
            // Like a real keyboard: key-down on press, repeated key-downs while held, key-up on release.
            byte vk = (byte)KeyArt.EditorShortcut(tool);
            byte[] modifiers = tool == KeyArt.EditorTool.Undo ? [Input.VK_CONTROL] : [];
            bool last = key == _editorLastKey;
            views[key] = new KeyView($"ed|{tool}|{(int)(flash * 20)}|{last}", c => KeyArt.Editor(c, tool, flash, last),
                Press: () =>
                {
                    _editorLastKey = k;
                    Input.Down(vk, modifiers);
                },
                Repeat: () => Input.Down(vk),
                Release: () =>
                {
                    _editorPressedAt[k] = Environment.TickCount64;
                    Input.Up(vk, modifiers);
                });
        }
        views[WorkflowsKey] = new KeyView("ed-back", KeyArt.Back, () => _editorOpen = false);
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
        ReleaseAll();
        try { _unsubscribe?.Invoke(); } catch (Exception) { }
        _unsubscribe = null;
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

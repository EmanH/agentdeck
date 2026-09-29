namespace AgentDeck.Core;

/// <summary>
/// Names each terminal after what's on its screen and picks an emoji icon for it (Stream Deck keys). The UI sends
/// a terminal's recent screen text every 30 s while it's changing; each new screen costs one gpt-6-luna call for
/// the 2-4 word title and one TypeSafe Jev classification for the icon. Idle terminals cost nothing.
/// </summary>
sealed class SessionAnalyzer(SessionManager sessions, OpenAiClient openai, TypeSafeClient typesafe)
{
    const int MinScreenChars = 40;
    const int MaxScreenChars = 4000;       // the tail of the screen is what's current
    const long MinGapMs = 25_000;          // at most one analysis per terminal per ~30 s push
    const double SwitchMargin = 0.15;      // a new icon must beat the current one by this much (no flicker)

    const string TitleInstructions = """
        You name a terminal where a coding agent (or a shell) is working, for a tiny Stream Deck key. From its recent
        screen text, reply with a 2 to 4 word title in Title Case saying what the work is about, e.g. "Login Cookie
        Bug", "Stripe Webhooks", "Deck Key Layout". Name the task, not the tool: never the agent's name, "Terminal",
        "Claude Code" or bits of the UI. If the current title still fits, reply with it unchanged; only change it
        when the work has clearly moved on. No punctuation or quotes. Reply with the title only.
        """;

    const string IconInstructions =
        "Pick the emoji icon that best represents what this terminal session is working on, judged from its title " +
        "and recent screen output. Prefer the specific subject of the work (e.g. payments, audio, tests, security) " +
        "over generic coding; use the idle icons only when nothing is happening.";

    public bool Enabled => openai.Enabled || typesafe.Enabled;

    /// <summary>The UI's latest screen text for a terminal. Analyses it unless it's unchanged, too soon, or tiny.</summary>
    public void OnScreen(int sessionId, string text)
    {
        if (!Enabled || sessions.Get(sessionId) is not { } s) return;
        text = text.Trim();
        if (text.Length < MinScreenChars) return;
        if (text.Length > MaxScreenChars) text = text[^MaxScreenChars..];

        int hash = text.GetHashCode();
        long now = Environment.TickCount64;
        lock (s)
        {
            if (s.Analyzing || hash == s.ScreenHash || now - s.AnalyzedTicks < MinGapMs) return;
            s.Analyzing = true;
            s.ScreenHash = hash;
            s.AnalyzedTicks = now;
        }
        _ = Task.Run(() => AnalyzeAsync(s, text));
    }

    async Task AnalyzeAsync(Session s, string screen)
    {
        try
        {
            long started = Environment.TickCount64;
            var agent = s.Agent == AgentKind.Shell ? "PowerShell" : s.Agent.ToString();
            // Title first, so the classifier sees the fresh title; each step fails on its own without the other.
            string? title = null;
            if (openai.Enabled)
            {
                try
                {
                    var input = $"Agent: {agent}\nCurrent title: {s.Topic ?? s.Label}\nRecent screen:\n{screen}";
                    title = Clean(await Retry(() => openai.RespondAsync(TitleInstructions, input, TimeSpan.FromSeconds(15), priority: false)));
                }
                catch (Exception ex) { Log.Info($"Session {s.Id} title skipped: {ex.Message}"); }
            }

            string? icon = null;
            double p = 0;
            if (typesafe.Enabled)
            {
                try
                {
                    var state = new { agent, title = title ?? s.Label, recentScreen = screen };
                    var result = await Retry(() => typesafe.ChooseAsync(state, IconInstructions, SessionIcons.Options));
                    icon = result.Choice;
                    p = result.ProbabilityOf(icon);
                    // Keep the current icon unless the new one is clearly better: no flicker between near-ties.
                    if (s.Icon != null && icon != s.Icon && p - result.ProbabilityOf(s.Icon) < SwitchMargin) icon = s.Icon;
                }
                catch (Exception ex) { Log.Info($"Session {s.Id} icon skipped: {ex.Message}"); }
            }

            bool changed = false;
            if (title != null && title != s.Topic) { s.Topic = title; changed = true; }
            if (icon != null && icon != s.Icon && IconLibrary.Exists(icon)) { s.Icon = icon; changed = true; }
            Log.Info($"Session {s.Id} analysed in {Environment.TickCount64 - started}ms: \"{s.Topic}\", {s.Icon} ({p:P0})");
            if (changed) sessions.NotifyChanged();
        }
        finally
        {
            lock (s) s.Analyzing = false;
        }
    }

    /// <summary>One retry after a short pause: a blip shouldn't cost a whole 30 s cycle.</summary>
    static async Task<T> Retry<T>(Func<Task<T>> call)
    {
        try { return await call(); }
        catch (Exception) { await Task.Delay(1500); return await call(); }
    }

    /// <summary>At most four words, no quotes or trailing punctuation; null if nothing usable came back.</summary>
    static string? Clean(string reply)
    {
        var words = reply.Split([' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Trim('"', '\'', '.', ',', ':', ';', '!', '*', '`'))
            .Where(w => w.Length > 0).Take(4).ToArray();
        var title = string.Join(' ', words);
        return title.Length is > 0 and <= 40 ? title : null;
    }
}

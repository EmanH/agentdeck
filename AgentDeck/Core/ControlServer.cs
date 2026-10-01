using System.Net;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using AgentDeck.Deck;

namespace AgentDeck.Core;

/// <summary>
/// Localhost HTTP control API exposing IDeckActions and session/project state for external orchestration.
/// Listens on 127.0.0.1:17832, JSON camelCase, optional Bearer token auth via AGENTDECK_API_TOKEN.
/// </summary>
sealed class ControlServer : IDisposable
{
    const int Port = 17832;
    static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    readonly HttpListener _listener = new();
    readonly ProjectStore _projects;
    readonly SessionManager _sessions;
    readonly WorkflowStore _workflows;
    readonly IDeckActions _actions;
    readonly Dispatcher _dispatcher;
    readonly Func<Dictionary<string, string?>> _getBranches;
    readonly string? _authToken;
    readonly CancellationTokenSource _cts = new();
    readonly Task _listenTask;

    public ControlServer(ProjectStore projects, SessionManager sessions, WorkflowStore workflows, IDeckActions actions,
                         Dispatcher dispatcher, Func<Dictionary<string, string?>> getBranches)
    {
        _projects = projects;
        _sessions = sessions;
        _workflows = workflows;
        _actions = actions;
        _dispatcher = dispatcher;
        _getBranches = getBranches;
        _authToken = Env.Get("AGENTDECK_API_TOKEN");

        try
        {
            _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
            _listener.Start();
            Log.Info($"Control API listening on http://127.0.0.1:{Port}{(_authToken != null ? " (auth required)" : "")}");
            _listenTask = Task.Run(ListenAsync);
        }
        catch (HttpListenerException ex)
        {
            Log.Error($"Control API failed to bind port {Port} (already in use?)", ex);
            _listener.Close();
            throw;
        }
    }

    async Task ListenAsync()
    {
        try
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                var ctx = await _listener.GetContextAsync().ConfigureAwait(false);
                _ = Task.Run(() => HandleRequestAsync(ctx), _cts.Token);
            }
        }
        catch (HttpListenerException) when (_cts.Token.IsCancellationRequested) { }
        catch (Exception ex) { Log.Error("Control API listener", ex); }
    }

    async Task HandleRequestAsync(HttpListenerContext ctx)
    {
        try
        {
            var req = ctx.Request;
            var res = ctx.Response;
            res.ContentEncoding = Encoding.UTF8;

            // Optional Bearer token auth
            if (_authToken != null)
            {
                var auth = req.Headers["Authorization"];
                if (auth != $"Bearer {_authToken}")
                {
                    await SendJsonAsync(res, 401, new { error = "Unauthorized" });
                    return;
                }
            }

            // CORS for local dev tools
            res.Headers["Access-Control-Allow-Origin"] = "*";
            res.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
            res.Headers["Access-Control-Allow-Headers"] = "Content-Type, Authorization";
            if (req.HttpMethod == "OPTIONS")
            {
                res.StatusCode = 204;
                res.Close();
                return;
            }

            var path = req.Url!.AbsolutePath.ToLowerInvariant().TrimEnd('/');
            var method = req.HttpMethod;

            switch ($"{method} {path}")
            {
                case "GET /health":
                    await SendJsonAsync(res, 200, new { status = "ok", version = "1.1.0" });
                    break;

                case "GET /state":
                    await GetStateAsync(res);
                    break;

                case "GET /workflows":
                    await GetWorkflowsAsync(res);
                    break;

                case "POST /projects/select":
                    await PostProjectSelectAsync(req, res);
                    break;

                case "POST /sessions/new":
                    await PostSessionNewAsync(req, res);
                    break;

                case string s when s.StartsWith("POST /sessions/") && s.EndsWith("/activate"):
                    await PostSessionActivateAsync(path, res);
                    break;

                case string s when s.StartsWith("POST /sessions/") && s.EndsWith("/close"):
                    await PostSessionCloseAsync(path, res);
                    break;

                case string s when s.StartsWith("POST /sessions/") && s.EndsWith("/input"):
                    await PostSessionInputAsync(path, req, res);
                    break;

                case string s when s.StartsWith("POST /sessions/") && s.EndsWith("/paste"):
                    await PostSessionPasteAsync(path, req, res);
                    break;

                case string s when s.StartsWith("POST /sessions/") && s.EndsWith("/enter"):
                    await PostSessionEnterAsync(path, res);
                    break;

                case string s when s.StartsWith("POST /workflows/") && s.EndsWith("/run"):
                    await PostWorkflowRunAsync(path, res);
                    break;

                case "POST /launch":
                    await PostLaunchAsync(req, res);
                    break;

                case "POST /dictation/toggle":
                    await PostDictationToggleAsync(res);
                    break;

                case "POST /window/show":
                    await PostWindowShowAsync(res);
                    break;

                default:
                    await SendJsonAsync(res, 404, new { error = "Not found" });
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Control API request", ex);
            try { await SendJsonAsync(ctx.Response, 500, new { error = ex.Message }); }
            catch { }
        }
    }

    async Task GetStateAsync(HttpListenerResponse res)
    {
        var projects = _projects.Snapshot();
        var sessions = _sessions.Snapshot().OrderBy(s => s.Id)
            .Select(s => new
            {
                s.Id,
                s.ProjectId,
                Agent = s.Agent.ToString().ToLowerInvariant(),
                s.Label,
                s.Done,
                s.Working,
                s.Busy
            });
        var branches = _getBranches();

        var state = new
        {
            projects = projects.Select(p => new { p.Id, p.Name, p.Path, p.Color, p.Icon }),
            selected = _projects.SelectedId,
            sessions,
            branches
        };

        await SendJsonAsync(res, 200, state);
    }

    async Task GetWorkflowsAsync(HttpListenerResponse res)
    {
        var workflows = _workflows.Snapshot().Select(w => new
        {
            w.Id,
            w.ProjectId,
            w.Name,
            Agent = w.Agent.ToString().ToLowerInvariant(),
            w.Instructions,
            w.Color,
            w.Icon,
            w.Model,
            w.Effort
        });

        await SendJsonAsync(res, 200, new { workflows });
    }

    async Task PostProjectSelectAsync(HttpListenerRequest req, HttpListenerResponse res)
    {
        var body = await ReadJsonAsync<Dictionary<string, string>>(req);
        if (body == null || !body.TryGetValue("id", out var id) || string.IsNullOrEmpty(id))
        {
            await SendJsonAsync(res, 400, new { error = "Missing 'id'" });
            return;
        }

        bool selected = false;
        await _dispatcher.InvokeAsync(() => selected = _projects.Select(id));

        if (!selected)
        {
            await SendJsonAsync(res, 404, new { error = "Project not found" });
            return;
        }

        await SendJsonAsync(res, 200, new { success = true });
    }

    async Task PostSessionNewAsync(HttpListenerRequest req, HttpListenerResponse res)
    {
        var body = await ReadJsonAsync<Dictionary<string, JsonElement>>(req);
        if (body == null || !body.TryGetValue("agent", out var agentEl))
        {
            await SendJsonAsync(res, 400, new { error = "Missing 'agent'" });
            return;
        }

        if (!Enum.TryParse<AgentKind>(agentEl.GetString(), ignoreCase: true, out var agent))
        {
            await SendJsonAsync(res, 400, new { error = "Invalid agent (shell, claude, codex, grok)" });
            return;
        }

        string? projectId = body.TryGetValue("projectId", out var pid) && pid.ValueKind == JsonValueKind.String ? pid.GetString() : null;
        string? prompt = body.TryGetValue("prompt", out var pr) && pr.ValueKind == JsonValueKind.String ? pr.GetString() : null;
        string? model = body.TryGetValue("model", out var mo) && mo.ValueKind == JsonValueKind.String ? mo.GetString() : null;
        string? effort = body.TryGetValue("effort", out var ef) && ef.ValueKind == JsonValueKind.String ? ef.GetString() : null;
        string? name = body.TryGetValue("name", out var na) && na.ValueKind == JsonValueKind.String ? na.GetString() : null;

        Session? session = null;
        await _dispatcher.InvokeAsync(() =>
        {
            var project = projectId != null ? _projects.Get(projectId) : _projects.Selected;
            if (project == null) return;
            try
            {
                session = _sessions.Create(project, agent, name, prompt, model, effort);
            }
            catch (Exception ex)
            {
                Log.Error($"Control API: create session {agent}", ex);
            }
        });

        if (session == null)
        {
            await SendJsonAsync(res, 404, new { error = "Project not found or session failed to start" });
            return;
        }

        await SendJsonAsync(res, 200, new { id = session.Id, projectId = session.ProjectId });
    }

    async Task PostSessionActivateAsync(string path, HttpListenerResponse res)
    {
        if (!TryParseSessionId(path, out var id))
        {
            await SendJsonAsync(res, 400, new { error = "Invalid session id" });
            return;
        }

        await _dispatcher.InvokeAsync(() => _actions.ActivateSession(id));
        await SendJsonAsync(res, 200, new { success = true });
    }

    async Task PostSessionCloseAsync(string path, HttpListenerResponse res)
    {
        if (!TryParseSessionId(path, out var id))
        {
            await SendJsonAsync(res, 400, new { error = "Invalid session id" });
            return;
        }

        await _dispatcher.InvokeAsync(() => _actions.CloseSession(id));
        await SendJsonAsync(res, 200, new { success = true });
    }

    async Task PostSessionInputAsync(string path, HttpListenerRequest req, HttpListenerResponse res)
    {
        if (!TryParseSessionId(path, out var id))
        {
            await SendJsonAsync(res, 400, new { error = "Invalid session id" });
            return;
        }

        var body = await ReadJsonAsync<Dictionary<string, string>>(req);
        if (body == null || !body.TryGetValue("data", out var data))
        {
            await SendJsonAsync(res, 400, new { error = "Missing 'data'" });
            return;
        }

        bool found = false;
        await _dispatcher.InvokeAsync(() =>
        {
            if (_sessions.Get(id) != null)
            {
                _sessions.Write(id, data);
                found = true;
            }
        });

        if (!found)
        {
            await SendJsonAsync(res, 404, new { error = "Session not found" });
            return;
        }

        await SendJsonAsync(res, 200, new { success = true });
    }

    async Task PostSessionPasteAsync(string path, HttpListenerRequest req, HttpListenerResponse res)
    {
        if (!TryParseSessionId(path, out var id))
        {
            await SendJsonAsync(res, 400, new { error = "Invalid session id" });
            return;
        }

        var body = await ReadJsonAsync<Dictionary<string, JsonElement>>(req);
        if (body == null || !body.TryGetValue("text", out var textEl) || textEl.ValueKind != JsonValueKind.String)
        {
            await SendJsonAsync(res, 400, new { error = "Missing 'text'" });
            return;
        }

        var text = textEl.GetString() ?? "";
        bool submit = body.TryGetValue("submit", out var submitEl) && submitEl.ValueKind == JsonValueKind.True;

        bool found = false;
        await _dispatcher.InvokeAsync(() =>
        {
            if (_sessions.Get(id) != null)
            {
                _sessions.Write(id, text);
                if (submit) _sessions.Write(id, "\r");
                found = true;
            }
        });

        if (!found)
        {
            await SendJsonAsync(res, 404, new { error = "Session not found" });
            return;
        }

        await SendJsonAsync(res, 200, new { success = true });
    }

    async Task PostSessionEnterAsync(string path, HttpListenerResponse res)
    {
        if (!TryParseSessionId(path, out var id))
        {
            await SendJsonAsync(res, 400, new { error = "Invalid session id" });
            return;
        }

        bool found = false;
        await _dispatcher.InvokeAsync(() =>
        {
            if (_sessions.Get(id) != null)
            {
                _sessions.Write(id, "\r");
                found = true;
            }
        });

        if (!found)
        {
            await SendJsonAsync(res, 404, new { error = "Session not found" });
            return;
        }

        await SendJsonAsync(res, 200, new { success = true });
    }

    async Task PostWorkflowRunAsync(string path, HttpListenerResponse res)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || parts[0] != "workflows")
        {
            await SendJsonAsync(res, 400, new { error = "Invalid workflow id" });
            return;
        }

        var id = parts[1];
        bool found = false;
        await _dispatcher.InvokeAsync(() =>
        {
            if (_workflows.Get(id) != null)
            {
                _actions.RunWorkflow(id);
                found = true;
            }
        });

        if (!found)
        {
            await SendJsonAsync(res, 404, new { error = "Workflow not found" });
            return;
        }

        await SendJsonAsync(res, 200, new { success = true });
    }

    async Task PostLaunchAsync(HttpListenerRequest req, HttpListenerResponse res)
    {
        var body = await ReadJsonAsync<Dictionary<string, string>>(req);
        if (body == null || !body.TryGetValue("agent", out var agentStr))
        {
            await SendJsonAsync(res, 400, new { error = "Missing 'agent'" });
            return;
        }

        if (!Enum.TryParse<AgentKind>(agentStr, ignoreCase: true, out var agent))
        {
            await SendJsonAsync(res, 400, new { error = "Invalid agent (shell, claude, codex, grok)" });
            return;
        }

        await _dispatcher.InvokeAsync(() => _actions.Launch(agent));
        await SendJsonAsync(res, 200, new { success = true });
    }

    async Task PostDictationToggleAsync(HttpListenerResponse res)
    {
        await _dispatcher.InvokeAsync(() => _actions.ToggleDictation());
        await SendJsonAsync(res, 200, new { success = true });
    }

    async Task PostWindowShowAsync(HttpListenerResponse res)
    {
        await _dispatcher.InvokeAsync(() => ((MainWindow)_actions).ShowFromTray());
        await SendJsonAsync(res, 200, new { success = true });
    }

    static bool TryParseSessionId(string path, out int id)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2 && parts[0] == "sessions" && int.TryParse(parts[1], out id))
            return true;
        id = 0;
        return false;
    }

    static async Task<T?> ReadJsonAsync<T>(HttpListenerRequest req) where T : class
    {
        try
        {
            using var reader = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8);
            var json = await reader.ReadToEndAsync();
            return JsonSerializer.Deserialize<T>(json, JsonOpts);
        }
        catch
        {
            return null;
        }
    }

    static async Task SendJsonAsync(HttpListenerResponse res, int statusCode, object data)
    {
        res.StatusCode = statusCode;
        res.ContentType = "application/json";
        var json = JsonSerializer.Serialize(data, JsonOpts);
        var bytes = Encoding.UTF8.GetBytes(json);
        res.ContentLength64 = bytes.Length;
        await res.OutputStream.WriteAsync(bytes);
        res.Close();
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        _listener.Close();
        try { _listenTask.Wait(1000); } catch { }
        _cts.Dispose();
        Log.Info("Control API stopped");
    }
}

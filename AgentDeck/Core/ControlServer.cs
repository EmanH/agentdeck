using System.Net;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;

namespace AgentDeck.Core;

/// <summary>
/// Localhost HTTP control surface for external assistants. Mirrors what the UI and Stream Deck do
/// (IDeckActions + session/project state). Mutations run on the WPF Dispatcher.
/// </summary>
sealed class ControlServer : IDisposable
{
    public const int DefaultPort = 17832;
    static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    readonly HttpListener _listener = new();
    readonly Dispatcher _ui;
    readonly IControlActions _actions;
    readonly string? _token;
    readonly int _port;
    readonly CancellationTokenSource _cts = new();
    Thread? _thread;

    public ControlServer(Dispatcher ui, IControlActions actions, int? port = null)
    {
        _ui = ui;
        _actions = actions;
        _port = port ?? ParsePort(Env.Get("AGENTDECK_API_PORT")) ?? DefaultPort;
        _token = Env.Get("AGENTDECK_API_TOKEN");
    }

    public int Port => _port;

    public void Start()
    {
        var prefix = $"http://127.0.0.1:{_port}/";
        try
        {
            _listener.Prefixes.Add(prefix);
            _listener.Start();
        }
        catch (Exception ex)
        {
            Log.Error($"control API bind {prefix}", ex);
            Log.Info($"Control API not started (port {_port} unavailable)");
            return;
        }
        _thread = new Thread(Listen) { IsBackground = true, Name = "control-api" };
        _thread.Start();
        Log.Info($"Control API listening on {prefix}" + (_token != null ? " (token required)" : ""));
    }

    static int? ParsePort(string? value) =>
        int.TryParse(value, out int p) && p is > 0 and < 65536 ? p : null;

    void Listen()
    {
        while (!_cts.IsCancellationRequested && _listener.IsListening)
        {
            HttpListenerContext? ctx = null;
            try { ctx = _listener.GetContext(); }
            catch (HttpListenerException) when (_cts.IsCancellationRequested) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex) { Log.Error("control API accept", ex); continue; }
            if (ctx != null) _ = Task.Run(() => Handle(ctx));
        }
    }

    async Task Handle(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var res = ctx.Response;
        try
        {
            res.Headers.Add("Access-Control-Allow-Origin", "null"); // file:// / local tools
            if (req.HttpMethod == "OPTIONS")
            {
                res.AddHeader("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
                res.AddHeader("Access-Control-Allow-Headers", "Content-Type, Authorization");
                res.StatusCode = 204;
                res.Close();
                return;
            }

            var path = req.Url?.AbsolutePath.TrimEnd('/') ?? "";
            if (path.Length == 0) path = "/";

            if (path != "/health" && !Authorized(req))
            {
                await WriteJson(res, 401, new { error = "unauthorized" });
                return;
            }

            switch (req.HttpMethod)
            {
                case "GET" when path == "/health":
                    await WriteJson(res, 200, new { ok = true, port = _port });
                    return;
                case "GET" when path == "/state":
                    await WriteJson(res, 200, await OnUi(() => _actions.GetState()));
                    return;
                case "GET" when path == "/workflows":
                    await WriteJson(res, 200, await OnUi(() => new { workflows = _actions.GetWorkflows() }));
                    return;
                case "POST" when path == "/projects/select":
                {
                    var body = await ReadBody(req);
                    if (body.Id is not { Length: > 0 } id) { await WriteJson(res, 400, new { error = "id required" }); return; }
                    var ok = await OnUi(() => _actions.SelectProject(id));
                    await WriteJson(res, ok ? 200 : 404, ok ? new { ok = true } : new { error = "project not found" });
                    return;
                }
                case "POST" when path == "/sessions/new":
                {
                    var body = await ReadBody(req);
                    if (!TryParseAgent(body.Agent, out var agent))
                    { await WriteJson(res, 400, new { error = "agent required (claude|codex|grok|shell)" }); return; }
                    var session = await OnUi(() => _actions.NewSession(body.ProjectId, agent, body.Prompt, body.Model, body.Effort, body.Name));
                    if (session == null) { await WriteJson(res, 400, new { error = "could not start session (missing project?)" }); return; }
                    await WriteJson(res, 200, new { ok = true, id = session.Id, projectId = session.ProjectId, agent = session.Agent.ToString().ToLowerInvariant(), label = session.Label });
                    return;
                }
                case "POST" when Match(path, "/sessions/", "/activate", out int sid):
                {
                    var ok = await OnUi(() => _actions.ActivateSession(sid));
                    await WriteJson(res, ok ? 200 : 404, ok ? new { ok = true } : new { error = "session not found" });
                    return;
                }
                case "POST" when Match(path, "/sessions/", "/close", out int sid):
                {
                    var ok = await OnUi(() => _actions.CloseSession(sid));
                    await WriteJson(res, ok ? 200 : 404, ok ? new { ok = true } : new { error = "session not found" });
                    return;
                }
                case "POST" when Match(path, "/sessions/", "/input", out int sid):
                {
                    var body = await ReadBody(req);
                    if (body.Data == null) { await WriteJson(res, 400, new { error = "data required" }); return; }
                    var ok = await OnUi(() => _actions.WriteInput(sid, body.Data));
                    await WriteJson(res, ok ? 200 : 404, ok ? new { ok = true } : new { error = "session not found" });
                    return;
                }
                case "POST" when Match(path, "/sessions/", "/paste", out int sid):
                {
                    var body = await ReadBody(req);
                    if (body.Text == null) { await WriteJson(res, 400, new { error = "text required" }); return; }
                    var ok = await OnUi(() => _actions.Paste(sid, body.Text, body.Submit == true));
                    await WriteJson(res, ok ? 200 : 404, ok ? new { ok = true } : new { error = "session not found" });
                    return;
                }
                case "POST" when Match(path, "/sessions/", "/enter", out int sid):
                {
                    var ok = await OnUi(() => _actions.WriteInput(sid, "\r"));
                    await WriteJson(res, ok ? 200 : 404, ok ? new { ok = true } : new { error = "session not found" });
                    return;
                }
                case "POST" when Match(path, "/workflows/", "/run", out string wfId):
                {
                    var ok = await OnUi(() => _actions.RunWorkflow(wfId));
                    await WriteJson(res, ok ? 200 : 404, ok ? new { ok = true } : new { error = "workflow not found" });
                    return;
                }
                case "POST" when path == "/launch":
                {
                    var body = await ReadBody(req);
                    if (!TryParseAgent(body.Agent, out var agent) || agent == AgentKind.Shell)
                    { await WriteJson(res, 400, new { error = "agent required (claude|codex|grok)" }); return; }
                    var session = await OnUi(() => _actions.Launch(agent));
                    if (session == null) { await WriteJson(res, 400, new { error = "could not launch (no project selected?)" }); return; }
                    await WriteJson(res, 200, new { ok = true, id = session.Id, projectId = session.ProjectId, agent = session.Agent.ToString().ToLowerInvariant() });
                    return;
                }
                case "POST" when path == "/dictation/toggle":
                    await OnUi(() => { _actions.ToggleDictation(); return true; });
                    await WriteJson(res, 200, new { ok = true });
                    return;
                case "POST" when path == "/window/show":
                    await OnUi(() => { _actions.ShowWindow(); return true; });
                    await WriteJson(res, 200, new { ok = true });
                    return;
                default:
                    await WriteJson(res, 404, new { error = "not found" });
                    return;
            }
        }
        catch (Exception ex)
        {
            Log.Error($"control API {req.HttpMethod} {req.Url?.AbsolutePath}", ex);
            try { await WriteJson(res, 500, new { error = ex.Message }); } catch (Exception) { }
        }
    }

    bool Authorized(HttpListenerRequest req)
    {
        if (_token == null) return true;
        var header = req.Headers["Authorization"];
        if (header != null && header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            && header["Bearer ".Length..].Trim() == _token) return true;
        return false;
    }

    Task<T> OnUi<T>(Func<T> action) => _ui.InvokeAsync(action).Task;

    static bool TryParseAgent(string? value, out AgentKind agent)
    {
        agent = default;
        return !string.IsNullOrWhiteSpace(value) && Enum.TryParse(value, ignoreCase: true, out agent);
    }

    static bool Match(string path, string prefix, string suffix, out int id)
    {
        id = 0;
        if (!path.StartsWith(prefix, StringComparison.Ordinal) || !path.EndsWith(suffix, StringComparison.Ordinal)) return false;
        var mid = path[prefix.Length..(path.Length - suffix.Length)];
        if (mid.EndsWith('/')) mid = mid[..^1];
        return int.TryParse(mid, out id);
    }

    static bool Match(string path, string prefix, string suffix, out string id)
    {
        id = "";
        if (!path.StartsWith(prefix, StringComparison.Ordinal) || !path.EndsWith(suffix, StringComparison.Ordinal)) return false;
        var mid = path[prefix.Length..(path.Length - suffix.Length)];
        if (mid.EndsWith('/')) mid = mid[..^1];
        if (mid.Length == 0 || mid.Contains('/')) return false;
        id = mid;
        return true;
    }

    static async Task<Body> ReadBody(HttpListenerRequest req)
    {
        if (!req.HasEntityBody) return new Body();
        using var reader = new StreamReader(req.InputStream, req.ContentEncoding);
        var text = await reader.ReadToEndAsync();
        if (string.IsNullOrWhiteSpace(text)) return new Body();
        return JsonSerializer.Deserialize<Body>(text, Json) ?? new Body();
    }

    static async Task WriteJson(HttpListenerResponse res, int status, object body)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(body, Json));
        res.StatusCode = status;
        res.ContentType = "application/json; charset=utf-8";
        res.ContentLength64 = bytes.Length;
        await res.OutputStream.WriteAsync(bytes);
        res.Close();
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { if (_listener.IsListening) _listener.Stop(); } catch (Exception) { }
        try { _listener.Close(); } catch (Exception) { }
        _thread?.Join(1000);
        _cts.Dispose();
    }

    sealed class Body
    {
        public string? Id { get; set; }
        public string? ProjectId { get; set; }
        public string? Agent { get; set; }
        public string? Prompt { get; set; }
        public string? Model { get; set; }
        public string? Effort { get; set; }
        public string? Name { get; set; }
        public string? Data { get; set; }
        public string? Text { get; set; }
        public bool? Submit { get; set; }
    }
}

/// <summary>UI-thread operations used by <see cref="ControlServer"/>. Implementations must run on the Dispatcher.</summary>
interface IControlActions
{
    object GetState();
    object[] GetWorkflows();
    bool SelectProject(string id);
    Session? NewSession(string? projectId, AgentKind agent, string? prompt, string? model, string? effort, string? name);
    bool ActivateSession(int id);
    bool CloseSession(int id);
    bool WriteInput(int id, string data);
    bool Paste(int id, string text, bool submit);
    bool RunWorkflow(string id);
    Session? Launch(AgentKind agent);
    void ToggleDictation();
    void ShowWindow();
}

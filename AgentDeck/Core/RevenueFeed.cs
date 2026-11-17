using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace AgentDeck.Core;

/// <summary>One new payment or subscriber, as summarised by the Airwallex relay worker (relay/airwallex).</summary>
sealed record RevenueEvent(string Key, string Kind, string? Ref, string Label, decimal? Amount, string? Currency, string Url, long At,
                           string? Country = null, string? Customer = null, string? Email = null, string? Method = null,
                           string? CardCountry = null, string? Funding = null, string? Description = null, string? OrderId = null,
                           double? Risk = null);

sealed record RevenueStats(int Subscribers, decimal Mrr, string Currency, long At,
                           decimal? Today = null, int? TodayCount = null, decimal? Month = null, int? MonthCount = null);

/// <summary>
/// Polls the Cloudflare relay that receives Airwallex webhooks. New events queue up until they're opened from the deck;
/// stats (active subscribers, MRR) refresh every few minutes. Configured by AIRWALLEX_RELAY_URL and AIRWALLEX_RELAY_TOKEN;
/// without them the feed is off and the deck key stays a normal session slot.
/// </summary>
sealed class RevenueFeed : IDisposable
{
    const int EventPollMs = 15_000, StatsPollMs = 5 * 60_000;
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    static readonly string CursorFile = Path.Combine(Log.Dir, "revenue-cursor.txt");

    readonly HttpClient _http;
    readonly Uri? _base;
    readonly CancellationTokenSource _stop = new();
    readonly object _gate = new();
    readonly List<RevenueEvent> _unseen = [];
    string? _cursor;

    public bool Enabled => _base != null;
    public RevenueStats? Stats { get; private set; }
    /// <summary>Changes whenever the queue or stats change, so the deck knows to redraw.</summary>
    public int Version { get; private set; }
    /// <summary>When the last payment or subscriber arrived (TickCount64), for the celebration animation.</summary>
    public long LastCelebrationTicks { get; private set; } = -1;

    public RevenueFeed()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        var url = Env.Get("AIRWALLEX_RELAY_URL");
        var token = Env.Get("AIRWALLEX_RELAY_TOKEN");
        if (url == null || token == null || !Uri.TryCreate(url.TrimEnd('/') + "/", UriKind.Absolute, out var b)) return;
        _base = b;
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        try { _cursor = File.Exists(CursorFile) ? File.ReadAllText(CursorFile).Trim() : null; } catch { }
        _ = Task.Run(() => Loop(_stop.Token));
    }

    public RevenueEvent[] Unseen() { lock (_gate) return [.. _unseen]; }

    /// <summary>Takes the oldest unseen event off the queue (pressing the key opens it).</summary>
    public RevenueEvent? Pop()
    {
        lock (_gate)
        {
            if (_unseen.Count == 0) return null;
            var e = _unseen[0];
            _unseen.RemoveAt(0);
            Version++;
            return e;
        }
    }

    async Task Loop(CancellationToken ct)
    {
        long nextStats = 0;
        while (!ct.IsCancellationRequested)
        {
            if (Environment.TickCount64 >= nextStats)
            {
                await PollStats(ct);
                nextStats = Environment.TickCount64 + StatsPollMs;
            }
            await PollEvents(ct);
            try { await Task.Delay(EventPollMs, ct); } catch (OperationCanceledException) { return; }
        }
    }

    async Task PollEvents(CancellationToken ct)
    {
        try
        {
            // First run: only take a cursor, so we don't replay a month of history onto the deck.
            var path = _cursor == null ? "events?latest=1" : $"events?after={Uri.EscapeDataString(_cursor)}";
            var res = await _http.GetFromJsonAsync<EventsResponse>(new Uri(_base!, path), Json, ct);
            if (res == null) return;
            bool gotNew = false, chime = false;
            lock (_gate)
            {
                if (_cursor != null)
                    foreach (var e in res.Events.Where(e => _unseen.All(u => u.Key != e.Key)))
                    {
                        // A completed payment supersedes its "checkout started" card.
                        if (e.Kind != "intent" && e.Ref != null) _unseen.RemoveAll(u => u.Kind == "intent" && u.Ref == e.Ref);
                        _unseen.Add(e);
                        gotNew = true;
                        chime |= e.Kind != "intent"; // checkouts are quiet
                    }
                if (gotNew) Version++;
                if (chime) LastCelebrationTicks = Environment.TickCount64;
            }
            if (chime) { Chime.PlayPayment(); _ = PollStats(ct); }
            if (res.Cursor != null && res.Cursor != _cursor)
            {
                _cursor = res.Cursor;
                try { File.WriteAllText(CursorFile, _cursor); } catch { }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { Log.Info($"revenue relay: {ex.Message}"); }
    }

    async Task PollStats(CancellationToken ct)
    {
        try
        {
            var stats = await _http.GetFromJsonAsync<RevenueStats>(new Uri(_base!, "stats"), Json, ct);
            if (stats != null) { Stats = stats; lock (_gate) Version++; }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { Log.Info($"revenue stats: {ex.Message}"); }
    }

    sealed record EventsResponse(RevenueEvent[] Events, string? Cursor);

    public void Dispose()
    {
        _stop.Cancel();
        _http.Dispose();
    }
}

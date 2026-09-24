using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace AgentDeck.Core;

/// <summary>Minimal OpenAI Responses API client for gpt-6-luna, tuned for latency.</summary>
sealed class OpenAiClient
{
    public const string Model = "gpt-6-luna";

    readonly HttpClient? _http;

    public OpenAiClient(string? apiKey)
    {
        if (apiKey == null) return;
        var handler = new SocketsHttpHandler
        {
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(10), // keep TLS warm between dictations
            PooledConnectionLifetime = TimeSpan.FromMinutes(30),
        };
        _http = new HttpClient(handler) { BaseAddress = new Uri("https://api.openai.com/"), Timeout = TimeSpan.FromSeconds(15) };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    }

    public bool Enabled => _http != null;

    /// <summary>Open the HTTPS connection ahead of time; costs no tokens.</summary>
    public async Task WarmUpAsync()
    {
        if (_http == null) return;
        try { using var _ = await _http.GetAsync($"v1/models/{Model}"); }
        catch (Exception) { }
    }

    public async Task<string> RespondAsync(string instructions, string input, TimeSpan timeout, CancellationToken ct = default)
    {
        if (_http == null) throw new InvalidOperationException("OPENAI_API_KEY is not set.");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var body = new
        {
            model = Model,
            instructions,
            input,
            reasoning = new { effort = "none" },
            service_tier = "priority",
        };
        using var response = await _http.PostAsJsonAsync("v1/responses", body, cts.Token);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"OpenAI {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(cts.Token)}");

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cts.Token), cancellationToken: cts.Token);
        var text = new StringBuilder();
        foreach (var item in doc.RootElement.GetProperty("output").EnumerateArray())
        {
            if (item.GetProperty("type").GetString() != "message") continue;
            foreach (var part in item.GetProperty("content").EnumerateArray())
                if (part.GetProperty("type").GetString() == "output_text")
                    text.Append(part.GetProperty("text").GetString());
        }
        return text.ToString().Trim();
    }
}

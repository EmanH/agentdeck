using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentDeck.Core;

/// <summary>
/// Minimal TypeSafe classifier client (the Jev model): one state, one "choice" question, and back comes the
/// winning option with the probability of every option. Same wire format as Framework.AI.TypeSafe.
/// </summary>
sealed class TypeSafeClient
{
    public const string Model = "jev-latest";
    /// <summary>TypeSafe accepts at most this many options on one choice question.</summary>
    public const int MaxOptions = 255;

    readonly HttpClient? _http;

    public TypeSafeClient(string? apiKey)
    {
        if (apiKey == null) return;
        var handler = new SocketsHttpHandler { PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5) };
        _http = new HttpClient(handler) { BaseAddress = new Uri("https://api.typesafe.ai/v1/"), Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    }

    public bool Enabled => _http != null;

    public sealed record ChoiceResult(string Choice, double Confidence, IReadOnlyDictionary<string, double> Probabilities)
    {
        public double ProbabilityOf(string option) => Probabilities.TryGetValue(option, out var p) ? p : 0;
    }

    /// <param name="options">Option name to description (at most <see cref="MaxOptions"/>).</param>
    public async Task<ChoiceResult> ChooseAsync(object state, string instructions, IReadOnlyList<(string Name, string Description)> options,
                                                CancellationToken ct = default)
    {
        if (_http == null) throw new InvalidOperationException("TYPESAFE_AI_API_KEY is not set.");
        if (options.Count is 0 or > MaxOptions) throw new ArgumentException($"Need 1..{MaxOptions} options.", nameof(options));

        var criteria = new JsonObject();
        foreach (var (name, description) in options) criteria[name] = description;
        var body = new JsonObject
        {
            ["state"] = JsonSerializer.SerializeToNode(state),
            ["model"] = Model,
            ["questions"] = new JsonObject
            {
                ["pick"] = new JsonObject { ["type"] = "choice", ["instructions"] = instructions, ["criteria"] = criteria },
            },
        };

        using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync("systemone", content, ct);
        var raw = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"TypeSafe {(int)response.StatusCode}: {(raw.Length > 300 ? raw[..300] : raw)}");

        using var doc = JsonDocument.Parse(raw);
        var answer = doc.RootElement.GetProperty("answers").GetProperty("pick");
        var probabilities = answer.GetProperty("probabilities").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetDouble());
        return new ChoiceResult(answer.GetProperty("choice").GetString() ?? "", answer.GetProperty("confidence").GetDouble(), probabilities);
    }
}

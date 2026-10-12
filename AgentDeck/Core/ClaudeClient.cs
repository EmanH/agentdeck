using System.Text;
using Anthropic;
using Anthropic.Models.Messages;

namespace AgentDeck.Core;

/// <summary>Minimal Claude Messages API client for Claude Haiku 5.5, tuned for latency.</summary>
sealed class ClaudeClient
{
    public const string Model = "claude-haiku-5-5";

    readonly AnthropicClient? _client;

    public ClaudeClient(string? apiKey)
    {
        if (apiKey == null) return;
        // No SDK retries: the caller has a tight deadline and its own fallback.
        _client = new AnthropicClient { ApiKey = apiKey, MaxRetries = 0, Timeout = TimeSpan.FromSeconds(15) };
    }

    public bool Enabled => _client != null;

    /// <summary>Open the HTTPS connection ahead of time; costs no tokens.</summary>
    public async Task WarmUpAsync()
    {
        if (_client == null) return;
        try { await _client.Models.Retrieve(Model); }
        catch (Exception) { }
    }

    public async Task<string> RespondAsync(string instructions, string input, TimeSpan timeout, CancellationToken ct = default)
    {
        if (_client == null) throw new InvalidOperationException("ANTHROPIC_API_KEY is not set.");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var response = await _client.Messages.Create(new MessageCreateParams
        {
            Model = Model,
            MaxTokens = 8192,
            Thinking = new ThinkingConfigDisabled(), // cleanup needs no reasoning; thinking only adds latency
            OutputConfig = new OutputConfig { Effort = Effort.Low },
            System = instructions,
            Messages = [new() { Role = Role.User, Content = input }],
        }, cts.Token);
        if (response.StopReason != "end_turn")
            throw new InvalidOperationException($"Claude stopped early ({response.StopReason}).");

        var text = new StringBuilder();
        foreach (var block in response.Content.Select(b => b.Value).OfType<TextBlock>()) text.Append(block.Text);
        return text.ToString().Trim();
    }
}

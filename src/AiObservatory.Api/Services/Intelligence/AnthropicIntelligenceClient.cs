using System.Text.Json.Nodes;
using Anthropic.SDK;
using Anthropic.SDK.Constants;
using Anthropic.SDK.Messaging;
using CommonFunction = Anthropic.SDK.Common.Function;
using CommonTool = Anthropic.SDK.Common.Tool;

namespace AiObservatory.Api.Services.Intelligence;

public class AnthropicIntelligenceClient
{
    private const string InsightsToolName = "record_insights";
    private readonly AnthropicClient? _client;
    private readonly ILogger<AnthropicIntelligenceClient> _logger;

    /// <summary>True when ANTHROPIC_API_KEY is configured. False = AI features silently disabled.</summary>
    public bool IsConfigured => _client is not null;

    public AnthropicIntelligenceClient(IConfiguration configuration, ILogger<AnthropicIntelligenceClient> logger)
    {
        var apiKey = configuration["ANTHROPIC_API_KEY"];
        // Whitespace/empty key means "disabled", same as unset — otherwise an empty-string
        // env var makes IsConfigured true and every worker cycle calls the API and 401s.
        _client = !string.IsNullOrWhiteSpace(apiKey) ? new AnthropicClient(new APIAuthentication(apiKey)) : null;
        _logger = logger;
        if (!IsConfigured)
        {
            _logger.LogWarning("ANTHROPIC_API_KEY not set — AI insights and explanations are disabled.");
        }
    }

    public virtual async Task<string> GenerateExplanationAsync(
        string title,
        string body,
        CancellationToken ct = default
    )
    {
        if (_client is null)
        {
            throw new InvalidOperationException("ANTHROPIC_API_KEY is not configured.");
        }
        var prompt = $"""
            An AI cost analysis system flagged this insight about API usage patterns:

            Title: {title}

            {body}

            Provide concise, actionable guidance on how to implement this recommendation.
            Format your response as markdown with numbered steps where appropriate.
            Be specific: include API parameters, configuration settings, or code patterns where relevant.
            Keep it under 300 words.
            """;

        var parameters = new MessageParameters
        {
            Model = AnthropicModels.Claude46Sonnet,
            MaxTokens = 1024,
            Messages = [new Message { Role = RoleType.User, Content = [new TextContent { Text = prompt }] }],
        };

        var response = await _client.Messages.GetClaudeMessageAsync(parameters, ct);

        return response.Content.OfType<TextContent>().FirstOrDefault()?.Text
            ?? throw new InvalidOperationException("Explanation response contained no text.");
    }

    public virtual async Task<string> GenerateInsightsJsonAsync(string prompt, CancellationToken ct = default)
    {
        if (_client is null)
        {
            throw new InvalidOperationException("ANTHROPIC_API_KEY is not configured.");
        }
        var client = _client;

        var insightSchema = JsonNode.Parse(
            """
            {
              "type": "object",
              "properties": {
                "insights": {
                  "type": "array",
                  "items": {
                    "type": "object",
                    "properties": {
                      "type": { "type": "string", "enum": ["summary", "efficiency", "anomaly", "recommendation"] },
                      "title": { "type": "string" },
                      "body": { "type": "string" },
                      "data": { "type": "object" }
                    },
                    "required": ["type", "title", "body", "data"]
                  }
                }
              },
              "required": ["insights"]
            }
            """
        )!;

        var tool = new CommonTool(
            new CommonFunction(InsightsToolName, "Report AI usage insights as a structured list", insightSchema)
        );

        var parameters = new MessageParameters
        {
            Model = AnthropicModels.Claude46Sonnet,
            MaxTokens = 4096,
            Messages = [new Message { Role = RoleType.User, Content = [new TextContent { Text = prompt }] }],
            Tools = [tool],
            ToolChoice = new ToolChoice { Type = ToolChoiceType.Tool, Name = InsightsToolName },
        };

        var response = await client.Messages.GetClaudeMessageAsync(parameters, ct);

        var toolUse =
            response.Content.OfType<ToolUseContent>().FirstOrDefault(t => t.Name == InsightsToolName)
            ?? throw new InvalidOperationException("Intelligence response did not contain expected tool use block.");

        var insightsArray =
            toolUse.Input?["insights"]?.AsArray()
            ?? throw new InvalidOperationException("Intelligence tool use response missing 'insights' array.");

        _logger.LogInformation("Intelligence client received {Count} insights from Claude", insightsArray.Count);

        return insightsArray.ToJsonString();
    }
}

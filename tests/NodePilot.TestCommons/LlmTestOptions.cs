using NodePilot.Ai;

namespace NodePilot.TestCommons;

/// <summary>
/// Builds a <see cref="LlmOptions"/> with exactly one profile selected as active — the shape almost
/// every test needs now that connection settings live per profile instead of on the section root.
/// </summary>
public static class LlmTestOptions
{
    public static LlmOptions WithProfile(
        string id = "default",
        string name = "Test profile",
        string baseUrl = "https://api.openai.com/v1",
        string model = "gpt-4o-mini",
        bool enabled = true,
        string? apiKey = null,
        int maxTokens = 4096,
        int timeoutSeconds = 90,
        bool enableToolCalling = false,
        int toolCallMaxDepth = 6)
        => new()
        {
            Enabled = enabled,
            ActiveProfileId = id,
            Profiles = new Dictionary<string, LlmProfileOptions>(StringComparer.OrdinalIgnoreCase)
            {
                [id] = new()
                {
                    Name = name,
                    BaseUrl = baseUrl,
                    Model = model,
                    ApiKey = apiKey,
                    MaxTokens = maxTokens,
                    TimeoutSeconds = timeoutSeconds,
                    EnableToolCalling = enableToolCalling,
                    ToolCallMaxDepth = toolCallMaxDepth,
                },
            },
        };

    /// <summary>Enabled, but with no profile to resolve — the 503 <c>LLM_NO_ACTIVE_PROFILE</c>
    /// state.</summary>
    public static LlmOptions EnabledWithoutProfile() => new() { Enabled = true };
}

namespace NodePilot.Ai;

/// <summary>
/// Which OpenAI wire dialect an endpoint speaks. It is not a config field: the operator already
/// states it by typing the URL, and the two dialects are addressed under distinct paths
/// (<c>/chat/completions</c> and <c>/responses</c>). See
/// <see cref="LlmEndpointGuard.ResolveEndpoint"/> for the detection rule.
/// </summary>
public enum LlmApiFlavor
{
    /// <summary>The classic <c>POST /chat/completions</c> format: OpenAI plus every local runtime
    /// (Ollama, LM Studio, vLLM, LocalAI, llama.cpp).</summary>
    ChatCompletions,

    /// <summary>OpenAI's <c>POST /responses</c> format. Required by models that are not served on
    /// chat completions at all.</summary>
    Responses,
}

/// <summary>
/// A configured LLM <c>BaseUrl</c> after validation and dialect detection: the URL to POST to, the
/// dialect the endpoint speaks, and the root that sibling paths hang off.
/// </summary>
/// <param name="PostUrl">The exact URL completions are POSTed to.</param>
/// <param name="ApiRoot">
/// The endpoint root, that is <see cref="PostUrl"/> minus its dialect suffix. Sibling paths hang
/// off this; currently only the settings test-probe's <c>/models</c>.
/// </param>
/// <param name="Flavor">The wire dialect the endpoint speaks.</param>
public sealed record LlmEndpointTarget(string PostUrl, string ApiRoot, LlmApiFlavor Flavor);

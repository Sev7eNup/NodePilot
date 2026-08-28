using Microsoft.AspNetCore.Mvc;
using NodePilot.Ai;
using NodePilot.Api.Ai;

namespace NodePilot.Api.Configuration;

/// <summary>
/// Error codes for the two ways the LLM integration can be unavailable. Every AI endpoint checks
/// both before calling a service, because <see cref="ILlmClientFactory.Create"/> throws without an
/// active profile and that must surface as a 503 instead of an unhandled exception.
/// </summary>
public static class LlmAvailability
{
    public const string DisabledCode = "LLM_DISABLED";
    public const string NoActiveProfileCode = "LLM_NO_ACTIVE_PROFILE";

    public const string NoActiveProfileMessage =
        "No active LLM profile is configured. Add a profile under Settings → System → Integrations → LLM "
        + "and select it as the active profile.";

    public const string DisabledMessage = "AI assistant is disabled. Set Llm:Enabled=true in configuration.";

    /// <summary>
    /// True when <paramref name="options"/> is enabled but no profile resolves, so the caller
    /// should answer <see cref="NoActiveProfileCode"/>. False when the integration is off
    /// altogether; that case belongs to <see cref="DisabledCode"/> and is checked first.
    /// </summary>
    public static bool IsMissingActiveProfile(LlmOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.Enabled && !options.TryResolveActiveProfile(out _);
    }

    /// <summary>
    /// The gate every AI endpoint runs first: returns the 503 to send back, or null when the
    /// integration is usable. Both checks live here so an endpoint cannot test one and miss the
    /// other. <paramref name="disabledMessage"/> exists for the knowledge endpoint, which answers
    /// in German; every other caller takes the default.
    /// </summary>
    public static ObjectResult? Unavailable(ControllerBase controller, LlmOptions options, string? disabledMessage = null)
    {
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentNullException.ThrowIfNull(options);

        if (!options.Enabled)
            return controller.LlmServiceUnavailable(DisabledCode, disabledMessage ?? DisabledMessage);
        if (IsMissingActiveProfile(options))
            return controller.LlmServiceUnavailable(NoActiveProfileCode, NoActiveProfileMessage);
        return null;
    }
}

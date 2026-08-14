using Microsoft.AspNetCore.Mvc;
using NodePilot.Ai;
using NodePilot.Api.Ai;

namespace NodePilot.Api.Configuration;

/// <summary>
/// The two ways the LLM integration can be unavailable, as API error codes. Every AI endpoint
/// gates on both before touching a service — <see cref="ILlmClientFactory.Create"/> throws without
/// an active profile, and that has to surface as a clean 503 rather than an unhandled exception.
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
    /// True when <paramref name="options"/> is enabled but no profile resolves — i.e. the caller
    /// should answer <see cref="NoActiveProfileCode"/>. Returns false when the integration is off
    /// altogether; that case belongs to <see cref="DisabledCode"/> and is checked first.
    /// </summary>
    public static bool IsMissingActiveProfile(LlmOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.Enabled && !options.TryResolveActiveProfile(out _);
    }

    /// <summary>
    /// The gate every AI endpoint runs first: returns the 503 to send back, or null when the
    /// integration is usable. Both checks live here so an endpoint cannot accidentally test one
    /// and not the other — <c>ILlmClientFactory.Create</c> throws without an active profile, and
    /// that has to surface as a clean 503 rather than an unhandled exception.
    /// <paramref name="disabledMessage"/> exists only because the knowledge endpoint answers in
    /// German; every other caller takes the default.
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

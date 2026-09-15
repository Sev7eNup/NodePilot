using System.Collections.Concurrent;
using System.Net;

namespace NodePilot.Ai;

/// <summary>
/// Upstream quirk shared by both wire dialects: reasoning models reject sampling parameters and
/// answer HTTP 400 when a request carries <c>temperature</c>. The parameter is optional, so the
/// request is retried once without it rather than failing the caller.
///
/// <para>The endpoint is remembered per <c>postUrl|model</c> so later calls skip the attempt that
/// is known to fail. The memory is process-wide and additive; a model that starts accepting the
/// parameter again only regains it after a restart, which costs nothing but a default sampling
/// temperature.</para>
/// </summary>
internal static class LlmTemperatureQuirk
{
    private static readonly ConcurrentDictionary<string, byte> Endpoints =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// True when the failure is an upstream rejection of <c>temperature</c>. The body has to name
    /// the parameter and say it is unsupported: a 400 that merely echoes the sent parameters would
    /// otherwise strip a temperature the endpoint never objected to.
    /// </summary>
    internal static bool IsUnsupported(LlmException ex) =>
        ex.Kind == LlmErrorKind.UpstreamError
        && ex.HttpStatus == (int)HttpStatusCode.BadRequest
        && ex.BodyExcerpt is { } body
        && body.Contains("temperature", StringComparison.OrdinalIgnoreCase)
        && (body.Contains("unsupported", StringComparison.OrdinalIgnoreCase)
            || body.Contains("not supported", StringComparison.OrdinalIgnoreCase)
            || body.Contains("does not support", StringComparison.OrdinalIgnoreCase));

    internal static string Key(string postUrl, string model) => $"{postUrl}|{model}";

    internal static bool IsKnown(string key) => Endpoints.ContainsKey(key);

    internal static void Remember(string key) => Endpoints.TryAdd(key, 0);
}

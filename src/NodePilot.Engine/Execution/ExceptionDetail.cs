namespace NodePilot.Engine.Execution;

/// <summary>
/// Flattens an exception chain into a single human-readable line.
///
/// <para>
/// Motivation (field finding 2026-08-02): a failing step persisted only
/// <c>ex.Message</c>. For wrapper exceptions that is worthless — an
/// <c>DbUpdateException</c> stores the literal 87-character string
/// "An error occurred while saving the entity changes. See the inner exception for
/// details.", and the actual cause (a primary-key violation) was only recoverable from
/// the server log. The same happened with SMTP, where <c>SmtpException</c> contributes
/// nothing beyond "Failure sending mail.". Both classes of error are diagnosable only
/// through their inner exception, so the inner chain has to travel with the message.
/// </para>
/// </summary>
internal static class ExceptionDetail
{
    /// <summary>Maximum number of chain links rendered — enough for cause-of-cause, short of a stack dump.</summary>
    private const int MaxLinks = 4;

    /// <summary>
    /// Renders <paramref name="ex"/> and its inner exceptions as
    /// <c>outer -> inner -> innermost</c>. Links whose text is already contained in the
    /// previous link are dropped, so wrappers that merely repeat their cause stay silent
    /// instead of doubling the message.
    /// </summary>
    public static string Describe(Exception? ex)
    {
        if (ex is null) return string.Empty;

        // AggregateException hides the interesting exception one level down and renders as
        // "One or more errors occurred. (...)". Unwrap the single-inner case; keep genuine
        // multi-error aggregates intact so no failure silently disappears.
        if (ex is AggregateException aggregate)
        {
            var flattened = aggregate.Flatten();
            if (flattened.InnerExceptions.Count == 1) ex = flattened.InnerExceptions[0];
        }

        var parts = new List<string>(MaxLinks);
        for (var current = ex; current is not null && parts.Count < MaxLinks; current = current.InnerException)
        {
            var message = current.Message?.Trim();
            if (string.IsNullOrEmpty(message)) continue;

            // Skip links that add nothing: many wrappers embed their cause verbatim.
            if (parts.Count > 0 && parts[^1].Contains(message, StringComparison.Ordinal)) continue;

            parts.Add(message);
        }

        return parts.Count == 0 ? ex.GetType().Name : string.Join(" -> ", parts);
    }
}

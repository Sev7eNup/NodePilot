using System.IO;

namespace NodePilot.ServiceSwitcher.Services;

internal sealed class AllowListReader
{
    public async Task<IReadOnlyList<string>> ReadAsync(string path, CancellationToken cancellationToken)
    {
        string content;
        try
        {
            content = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"Allowlist could not be read from '{path}': {exception.Message}", exception);
        }

        var entries = content
            .Split([',', ';', '\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (entries.Length == 0)
            throw new InvalidOperationException($"Allowlist '{path}' is empty. Empty allowlists are rejected for safety.");

        return entries;
    }
}

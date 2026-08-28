using System.Text.Json;
using Microsoft.Extensions.Configuration;
using NodePilot.Core.Interfaces;
using NodePilot.Engine.Security;

namespace NodePilot.Engine.Activities;

/// <summary>
/// Resolves the <c>source = file | inline</c> payload that <see cref="JsonQueryActivity"/> and
/// <see cref="XmlQueryActivity"/> both accept. The two differ only in their size cap and in the
/// wording of the oversize message, so both are supplied by the caller — as is the <c>Fail</c>
/// projection that carries the activity prefix.
/// </summary>
internal static class QueryPayloadSource
{
    private static readonly IConfiguration EmptyPathGuardConfiguration =
        new ConfigurationBuilder().Build();

    public static async Task<(string? Content, ActivityResult? Error)> LoadAsync(
        string source,
        JsonElement config,
        IConfiguration? pathGuardConfig,
        long maxBytes,
        Func<string, ActivityResult> fail,
        Func<string, long, string> oversizeMessage,
        CancellationToken ct)
    {
        if (source == "file")
        {
            var path = config.GetStringOrNull("path");
            if (string.IsNullOrWhiteSpace(path))
                return (null, fail("'path' is required when source=file"));

            // Apply PathGuard unconditionally. AllowedRoots remain optional, but the
            // link-local reparse check is not: a local-looking JSON/XML path may be a
            // junction to an attacker-controlled UNC share even when no IConfiguration
            // was injected.
            try
            {
                PathGuard.Validate(pathGuardConfig ?? EmptyPathGuardConfiguration, path);
            }
            catch (InvalidOperationException ex)
            {
                return (null, fail($"file access denied: {ex.Message}"));
            }

            if (!File.Exists(path))
                return (null, fail($"file not found: {path}"));

            // Check size before reading so a 10 GiB file doesn't pin the managed heap.
            var fileInfo = new FileInfo(path);
            if (fileInfo.Length > maxBytes)
                return (null, fail(oversizeMessage(path, fileInfo.Length)));

            return (await File.ReadAllTextAsync(path, ct), null);
        }

        var inline = config.GetString("content", "");
        if (string.IsNullOrWhiteSpace(inline))
            return (null, fail("'content' is required when source=inline"));
        return (inline, null);
    }
}

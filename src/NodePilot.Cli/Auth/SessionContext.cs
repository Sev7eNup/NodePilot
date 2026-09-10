using NodePilot.Core.Clients;

namespace NodePilot.Cli.Auth;

/// <summary>
/// Resolved per-command session state. Built by <see cref="SessionResolver"/> from
/// CLI flags, environment variables, config and the encrypted token store.
/// </summary>
public sealed class SessionContext
{
    public required string Profile { get; init; }
    public string? Server { get; init; }
    public StoredSession? Session { get; init; }
    public bool AllowInsecureLoopback { get; init; }

    /// <summary>How this call treats the server certificate (pin and/or bypass).</summary>
    public ClientTlsOptions Tls { get; init; } = ClientTlsOptions.None;

    /// <summary>Set when a stored pin was skipped because it belongs to another server origin.</summary>
    public string? TlsPinNotice { get; init; }

    public bool HasServer => !string.IsNullOrWhiteSpace(Server);
    public bool HasSession => Session is not null && !string.IsNullOrWhiteSpace(Session.Token);
}

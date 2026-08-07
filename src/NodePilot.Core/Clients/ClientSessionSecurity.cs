namespace NodePilot.Core.Clients;

/// <summary>
/// Shared facts for the two HTTP-only clients (the <c>np</c> CLI and the
/// <c>nodepilot-mcp</c> server). Both deliberately copy their HTTP plumbing
/// (ADR 0005), but they MUST agree on the DPAPI session-blob format: the MCP
/// server reads the same <c>%APPDATA%\NodePilot\session-&lt;profile&gt;.dat</c>
/// file that <c>np auth login</c> writes. Before this constant existed, the
/// entropy literal was hard-coded in both projects — a silent-breakage coupling
/// (coherence audit 2026-08).
/// </summary>
public static class ClientSessionSecurity
{
    /// <summary>
    /// DPAPI additional entropy for the session blob. The value predates the MCP
    /// server and is part of the on-disk format — changing it would orphan every
    /// existing logged-in session, so it stays "NodePilot.Cli/v1" even though the
    /// blob is now shared by two executables.
    /// </summary>
    public const string DpapiSessionEntropy = "NodePilot.Cli/v1";
}

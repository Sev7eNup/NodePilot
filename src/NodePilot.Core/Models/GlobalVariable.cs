namespace NodePilot.Core.Models;

/// <summary>
/// Admin-managed key/value pair accessible from every workflow via the
/// <c>{{globals.NAME}}</c> template. Conceptually equivalent to SCOrch "Variables":
/// a shared pool of constants (database connection strings, API endpoints,
/// environment tags, credentials-for-third-party-APIs) that keeps hard-coded values
/// out of individual workflow definitions.
///
/// <para>
/// When <see cref="IsSecret"/> is true, <see cref="Value"/> is stored DPAPI-encrypted
/// (scope configurable via <c>Credentials:DpapiScope</c>) and never returned through
/// <c>GET /api/global-variables</c> — callers see <c>"***"</c> instead. Non-secret
/// values are stored plaintext and returned verbatim.
/// </para>
///
/// <para>
/// The engine resolves <c>{{globals.NAME}}</c> by looking up the row and (for
/// secrets) decrypting at step-execution time. The resolved value flows into the
/// same <c>Variables</c> dict the step-output templates use, so downstream activities
/// receive the plaintext just as they would for an upstream <c>runScript</c> output.
/// The <c>OutputRedactor</c> masks the final value before persisting Output/ErrorOutput
/// so decrypted secrets never leak into the audit-readable step log.
/// </para>
/// </summary>
public class GlobalVariable
{
    public Guid Id { get; set; }

    /// <summary>
    /// Case-sensitive identifier. Convention: <c>SCREAMING_SNAKE_CASE</c>. Restricted
    /// to <c>[A-Za-z0-9_\-]</c> by the controller so templates stay unambiguous
    /// (a hyphen in the name doesn't collide with anything else in the
    /// <c>{{globals.NAME}}</c> grammar).
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Plaintext for non-secret variables; Base64-encoded DPAPI ciphertext for secret
    /// variables (see <see cref="IsSecret"/>).
    /// </summary>
    public string Value { get; set; } = string.Empty;

    public bool IsSecret { get; set; }

    public string? Description { get; set; }

    /// <summary>
    /// Organizational folder membership. Every variable belongs to exactly one
    /// <see cref="GlobalVariableFolder"/>, defaulting to the singleton Root
    /// (<see cref="GlobalVariableFolder.RootFolderId"/>). Purely cosmetic — the folder never
    /// affects how <c>{{globals.NAME}}</c> resolves (lookup is by the globally unique Name).
    /// </summary>
    public Guid FolderId { get; set; } = GlobalVariableFolder.RootFolderId;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Username of the last editor (audit cross-reference).</summary>
    public string? UpdatedBy { get; set; }
}

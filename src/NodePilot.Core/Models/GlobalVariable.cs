namespace NodePilot.Core.Models;

/// <summary>
/// Admin-managed key/value pair readable from every workflow via the <c>{{globals.NAME}}</c>
/// template. It holds a shared pool of constants (connection strings, API endpoints,
/// environment tags) so those values are not hard-coded in individual workflow definitions.
///
/// <para>
/// When <see cref="IsSecret"/> is true, <see cref="Value"/> is stored DPAPI-encrypted (scope
/// from <c>Credentials:DpapiScope</c>) and <c>GET /api/global-variables</c> returns
/// <c>"***"</c> instead. Non-secret values are stored and returned as plaintext.
/// </para>
///
/// <para>
/// The engine resolves <c>{{globals.NAME}}</c> from the row, decrypting secrets at
/// step-execution time, and puts the value into the same <c>Variables</c> dictionary that
/// step-output templates use. <c>OutputRedactor</c> masks it again before Output/ErrorOutput
/// are persisted, so decrypted secrets do not reach the step log.
/// </para>
/// </summary>
public class GlobalVariable
{
    public Guid Id { get; set; }

    /// <summary>
    /// Case-sensitive identifier, by convention <c>SCREAMING_SNAKE_CASE</c>. The controller
    /// restricts it to <c>[A-Za-z0-9_\-]</c> so the name stays unambiguous inside the
    /// <c>{{globals.NAME}}</c> grammar.
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
    /// Folder membership for the UI. Every variable belongs to exactly one
    /// <see cref="GlobalVariableFolder"/>, defaulting to the singleton Root
    /// (<see cref="GlobalVariableFolder.RootFolderId"/>). The folder never affects how
    /// <c>{{globals.NAME}}</c> resolves; lookup is by the globally unique Name.
    /// </summary>
    public Guid FolderId { get; set; } = GlobalVariableFolder.RootFolderId;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Username of the last editor (audit cross-reference).</summary>
    public string? UpdatedBy { get; set; }
}

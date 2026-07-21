namespace NodePilot.Api.Services.DbAdmin;

/// <summary>
/// Configuration for the admin SQL query console. Bound from <c>DbAdmin:*</c>.
/// </summary>
/// <remarks>
/// Write-queries are a deliberate escalation: they bypass every guard the row-editor
/// applies (masked columns, last-admin protection, GlobalVariable.Value masking). The
/// default is read-only — operators that need write-mode have to flip the config flag
/// AND the UI has to send an explicit confirmation header per request.
/// </remarks>
public sealed class DbAdminOptions
{
    public const string SectionName = "DbAdmin";

    /// <summary>
    /// When <c>false</c> (default), the query endpoint rejects anything that isn't a
    /// read-only statement. Flipping this only enables the server-side write path; the
    /// UI still requires per-request confirmation.
    /// </summary>
    public bool AllowWriteQueries { get; set; }

    /// <summary>Per-statement timeout. Default 30s. Clamped to [1, 600].</summary>
    public int QueryTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Hard cap on rows returned in a single response. Default 10 000 — enough for ad-hoc
    /// analysis but small enough to keep <c>SELECT *</c> mistakes from blowing up memory.
    /// </summary>
    public int QueryMaxRows { get; set; } = 10_000;
}

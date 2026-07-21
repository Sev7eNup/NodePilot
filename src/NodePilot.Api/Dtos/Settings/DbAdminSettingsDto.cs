using System.ComponentModel.DataAnnotations;

namespace NodePilot.Api.Dtos.Settings;

/// <summary>
/// Settings UI-facing DTO for the admin SQL query console. Maps 1:1 to
/// <c>DbAdmin:*</c> in configuration and to <c>DbAdminOptions</c> at runtime.
///
/// <para>
/// <c>AllowWriteQueries</c> is a deliberate escalation: enabling it lets an Admin
/// mutate the database from the query console, bypassing every per-table guard
/// (masked columns, last-admin protection, GlobalVariable.Value masking). The UI
/// surface should ask for a typed confirmation before flipping it from false to
/// true, matching the friction the QueryPane itself enforces per-statement.
/// </para>
/// </summary>
public sealed class DbAdminSettingsDto
{
    public bool AllowWriteQueries { get; set; }

    [Range(1, 600)]
    public int QueryTimeoutSeconds { get; set; } = 30;

    [Range(1, 1_000_000)]
    public int QueryMaxRows { get; set; } = 10_000;
}

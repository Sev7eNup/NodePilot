using System.Text.RegularExpressions;

namespace NodePilot.Core.Activities;

/// <summary>
/// Single source of truth for the <c>custom:&lt;key&gt;</c> activity-type convention. It lives in
/// Core (DB-free) so the structural validator, the engine dispatch, the MCP analyzer and the
/// frontend mirror all use the same prefix and grammar. <see cref="ExecutorSentinel"/> is the fixed
/// <c>IActivityExecutor.ActivityType</c> of the single executor that serves every custom activity;
/// real nodes never use it bare.
/// </summary>
public static partial class CustomActivityType
{
    public const string Prefix = "custom:";

    /// <summary>The reserved <c>IActivityExecutor.ActivityType</c> sentinel of <c>CustomActivityExecutor</c>.</summary>
    public const string ExecutorSentinel = "custom";

    /// <summary>Allowed shape of a full type string: <c>custom:&lt;slug&gt;</c> with a 1..64 slug.</summary>
    [GeneratedRegex(@"^custom:[A-Za-z0-9_\-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex FullTypeRegex();

    /// <summary>Allowed shape of a bare key slug.</summary>
    [GeneratedRegex(@"^[A-Za-z0-9_\-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyRegex();

    public static bool IsCustomType(string? type) =>
        type is not null && type.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>True for a syntactically valid <c>custom:&lt;slug&gt;</c> (does not check existence).</summary>
    public static bool IsValidCustomType(string? type) =>
        type is not null && FullTypeRegex().IsMatch(type);

    public static bool IsValidKey(string? key) => key is not null && KeyRegex().IsMatch(key);

    public static string ForKey(string key) => Prefix + key;
}

namespace NodePilot.Cli;

/// <summary>
/// Semantic process exit codes. Scripts can branch on these:
///   0 = success, 1 = generic error, 2 = workflow run terminated non-Succeeded,
///   3 = auth required, 4 = permission denied (Admin-only or wrong role).
/// </summary>
public static class ExitCodes
{
    public const int Success = 0;
    public const int Error = 1;
    public const int RunFailed = 2;
    public const int AuthRequired = 3;
    public const int PermissionDenied = 4;
}

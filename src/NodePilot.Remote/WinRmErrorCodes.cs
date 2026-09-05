namespace NodePilot.Remote;

/// <summary>
/// Classifies a <c>PSRemotingTransportException.ErrorCode</c> as a denied logon, so the step
/// retry loop can stop repeating it. Kept as a pure predicate: a WSMan endpoint is not needed
/// to test it. The list starts minimal and is grown from field reports — WSMan also surfaces
/// auth failures under its own 0x803380xx codes, which are not covered here.
/// </summary>
internal static class WinRmErrorCodes
{
    private const int ErrorLogonFailure = 1326;
    private const int ErrorPasswordExpired = 1330;
    private const int ErrorPasswordMustChange = 1907;
    private const int ErrorAccountLockedOut = 1909;
    private const int SecELogonDenied = unchecked((int)0x8009030C);
    private const int SecENoCredentials = unchecked((int)0x8009030E);

    /// <summary>
    /// True when the code means the credential itself was rejected, so another attempt only
    /// produces another bad logon.
    /// </summary>
    /// <remarks>
    /// Deliberately absent: SEC_E_NO_AUTHENTICATING_AUTHORITY (0x80090311), ERROR_ACCESS_DENIED (5)
    /// and every connect/timeout code. A briefly unreachable DC and a WinRM shell-quota rejection
    /// (see <see cref="WinRmSessionPool"/>) are exactly the transient cases the retry exists for.
    /// </remarks>
    public static bool IsLogonDenied(int errorCode) => errorCode switch
    {
        ErrorLogonFailure => true,
        ErrorPasswordExpired => true,
        ErrorPasswordMustChange => true,
        ErrorAccountLockedOut => true,
        SecELogonDenied => true,
        SecENoCredentials => true,
        _ => false,
    };
}

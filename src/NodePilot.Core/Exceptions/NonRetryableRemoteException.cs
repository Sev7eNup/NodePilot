namespace NodePilot.Core.Exceptions;

/// <summary>
/// A remote failure that repeating cannot fix. The step retry loop never re-runs it —
/// repeated logons against a Windows target can trip an account-lockout policy.
/// </summary>
public sealed class NonRetryableRemoteException : Exception
{
    public NonRetryableRemoteException(string message, Exception? innerException = null)
        : base(message, innerException) { }
}

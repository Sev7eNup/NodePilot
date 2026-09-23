using System.Diagnostics;
using System.Management.Automation.Runspaces;
using System.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NodePilot.Core.Exceptions;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;

namespace NodePilot.Remote;

public class WinRmSessionFactory : IRemoteSessionFactory
{
    internal static readonly ActivitySource RemoteSource = new(NodePilot.Core.Telemetry.TelemetryConstants.Sources.Remote);

    private readonly ICredentialStore _credentialStore;
    private readonly IConfiguration? _configuration;
    private readonly ILogger<WinRmSessionFactory>? _logger;

    public WinRmSessionFactory(ICredentialStore credentialStore)
        : this(credentialStore, null, null) { }

    public WinRmSessionFactory(
        ICredentialStore credentialStore,
        IConfiguration? configuration,
        ILogger<WinRmSessionFactory>? logger)
    {
        _credentialStore = credentialStore;
        _configuration = configuration;
        _logger = logger;
    }

    // virtual: lets test subclasses replace the real WinRM connect logic without the pool having
    // to give up its cast assumption `(WinRmSession)session`. There is no production override —
    // anyone wanting to replace the WinRM path entirely registers NoOpSessionFactory instead.
    public virtual async Task<IRemoteSession> CreateSessionAsync(ManagedMachine machine, Credential? credential, CancellationToken ct)
    {
        // HTTP uses Negotiate: Kerberos in a domain, NTLM in a workgroup. HTTPS is opt-in per
        // machine; Remote:RequireWinRmSsl=true forbids HTTP for all machines.
        if (!machine.UseSsl
            && string.Equals(_configuration?["Remote:RequireWinRmSsl"], "true", StringComparison.OrdinalIgnoreCase))
            throw new NonRetryableRemoteException(
                $"WinRM over HTTP is blocked by configuration for machine '{machine.Name}' ({machine.Hostname}). " +
                "Enable SSL on the target (winrm quickconfig -transport:https) and set machine.UseSsl=true, " +
                "or set Remote:RequireWinRmSsl=false to allow WinRM over HTTP.");

        var authLabel = credential is null ? "negotiate_implicit" : "negotiate_explicit";
        using var activity = RemoteSource.StartActivity("winrm.connect", ActivityKind.Client);
        activity?.SetTag("nodepilot.remote.target", machine.Hostname);
        activity?.SetTag("nodepilot.remote.port", machine.WinRmPort);
        activity?.SetTag("nodepilot.remote.transport", "winrm");
        activity?.SetTag("nodepilot.remote.use_ssl", machine.UseSsl);
        activity?.SetTag("nodepilot.remote.auth", authLabel);

        var connectStopwatch = System.Diagnostics.Stopwatch.StartNew();
        var authTag = new KeyValuePair<string, object?>("auth", authLabel);

        var scheme = machine.UseSsl ? "https" : "http";
        var uri = new Uri($"{scheme}://{machine.Hostname}:{machine.WinRmPort}/wsman");
        const string shellUri = "http://schemas.microsoft.com/powershell/Microsoft.PowerShell";

        WSManConnectionInfo connInfo;

        if (credential is not null)
        {
            // Explicit credentials. DPAPI decrypt can fail for many reasons (scope mismatch
            // after re-imaging, wrong service-account identity) — the raw CryptographicException
            // message includes paths/stack frames that a Viewer-role user can read via the
            // Executions step API. Wrap + sanitize so the failure is visible to operators via
            // the server log but not via the per-step ErrorOutput channel.
            string password;
            try
            {
                // Actor = remote host we're connecting to, so a security review of the audit
                // log can see "which target did this decrypt was for" without joining tables.
                password = _credentialStore.DecryptPassword(
                    credential,
                    actor: $"winrm:{machine.Hostname}");
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex,
                    "DPAPI decrypt failed for credential {CredentialId} (name={Name}). " +
                    "Likely cause: DPAPI scope mismatch — re-enter the credential after a service-account or host change.",
                    credential.Id, credential.Name);
                throw new NonRetryableRemoteException(
                    $"Credential decrypt failed (id={credential.Id}). Check the server log for details.");
            }

            var securePassword = new SecureString();
            foreach (var c in password)
                securePassword.AppendChar(c);
            securePassword.MakeReadOnly();

            var username = string.IsNullOrEmpty(credential.Domain)
                ? credential.Username
                : $"{credential.Domain}\\{credential.Username}";

            connInfo = new WSManConnectionInfo(uri, shellUri,
                new System.Management.Automation.PSCredential(username, securePassword))
            {
                AuthenticationMechanism = AuthenticationMechanism.Negotiate,
            };
        }
        else
        {
            // No credential — use NodePilot process identity (integrated Windows auth)
            connInfo = new WSManConnectionInfo(uri, shellUri, credential: null)
            {
                AuthenticationMechanism = AuthenticationMechanism.NegotiateWithImplicitCredential,
            };
        }

        // WSMan timeouts. The defaults are intentionally generous — OperationTimeout is the
        // server-side heartbeat ceiling, NOT the user's script timeout (that one is enforced via
        // ps.Stop() in WinRmSession.ExecuteScriptAsync). Too low an OperationTimeout kills
        // long-running scripts that produce no output for a while (e.g. a long `Wait-Job`, big
        // file copies) on the WSMan server before our own timeout even gets a chance to fire —
        // the symptom is a confusing "the WS-Management service cannot complete the operation
        // within the time specified". 300s (5 min) covers typical long-running actions without
        // any special handling. Anyone with even longer scripts can raise this via config.
        var operationTimeoutSec = _configuration?.GetValue<int?>("Remote:WinRm:OperationTimeoutSeconds") ?? 300;
        var openTimeoutSec = _configuration?.GetValue<int?>("Remote:WinRm:OpenTimeoutSeconds") ?? 30;
        connInfo.OperationTimeout = operationTimeoutSec * 1000;
        connInfo.OpenTimeout = openTimeoutSec * 1000;

        var runspace = RunspaceFactory.CreateRunspace(connInfo);
        try
        {
            // runspace.Open() is synchronous and has no official cancellation API — wrapping it
            // in Task.Run only gives us an asynchronous facade; the worker thread stays blocked
            // until OpenTimeout fires (or it succeeds). The only lever that actually cuts off
            // WSMan when the caller's cancellation token fires is disposing the runspace mid
            // handshake. Best-effort: we register exactly that, catch whatever exception it
            // produces, and normalize it to an OperationCanceledException so caller code (the
            // pool, the activity) sees the same cancellation semantics as everywhere else.
            using var ctRegistration = ct.Register(() =>
            {
                try { runspace.Dispose(); } catch { /* race during Open is expected */ }
            });
            await Task.Run(() => runspace.Open(), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            connectStopwatch.Stop();
            activity?.SetStatus(ActivityStatusCode.Error, "cancelled");
            var cancelTag = new KeyValuePair<string, object?>("result", "cancelled");
            RemoteMetrics.SessionsOpened.Add(1, cancelTag, authTag);
            RemoteMetrics.SessionOpenDuration.Record(connectStopwatch.Elapsed.TotalMilliseconds, cancelTag, authTag);
            try { runspace.Dispose(); } catch { /* best-effort: runspace may already be torn down */ }
            throw;
        }
        catch (Exception ex) when (ct.IsCancellationRequested)
        {
            // A forced Dispose during Open() typically throws PSRemotingTransportException or
            // InvalidOperationException — attribute it back to the caller's cancellation.
            connectStopwatch.Stop();
            activity?.SetStatus(ActivityStatusCode.Error, "cancelled");
            var cancelTag = new KeyValuePair<string, object?>("result", "cancelled");
            RemoteMetrics.SessionsOpened.Add(1, cancelTag, authTag);
            RemoteMetrics.SessionOpenDuration.Record(connectStopwatch.Elapsed.TotalMilliseconds, cancelTag, authTag);
            try { runspace.Dispose(); } catch { /* best-effort: runspace may already be torn down */ }
            throw new OperationCanceledException("WinRM connect aborted by cancellation token", ex, ct);
        }
        catch (Exception ex)
        {
            connectStopwatch.Stop();
            // Spans leave the process unredacted, so they carry the failure class, never its text.
            activity?.SetStatus(ActivityStatusCode.Error, "connect_failed");
            activity?.SetTag("exception.type", ex.GetType().FullName);
            var failTag = new KeyValuePair<string, object?>("result", "fail");
            RemoteMetrics.SessionsOpened.Add(1, failTag, authTag);
            RemoteMetrics.SessionOpenDuration.Record(connectStopwatch.Elapsed.TotalMilliseconds, failTag, authTag);
            RemoteMetrics.AuthFailures.Add(1, authTag, new KeyValuePair<string, object?>("reason", ex.GetType().Name));
            runspace.Dispose();
            var nonRetryable = ClassifyConnectFailure(ex, machine);
            if (nonRetryable is not null) throw nonRetryable;
            throw;
        }

        connectStopwatch.Stop();
        activity?.SetStatus(ActivityStatusCode.Ok);
        var okTag = new KeyValuePair<string, object?>("result", "ok");
        RemoteMetrics.SessionsOpened.Add(1, okTag, authTag);
        RemoteMetrics.SessionOpenDuration.Record(connectStopwatch.Elapsed.TotalMilliseconds, okTag, authTag);
        RemoteMetrics.SessionsActive.Add(1);

        return new WinRmSession(runspace, machine.Hostname);
    }

    /// <summary>
    /// Returns a <see cref="NonRetryableRemoteException"/> for connect failures a retry cannot fix,
    /// or null to rethrow the original exception.
    /// </summary>
    internal static NonRetryableRemoteException? ClassifyConnectFailure(Exception ex, ManagedMachine machine)
    {
        if (ex is not System.Management.Automation.Remoting.PSRemotingTransportException transport)
            return null;
        // A rejected credential does not become valid on the next attempt, and repeating it
        // costs another bad logon against the domain.
        if (WinRmErrorCodes.IsLogonDenied(transport.ErrorCode))
            return new NonRetryableRemoteException(
                $"WinRM logon denied for '{machine.Name}' ({machine.Hostname}); not retried to avoid locking the account.",
                ex);
        return null;
    }
}

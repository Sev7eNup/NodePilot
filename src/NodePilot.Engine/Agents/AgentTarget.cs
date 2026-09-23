using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NodePilot.Core.Agents;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.Engine.Activities;
using NodePilot.Engine.PowerShell;

namespace NodePilot.Engine.Agents;

public sealed class AgentTargetFactory(NodePilotDbContext db, ICredentialStore credentials,
    IRemoteSessionFactory sessions, PowerShellEngineFactory engines, IOptionsMonitor<AgentOptions> options,
    ILogger<AgentTargetFactory> logger)
{
    public async Task<AgentTarget> CreateAsync(AgentDefinition definition, StepExecutionContext context, CancellationToken ct)
    {
        var machine = definition.TargetMachineId.HasValue
            ? await db.ManagedMachines.AsNoTracking().SingleOrDefaultAsync(m => m.Id == definition.TargetMachineId.Value, ct)
            : context.ResolvedMachine;
        if (definition.TargetMachineId.HasValue && machine is null)
            throw new ArgumentException("The configured agent target machine was not found.");
        if (machine is null && context.TargetMachineId.HasValue)
            machine = await db.ManagedMachines.AsNoTracking().SingleOrDefaultAsync(m => m.Id == context.TargetMachineId.Value, ct);
        if (machine is null && (definition.TargetMachineId.HasValue || context.TargetMachineId.HasValue))
            throw new ArgumentException("The configured agent target machine was not found.");
        machine ??= new ManagedMachine { Hostname = "localhost", Name = "NodePilot server" };
        var credentialId = definition.CredentialId ?? context.CredentialId ?? machine.DefaultCredentialId;
        Credential? credential = null;
        if (definition.UseServiceIdentity)
        {
            if (!options.CurrentValue.AllowServiceIdentity)
                throw new UnauthorizedAccessException("Agent execution using the service identity is disabled by the administrator.");
        }
        else
        {
            if (credentialId is null) throw new UnauthorizedAccessException("Select an agent credential or explicitly enable service identity.");
            credential = await credentials.GetAsync(credentialId.Value, ct)
                ?? throw new UnauthorizedAccessException("The configured agent credential is unavailable.");
        }
        return new AgentTarget(machine, credential, definition.UseServiceIdentity, sessions, engines, options, context.StepId, logger);
    }
}

public sealed class AgentTarget(ManagedMachine machine, Credential? credential, bool serviceIdentity,
    IRemoteSessionFactory sessions, PowerShellEngineFactory engines, IOptionsMonitor<AgentOptions> options,
    string stepId, ILogger logger) : IAsyncDisposable
{
    private IRemoteSession? _session;
    public string Hostname => machine.Hostname;
    public string WorkingRoot { get; private set; } = "";

    public async Task<string> ExecuteAsync(string script, CancellationToken ct, int timeoutSeconds = 60)
    {
        ct.ThrowIfCancellationRequested();
        if (!options.CurrentValue.Enabled || serviceIdentity && !options.CurrentValue.AllowServiceIdentity)
            throw new UnauthorizedAccessException("Agent execution is disabled by policy.");
        return await ExecuteCoreAsync(script, ct, timeoutSeconds);
    }

    private async Task<string> ExecuteCoreAsync(string script, CancellationToken ct, int timeoutSeconds)
    {
        if (serviceIdentity && BaseRemoteActivity.IsLoopbackHostname(machine.Hostname))
        {
            var result = await engines.GetEngine("auto", true).ExecuteAsync(new PowerShellExecutionRequest
            {
                ScriptText = script, Isolated = true, Timeout = TimeSpan.FromSeconds(timeoutSeconds), OutputCaptureAllowlist = []
            }, ct);
            if (!result.Success) throw new InvalidOperationException(result.Error);
            return PowerShellActivitySupport.ExtractMarkers(result.Output, stepId, logger).cleanOutput;
        }
        _session ??= await sessions.CreateSessionAsync(machine, credential, ct);
        var remote = await _session.ExecuteScriptAsync(script, timeoutSeconds, ct);
        if (!remote.Success) throw new InvalidOperationException(remote.ErrorOutput);
        return remote.Output;
    }

    public async Task<string> EnsureWorkingRootAsync(Guid runId, string memberId, CancellationToken ct)
    {
        if (WorkingRoot.Length > 0) return WorkingRoot;
        var name = $"NodePilot-Agent-{runId:N}-{memberId}";
        var candidate = (await ExecuteAsync("$ErrorActionPreference='Stop'; $p=[IO.Path]::Combine([IO.Path]::GetTempPath(), "
            + PowerShellOperation.Literal(name) + "); [void][IO.Directory]::CreateDirectory($p); Write-Output $p", ct)).Trim();
        if (!Path.IsPathFullyQualified(candidate) || !Path.GetFileName(candidate).Equals(name, StringComparison.Ordinal))
            throw new InvalidOperationException("Target returned an invalid agent staging directory.");
        WorkingRoot = candidate;
        return WorkingRoot;
    }

    public async ValueTask DisposeAsync()
    {
        if (WorkingRoot.Length > 0)
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                // Host-owned cleanup must still run after an administrator revokes agent execution.
                await ExecuteCoreAsync("$p=" + PowerShellOperation.Literal(WorkingRoot)
                    + "; if(Test-Path -LiteralPath $p){ Remove-Item -LiteralPath $p -Recurse -Force -ErrorAction Stop }", cleanup.Token, 10);
            }
            catch (Exception ex) { logger.LogWarning(ex, "Agent target staging cleanup failed on {Machine}", machine.Hostname); }
        }
        if (_session is not null) await _session.DisposeAsync();
    }
}

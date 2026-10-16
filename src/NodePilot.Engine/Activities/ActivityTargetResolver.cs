using System.Diagnostics.CodeAnalysis;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;

namespace NodePilot.Engine.Activities;

/// <summary>
/// Where a PowerShell-backed step runs: the target machine (null = none configured) and the
/// credential that applies to it.
/// </summary>
internal sealed class ActivityTarget(ManagedMachine? machine, Credential? credential)
{
    public ManagedMachine? Machine { get; } = machine;

    public Credential? Credential { get; } = credential;

    /// <summary>
    /// True when the step runs on the NodePilot host: no target machine, or a loopback machine
    /// without a credential. The localhost bypass is a product feature (operator trust), so this
    /// never demands a target. Computed once, because the loopback check may resolve DNS.
    /// </summary>
    [MemberNotNullWhen(false, nameof(Machine))]
    public bool RunsLocally { get; } = machine is null
        || (credential is null && BaseRemoteActivity.IsLoopbackHostname(machine.Hostname));
}

/// <summary>
/// The single target and credential resolution for every activity that runs over WinRM or
/// through the localhost bypass, so the activities cannot drift apart.
/// </summary>
internal static class ActivityTargetResolver
{
    public static async Task<ActivityTarget> ResolveAsync(
        StepExecutionContext context,
        NodePilot.Data.NodePilotDbContext? db,
        ICredentialStore? credentialStore,
        CancellationToken ct)
    {
        var machine = await ResolveMachineAsync(context, db, ct);
        return new ActivityTarget(machine, await ResolveCredentialAsync(context, machine, credentialStore, ct));
    }

    /// <summary>
    /// The machine the engine pre-resolved (registered or ad-hoc hostname), else the registered
    /// machine behind <see cref="StepExecutionContext.TargetMachineId"/>.
    /// </summary>
    public static async Task<ManagedMachine?> ResolveMachineAsync(
        StepExecutionContext context, NodePilot.Data.NodePilotDbContext? db, CancellationToken ct)
    {
        var machine = context.ResolvedMachine;
        if (machine is null && context.TargetMachineId is { } machineId && db is not null)
            machine = await db.ManagedMachines.FindAsync([machineId], ct);
        return machine;
    }

    /// <summary>
    /// The step's credential, else the machine's default credential. Without a machine there is
    /// nothing to authenticate against, so none is loaded. A configured credential is never
    /// silently dropped: without a credential store this throws.
    /// </summary>
    public static async Task<Credential?> ResolveCredentialAsync(
        StepExecutionContext context, ManagedMachine? machine, ICredentialStore? credentialStore, CancellationToken ct)
    {
        if (machine is null) return null;

        var credentialId = context.CredentialId ?? machine.DefaultCredentialId;
        if (credentialId is null) return null;

        if (credentialStore is null)
            throw new InvalidOperationException(
                "A credential is configured for this step, but no credential store is available to load it.");

        return await credentialStore.GetAsync(credentialId.Value, ct);
    }
}

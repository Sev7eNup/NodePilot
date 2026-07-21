using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NodePilot.Api.Audit;
using NodePilot.Core.Audit;
using NodePilot.Api.Dtos;
using NodePilot.Api.Telemetry;
using NodePilot.Core.Enums;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Core.WorkflowDefinitions;
using NodePilot.Data;

namespace NodePilot.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class MachinesController : ControllerBase
{
    private const int MaxMachineNameLength = 200;
    private const int MaxHostnameLength = 500;
    // Rolling window for the "X/Y steps OK last week" cell on the machines list.
    // Matches the implicit window on WorkflowsPage's success-rate column so the
    // two views show comparable signal periods.
    private const int RecentStatsWindowDays = 7;

    private readonly NodePilotDbContext _db;
    private readonly IRemoteSessionFactory _sessionFactory;
    private readonly ICredentialStore _credentialStore;
    private readonly IAuditWriter _audit;
    private readonly ILogger<MachinesController> _logger;

    // logger is optional so the slim direct-construction tests don't have to thread a logger
    // through every call site; DI always supplies the real one in production.
    public MachinesController(NodePilotDbContext db, IRemoteSessionFactory sessionFactory,
        ICredentialStore credentialStore, IAuditWriter audit, ILogger<MachinesController>? logger = null)
    {
        _db = db;
        _sessionFactory = sessionFactory;
        _credentialStore = credentialStore;
        _audit = audit;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<MachinesController>.Instance;
    }

    [HttpGet]
    public async Task<ActionResult<List<MachineResponse>>> GetAll(CancellationToken ct)
    {
        var machines = await _db.ManagedMachines
            .AsNoTracking()
            .OrderBy(m => m.Name)
            .ToListAsync(ct);

        var stats = await ComputeOperationalStatsAsync(ct);

        var responses = machines
            .Select(m => BuildMachineResponse(m, stats))
            .ToList();

        return Ok(responses);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<MachineResponse>> GetById(Guid id, CancellationToken ct)
    {
        var m = await _db.ManagedMachines.FindAsync([id], ct);
        if (m is null) return NotFound();

        var stats = await ComputeOperationalStatsAsync(ct);
        return Ok(BuildMachineResponse(m, stats));
    }

    /// <summary>
    /// Aggregated per-machine counts that back the operational columns on the
    /// machines list (workflow references, recent step throughput, live runs).
    /// Computed once per list request so we don't re-scan workflows + step rows
    /// 50 times for 50 machines.
    /// </summary>
    private sealed record OperationalStats(
        IReadOnlyDictionary<Guid, int> WorkflowReferences,
        IReadOnlyDictionary<Guid, (int Total, int Failed)> RecentStepStats,
        IReadOnlyDictionary<Guid, int> ActiveRuns);

    private async Task<OperationalStats> ComputeOperationalStatsAsync(CancellationToken ct)
    {
        // 1) Workflow references — parse every workflow definition once and count
        //    each distinct machine id per workflow. We deliberately count workflows,
        //    not nodes: one workflow with 5 runScript steps against the same host
        //    should appear as "used by 1 workflow", not 5.
        //
        //    A node's data.targetMachineId can technically be a template like
        //    "{{globals.targetHost}}" — in that case Guid.TryParse fails and we
        //    skip the reference. Dynamic targets aren't attributable to a specific
        //    machine without running the resolver, which we don't do here.
        var definitions = await _db.Workflows
            .AsNoTracking()
            .Select(w => new { w.Id, w.DefinitionJson })
            .ToListAsync(ct);

        var workflowRefs = new Dictionary<Guid, int>();
        foreach (var w in definitions)
        {
            HashSet<Guid> distinctTargets;
            try
            {
                using var doc = JsonDocument.Parse(w.DefinitionJson);
                var nodes = WorkflowDefinitionParser.ParseNodes(doc.RootElement);
                distinctTargets = nodes
                    .Where(n => !string.IsNullOrEmpty(n.Data.TargetMachineRaw))
                    .Select(n => Guid.TryParse(n.Data.TargetMachineRaw, out var g) ? g : Guid.Empty)
                    .Where(g => g != Guid.Empty)
                    .ToHashSet();
            }
            catch (JsonException)
            {
                continue;
            }
            foreach (var mid in distinctTargets)
                workflowRefs[mid] = workflowRefs.GetValueOrDefault(mid) + 1;
        }

        // 2) Recent step stats — last 7 days, grouped by resolved target string.
        //    StepExecution.TargetMachine stores the template-resolved string (set
        //    by StepRunner). For UI-authored workflows that's the machine Guid in
        //    canonical "D" format; for dynamically-resolved targets it's whatever
        //    the template produced, which we can't reliably attribute. We match
        //    only on parseable Guids to keep the join clean.
        var since = DateTime.UtcNow.AddDays(-RecentStatsWindowDays);
        var rawStepRows = await _db.StepExecutions
            .AsNoTracking()
            .Where(s => s.StartedAt >= since && s.TargetMachine != null)
            .GroupBy(s => s.TargetMachine!)
            .Select(g => new
            {
                Target = g.Key,
                Total = g.Count(),
                Failed = g.Count(s => s.Status == ExecutionStatus.Failed),
            })
            .ToListAsync(ct);

        var recentStepStats = new Dictionary<Guid, (int Total, int Failed)>();
        foreach (var row in rawStepRows)
            if (Guid.TryParse(row.Target, out var mid))
                recentStepStats[mid] = (row.Total, row.Failed);

        // 3) Active runs — step executions currently in Running state. Same
        //    Guid-only attribution as above.
        var rawActiveRows = await _db.StepExecutions
            .AsNoTracking()
            .Where(s => s.Status == ExecutionStatus.Running && s.TargetMachine != null)
            .GroupBy(s => s.TargetMachine!)
            .Select(g => new { Target = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var activeRuns = new Dictionary<Guid, int>();
        foreach (var row in rawActiveRows)
            if (Guid.TryParse(row.Target, out var mid))
                activeRuns[mid] = row.Count;

        return new OperationalStats(workflowRefs, recentStepStats, activeRuns);
    }

    private static MachineResponse BuildMachineResponse(ManagedMachine m, OperationalStats stats)
    {
        var (total, failed) = stats.RecentStepStats.TryGetValue(m.Id, out var s) ? s : (0, 0);
        return new MachineResponse(
            m.Id, m.Name, m.Hostname, m.WinRmPort, m.UseSsl,
            m.DefaultCredentialId, m.Tags, m.LastConnectivityCheck, m.IsReachable,
            UsedByWorkflowCount: stats.WorkflowReferences.GetValueOrDefault(m.Id),
            RecentStepCount: total,
            RecentFailedStepCount: failed,
            ActiveRunCount: stats.ActiveRuns.GetValueOrDefault(m.Id));
    }

    [HttpPost]
    [Authorize(Roles = "Admin,Operator")]
    public async Task<ActionResult<MachineResponse>> Create(CreateMachineRequest request, CancellationToken ct)
    {
        if (await ValidateMachineRequestAsync(request.Name, request.Hostname, request.WinRmPort, request.DefaultCredentialId, ct) is { } validationError)
            return validationError;

        var machine = new ManagedMachine
        {
            Id = Guid.NewGuid(),
            Name = request.Name.Trim(),
            Hostname = request.Hostname.Trim(),
            WinRmPort = request.WinRmPort,
            UseSsl = request.UseSsl,
            DefaultCredentialId = request.DefaultCredentialId,
            Tags = request.Tags?.Trim()
        };

        _db.ManagedMachines.Add(machine);
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(AuditActions.MachineCreated, "Machine", machine.Id,
            AuditDetails.Json(("name", machine.Name), ("hostname", machine.Hostname)), ct);

        // Newly created machine has no usage yet — all operational counters start
        // at zero. No need to round-trip the aggregations DB-side just to read 0s.
        var response = new MachineResponse(
            machine.Id, machine.Name, machine.Hostname, machine.WinRmPort, machine.UseSsl,
            machine.DefaultCredentialId, machine.Tags, machine.LastConnectivityCheck, machine.IsReachable,
            UsedByWorkflowCount: 0, RecentStepCount: 0, RecentFailedStepCount: 0, ActiveRunCount: 0);

        return CreatedAtAction(nameof(GetById), new { id = machine.Id }, response);
    }

    [HttpPut("{id:guid}")]
    [Authorize(Roles = "Admin,Operator")]
    public async Task<IActionResult> Update(Guid id, UpdateMachineRequest request, CancellationToken ct)
    {
        var machine = await _db.ManagedMachines.FindAsync([id], ct);
        if (machine is null) return NotFound();

        if (await ValidateMachineRequestAsync(request.Name, request.Hostname, request.WinRmPort, request.DefaultCredentialId, ct) is { } validationError)
            return validationError;

        machine.Name = request.Name.Trim();
        machine.Hostname = request.Hostname.Trim();
        machine.WinRmPort = request.WinRmPort;
        machine.UseSsl = request.UseSsl;
        machine.DefaultCredentialId = request.DefaultCredentialId;
        machine.Tags = request.Tags?.Trim();

        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(AuditActions.MachineUpdated, "Machine", machine.Id,
            AuditDetails.Json(("name", machine.Name), ("hostname", machine.Hostname)), ct);

        return NoContent();
    }

    private async Task<BadRequestObjectResult?> ValidateMachineRequestAsync(
        string? name,
        string? hostname,
        int winRmPort,
        Guid? defaultCredentialId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name))
            return ApiProblems.BadRequest(this, "MACHINE_NAME_REQUIRED", "name is required");
        if (name.Length > MaxMachineNameLength)
            return ApiProblems.BadRequest(this, "MACHINE_NAME_TOO_LONG", $"name must be at most {MaxMachineNameLength} characters");
        if (name.Any(char.IsControl))
            return ApiProblems.BadRequest(this, "MACHINE_NAME_CONTROL_CHARS", "name must not contain control characters");

        if (string.IsNullOrWhiteSpace(hostname))
            return ApiProblems.BadRequest(this, "MACHINE_HOSTNAME_REQUIRED", "hostname is required");
        var trimmedHost = hostname.Trim();
        if (trimmedHost.Length > MaxHostnameLength)
            return ApiProblems.BadRequest(this, "MACHINE_HOSTNAME_TOO_LONG", $"hostname must be at most {MaxHostnameLength} characters");
        if (trimmedHost.Any(char.IsControl) || trimmedHost.Contains('/') || trimmedHost.Contains('\\') || trimmedHost.Contains('@'))
            return ApiProblems.BadRequest(this, "MACHINE_HOSTNAME_INVALID_SURFACE", "hostname must be a host name or IP address, not a URL or path");
        if (Uri.CheckHostName(trimmedHost) == UriHostNameType.Unknown && !IPAddress.TryParse(trimmedHost, out _))
            return ApiProblems.BadRequest(this, "MACHINE_HOSTNAME_INVALID", "hostname must be a valid host name or IP address");

        if (winRmPort is < 1 or > 65535)
            return ApiProblems.BadRequest(this, "MACHINE_WINRM_PORT_INVALID", "winRmPort must be between 1 and 65535");

        if (defaultCredentialId is { } credentialId
            && !await _db.Credentials.AsNoTracking().AnyAsync(c => c.Id == credentialId, ct))
            return ApiProblems.BadRequest(this, "MACHINE_DEFAULT_CREDENTIAL_UNKNOWN", "defaultCredentialId does not exist");

        return null;
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var machine = await _db.ManagedMachines.FindAsync([id], ct);
        if (machine is null) return NotFound();

        var snapshotName = machine.Name;
        _db.ManagedMachines.Remove(machine);
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(AuditActions.MachineDeleted, "Machine", id,
            AuditDetails.Json(("name", snapshotName)), ct);

        return NoContent();
    }

    [HttpPost("{id:guid}/test")]
    [Authorize(Roles = "Admin,Operator")]
    public async Task<ActionResult<object>> TestConnection(Guid id, [FromBody] TestConnectionRequest? req, CancellationToken ct)
    {
        var machine = await _db.ManagedMachines.FindAsync([id], ct);
        if (machine is null) return NotFound();

        // Credential selection: an explicit override in the request body wins, otherwise fall
        // back to the machine's default credential. This lets the designer's "Validate" button
        // test a step-specific credential override without changing the machine's default config.
        var credentialId = req?.CredentialId ?? machine.DefaultCredentialId;
        if (credentialId is null)
            return ApiProblems.BadRequest(this, "MACHINE_CREDENTIAL_REQUIRED", "No credential — neither an override nor a default on the machine");

        var credential = await _credentialStore.GetAsync(credentialId.Value, ct);

        try
        {
            var sw = Stopwatch.StartNew();
            await using var session = await _sessionFactory.CreateSessionAsync(machine, credential, ct);
            var result = await session.ExecuteScriptAsync("$env:COMPUTERNAME", 15, ct);
            sw.Stop();

            // Explicitly typed key-value pair — plain `new(...)` is ambiguous here between the
            // `Counter<T>.Add(T, KVP)` and `Counter<T>.Add(T, params KVP[])` overloads.
            ApiMetrics.MachineTestConnections.Add(1,
                new KeyValuePair<string, object?>("result", result.Success ? "success" : "failure"));
            ApiMetrics.MachineTestDuration.Record(sw.Elapsed.TotalMilliseconds);

            // Only update the stored reachability state when the test ran with the machine's
            // default credential — otherwise a failed override test (e.g. testing a wrong
            // credential on purpose) would wrongly flip an otherwise-healthy host to
            // IsReachable=false.
            if (req?.CredentialId is null)
            {
                machine.IsReachable = result.Success;
                machine.LastConnectivityCheck = DateTime.UtcNow;
                await _db.SaveChangesAsync(ct);
            }

            // Audit: machine connectivity probes are dual-use — legit "is this host up" plus
            // potential WinRM-credential sweeps against a target list. Logging every probe
            // (success or failure) closes that forensics gap. Two distinct verbs because the
            // AuditWriter's SIEM mapping derives event.outcome from the suffix (_TEST_FAILED
            // → failure, _TESTED → success) — matches the established LOGIN_SUCCESS / LOGIN_FAILED
            // pattern and lets Sigma/Sentinel rules gate on event.outcome=failure without
            // having to parse the Details JSON for a `success` field.
            var action = result.Success
                ? AuditActions.MachineConnectionTested
                : AuditActions.MachineConnectionTestFailed;
            await _audit.LogAsync(action, "Machine", machine.Id,
                AuditDetails.Json(
                    ("machineName", machine.Name),
                    ("hostname", machine.Hostname),
                    ("credentialId", credentialId.ToString()),
                    ("credentialName", credential.Name),
                    ("credentialOverride", (req?.CredentialId is not null).ToString()),
                    ("success", result.Success.ToString()),
                    ("durationMs", sw.Elapsed.TotalMilliseconds.ToString("F0"))),
                ct);

            return Ok(new { success = result.Success, computerName = result.Output, error = result.ErrorOutput,
                credentialUsed = credential.Name });
        }
        catch (Exception ex)
        {
            ApiMetrics.MachineTestConnections.Add(1,
                new("result", "failure"),
                new("source", "exception"));

            if (req?.CredentialId is null)
            {
                machine.IsReachable = false;
                machine.LastConnectivityCheck = DateTime.UtcNow;
                await _db.SaveChangesAsync(ct);
            }

            // M-6 (security audit 2026-05-15): never echo the raw exception text back over the
            // HTTP response. WinRM / PSRemoting exceptions embed internal hostnames, SPN / Kerberos
            // realm details and provider internals. The full detail stays in the server log
            // (correlation id) and the admin-only audit trail; the caller gets the exception class
            // (enough to tell "auth failed" from "host unreachable") plus the correlation id.
            var correlationId = HttpContext?.TraceIdentifier ?? Activity.Current?.Id ?? machine.Id.ToString();
            _logger.LogWarning(ex,
                "Machine connection test failed for {Hostname} with credential {CredentialId} (correlationId={CorrelationId})",
                machine.Hostname, credentialId, correlationId);

            await _audit.LogAsync(AuditActions.MachineConnectionTestFailed, "Machine", machine.Id,
                AuditDetails.Json(
                    ("machineName", machine.Name),
                    ("hostname", machine.Hostname),
                    ("credentialId", credentialId.ToString()),
                    ("credentialName", credential.Name),
                    ("credentialOverride", (req?.CredentialId is not null).ToString()),
                    ("success", "False"),
                    ("correlationId", correlationId),
                    ("error", ex.Message)),
                ct);

            return Ok(new
            {
                success = false,
                error = $"Connection test failed ({ex.GetType().Name}). Reference {correlationId} in the server log / audit trail for details.",
                correlationId,
                credentialUsed = credential.Name,
            });
        }
    }
}

using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NodePilot.Api.Audit;
using NodePilot.Api.Dtos;
using NodePilot.Core.Agents;
using NodePilot.Core.Audit;
using NodePilot.Core.Interfaces;
using NodePilot.Data;
using NodePilot.Engine.Agents;

namespace NodePilot.Api.Controllers;

[ApiController, Route("api/agents"), Authorize(Roles = "Admin,Operator")]
public sealed class AgentToolsController(NodePilotDbContext db, ISecretProtector protector,
    AgentMcpClientFactory clients, IAuditWriter audit) : ControllerBase
{
    [HttpGet("mcp-servers")]
    public async Task<ActionResult<IReadOnlyList<AgentMcpServerResponse>>> GetMcpServers(CancellationToken ct)
        => Ok((await db.AgentMcpServers.AsNoTracking().OrderBy(x => x.Name).ToListAsync(ct))
            .Where(x => x.Enabled || User.IsInRole("Admin")).Select(ToResponse).ToList());

    [HttpGet("mcp-servers/{id:guid}/tools")]
    public async Task<ActionResult<IReadOnlyList<AgentMcpToolResponse>>> GetMcpTools(Guid id, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await using var client = await clients.ConnectAsync(id, timeout.Token);
            var tools = await client.ListToolsAsync(cancellationToken: timeout.Token);
            return Ok(tools.Take(200).Select(t => new AgentMcpToolResponse(t.Name, t.Description, t.JsonSchema,
                AgentExternalReadPolicy.IsReadOnly(t), AgentExternalReadPolicy.ContractFingerprint(t))).ToList());
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        { return BadRequest(new { message = "MCP discovery failed. Check the registered server, connection policy and credentials." }); }
    }

    [HttpPut("mcp-servers/{id:guid}"), Authorize(Roles = "Admin")]
    public async Task<ActionResult<AgentMcpServerResponse>> SaveMcpServer(Guid id, SaveAgentMcpServerRequest request, CancellationToken ct)
    {
        if (request.Transport == "stdio" && (string.IsNullOrWhiteSpace(request.Command) || !Path.IsPathFullyQualified(request.Command)))
            return BadRequest(new { message = "MCP stdio requires an absolute executable path." });
        if (request.Transport == "streamableHttp" && (!Uri.TryCreate(request.Endpoint, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0))
            return BadRequest(new { message = "MCP HTTP requires an HTTP(S) endpoint without embedded credentials." });
        if (request.Arguments.Any(x => x is null || x.Length > 4096) || request.Secrets?.Count > 32
            || request.Secrets?.Any(x => string.IsNullOrWhiteSpace(x.Key) || x.Key.Length > 128 || x.Value is null || x.Value.Length > 8192) == true)
            return BadRequest(new { message = "MCP arguments or secret values exceed their limits." });
        var row = await db.AgentMcpServers.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (row is not null && request.UpdatedAt != row.UpdatedAt) return Conflict(new { message = "MCP server was changed. Reload before saving." });
        if (row?.ProtectedSecrets is not null && request.Secrets is null
            && (row.Endpoint != request.Endpoint || row.Command != request.Command || row.Transport != request.Transport))
            return BadRequest(new { message = "Supply credentials again, or clear them, when changing the MCP destination." });
        if (row is null) { row = new AgentMcpServer { Id = id }; db.AgentMcpServers.Add(row); }
        row.Name = request.Name.Trim(); row.Enabled = request.Enabled; row.Transport = request.Transport;
        row.Command = request.Command; row.ArgumentsJson = JsonSerializer.Serialize(request.Arguments);
        row.Endpoint = request.Endpoint;
        // PostgreSQL stores microseconds; return the same concurrency value that is persisted.
        var now = DateTime.UtcNow;
        row.UpdatedAt = new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerMicrosecond, DateTimeKind.Utc);
        if (request.Secrets is not null)
        {
            row.ProtectedSecrets = request.Secrets.Count == 0 ? null : protector.Protect(JsonSerializer.Serialize(request.Secrets));
            row.SecretProvider = row.ProtectedSecrets is null ? null : protector.ProviderName;
        }
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { return Conflict(new { message = "MCP server was changed. Reload before saving." }); }
        await audit.LogAsync(AuditActions.AgentMcpServerSaved, "AgentMcpServer", id, AuditDetails.Json(("name", row.Name)), ct);
        return Ok(ToResponse(row));
    }

    [HttpDelete("mcp-servers/{id:guid}"), Authorize(Roles = "Admin")]
    public async Task<IActionResult> DeleteMcpServer(Guid id, CancellationToken ct)
    {
        var row = await db.AgentMcpServers.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (row is null) return NotFound();
        db.AgentMcpServers.Remove(row); await db.SaveChangesAsync(ct);
        await audit.LogAsync(AuditActions.AgentMcpServerDeleted, "AgentMcpServer", id, ct: ct);
        return NoContent();
    }

    [HttpGet("skills")]
    public async Task<ActionResult<IReadOnlyList<AgentSkillResponse>>> GetSkills(CancellationToken ct)
    {
        var admin = User.IsInRole("Admin");
        return Ok(await db.AgentSkillPackages.AsNoTracking().Where(x => x.Enabled || admin).OrderBy(x => x.Name).ThenBy(x => x.Version)
            .Select(x => new AgentSkillResponse(x.Id, x.Name, x.Version, x.Description, x.Sha256, x.Enabled, x.CreatedAt)).ToListAsync(ct));
    }

    [HttpPost("skills"), Authorize(Roles = "Admin"), RequestSizeLimit(14_000_000)]
    public async Task<ActionResult<AgentSkillResponse>> ImportSkill(ImportAgentSkillRequest request, CancellationToken ct)
    {
        AgentSkillArchive.ValidatedSkill skill;
        try { skill = AgentSkillArchive.Read(request.Package); }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or IOException)
        { return BadRequest(new { message = ex.Message }); }
        if (await db.AgentSkillPackages.AnyAsync(x => x.Name == skill.Name && x.Version == request.Version, ct))
            return Conflict(new { message = "That skill version already exists. Import a new version." });
        var row = new AgentSkillPackage
        {
            Id = Guid.NewGuid(), Name = skill.Name, Version = request.Version, Description = skill.Description,
            Package = request.Package, Sha256 = skill.Sha256, Enabled = true
        };
        db.AgentSkillPackages.Add(row); await db.SaveChangesAsync(ct);
        await audit.LogAsync(AuditActions.AgentSkillImported, "AgentSkill", row.Id, AuditDetails.Json(("name", row.Name), ("version", row.Version)), ct);
        return Ok(ToResponse(row));
    }

    [HttpPut("skills/{id:guid}/enabled"), Authorize(Roles = "Admin")]
    public async Task<IActionResult> SetSkillEnabled(Guid id, SetAgentSkillEnabledRequest request, CancellationToken ct)
    {
        var row = await db.AgentSkillPackages.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (row is null) return NotFound();
        row.Enabled = request.Enabled; await db.SaveChangesAsync(ct);
        await audit.LogAsync(AuditActions.AgentSkillUpdated, "AgentSkill", id, AuditDetails.Json(("enabled", row.Enabled)), ct);
        return NoContent();
    }

    [HttpDelete("skills/{id:guid}"), Authorize(Roles = "Admin")]
    public async Task<IActionResult> DeleteSkill(Guid id, CancellationToken ct)
    {
        var row = await db.AgentSkillPackages.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (row is null) return NotFound();
        db.AgentSkillPackages.Remove(row); await db.SaveChangesAsync(ct);
        await audit.LogAsync(AuditActions.AgentSkillDeleted, "AgentSkill", id, ct: ct);
        return NoContent();
    }

    private static AgentMcpServerResponse ToResponse(AgentMcpServer x) => new(x.Id, x.Name, x.Enabled, x.Transport,
        x.Command, JsonSerializer.Deserialize<string[]>(x.ArgumentsJson) ?? [], x.Endpoint, x.ProtectedSecrets is not null, x.UpdatedAt);
    private static AgentSkillResponse ToResponse(AgentSkillPackage x) => new(x.Id, x.Name, x.Version, x.Description, x.Sha256, x.Enabled, x.CreatedAt);
}

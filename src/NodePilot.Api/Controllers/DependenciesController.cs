using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NodePilot.Api.Dtos;
using NodePilot.Api.Services;
using NodePilot.Core.Dependencies;
using NodePilot.Core.Interfaces;
using NodePilot.Data;

namespace NodePilot.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/dependencies")]
public sealed class DependenciesController(NodePilotDbContext db, IResourceAuthorizationService authz,
    WorkflowDefinitionFactsCache cache) : ControllerBase
{
    private static readonly HashSet<string> Kinds = ["workflow", "machine", "credential", "variable", "customActivity"];
    private static readonly HashSet<string> States = ["problems", "unused"];

    [HttpGet("objects")]
    public async Task<ActionResult<DependencyObjectsDto>> Objects(CancellationToken ct,
        [FromQuery] string? q = null, [FromQuery] string? kind = null, [FromQuery] string? state = null,
        [FromQuery] string sort = "name", [FromQuery] string? cursor = null, [FromQuery] int limit = 50)
    {
        if (kind is not null && !Kinds.Contains(kind) || state is not null && !States.Contains(state)
            || sort is not ("name" or "usage") || q?.Length > 200) return BadRequest();
        var graph = await DependencySnapshotReader.ReadAsync(db, authz, cache, User, ct);
        var users = graph.DirectUsers();
        var problems = graph.ProblemCounts();
        // Workflows start from triggers, so having no caller is normal and not listed as unused.
        var items = graph.Resources.Where(r => (kind is null || r.Kind == kind)
            && (string.IsNullOrWhiteSpace(q) || r.Name.Contains(q.Trim(), StringComparison.OrdinalIgnoreCase)
                || r.Context.Contains(q.Trim(), StringComparison.OrdinalIgnoreCase) || r.Id.ToString().Contains(q.Trim(), StringComparison.OrdinalIgnoreCase))
            && state switch
            {
                "problems" => problems.ContainsKey(r.Key),
                "unused" => r.Kind != "workflow" && !users.ContainsKey(r.Key),
                _ => true,
            })
            .OrderByDescending(r => sort == "usage" ? users.GetValueOrDefault(r.Key) : 0)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Kind).ThenBy(r => r.Id).ToArray();
        var revision = Revision(graph);
        var stamp = Hash($"{revision}|objects|{kind}|{state}|{sort}|{q}");
        if (!Offset(cursor, stamp, out var offset)) return StaleCursor();
        limit = Math.Clamp(limit, 1, 100);
        // Indirect usage needs a traversal per object, so it is computed for the returned page only.
        return Ok(new DependencyObjectsDto(items.Skip(offset).Take(limit).Select(r => new DependencyObjectSummaryDto(Object(r),
                users.GetValueOrDefault(r.Key), graph.Analyze(r.Key, true).Matches.Count(m => !m.Possible && m.Distance > 1),
                problems.GetValueOrDefault(r.Key))).ToArray(), revision,
            Next(stamp, offset, limit, items.Length)));
    }

    [HttpGet("{kind}/{id:guid}")]
    public async Task<ActionResult<DependencyReportDto>> Report(string kind, Guid id, CancellationToken ct,
        [FromQuery] string direction = "usedBy", [FromQuery] string? cursor = null, [FromQuery] int limit = 50)
    {
        if (!Kinds.Contains(kind) || direction is not ("uses" or "usedBy")) return BadRequest();
        var graph = await DependencySnapshotReader.ReadAsync(db, authz, cache, User, ct);
        var root = graph.Resources.FirstOrDefault(r => r.Kind == kind && r.Id == id);
        if (root is null) return NotFound();
        var analysis = graph.Analyze(root.Key, direction == "usedBy");
        var resources = graph.Resources.ToDictionary(r => r.Key);
        var sorted = analysis.Matches.OrderBy(m => m.Possible).ThenBy(m => m.Distance > 1)
            .ThenBy(m => resources[m.Key].Name, StringComparer.OrdinalIgnoreCase).ThenBy(m => m.Key.Kind).ThenBy(m => m.Key.Id).ToArray();
        var revision = Revision(graph);
        var stamp = Hash($"{revision}|{kind}|{id}|{direction}");
        if (!Offset(cursor, stamp, out var offset)) return StaleCursor();
        limit = Math.Clamp(limit, 1, 100);
        var limitations = graph.Limitations.GetValueOrDefault(root.Key, []).Concat(analysis.Limitations)
            .Concat(analysis.Matches.SelectMany(m => graph.Limitations.GetValueOrDefault(m.Key, []))).Distinct().Order().ToArray();
        return Ok(new DependencyReportDto(Object(root), direction, DateTime.UtcNow, revision, analysis.Complete,
            limitations, new(sorted.Count(m => !m.Possible && m.Distance == 1), sorted.Count(m => !m.Possible && m.Distance > 1), sorted.Count(m => m.Possible)),
            sorted.Skip(offset).Take(limit).Select(m => new DependencyItemDto(Object(resources[m.Key]), m.Distance, m.Possible,
                m.Paths.Select(p => Path(p, resources)).ToArray())).ToArray(),
            graph.Problems.Where(p => p.Source == root.Key).Select(p => new DependencyProblemDto(p.Kind, p.Status, Location(p.Location))).ToArray(),
            Next(stamp, offset, limit, sorted.Length)));
    }

    [HttpGet("{kind}/{id:guid}/evidence")]
    public async Task<ActionResult<DependencyEvidenceDto>> Evidence(string kind, Guid id, CancellationToken ct,
        [FromQuery] string relatedKind, [FromQuery] Guid relatedId, [FromQuery] string direction = "usedBy",
        [FromQuery] string? cursor = null, [FromQuery] int limit = 50)
    {
        if (!Kinds.Contains(kind) || !Kinds.Contains(relatedKind) || direction is not ("uses" or "usedBy")) return BadRequest();
        var graph = await DependencySnapshotReader.ReadAsync(db, authz, cache, User, ct);
        var resources = graph.Resources.ToDictionary(r => r.Key);
        var key = new DependencyKey(kind, id);
        if (!resources.ContainsKey(key)) return NotFound();
        var match = graph.Analyze(key, direction == "usedBy").Matches.FirstOrDefault(m => m.Key == new DependencyKey(relatedKind, relatedId));
        if (match is null) return NotFound();
        var items = match.Paths.SelectMany(p => p.Links).SelectMany(l => l.Locations).Select(Location).Distinct()
            .OrderBy(l => l.SourceKind).ThenBy(l => l.SourceId).ThenBy(l => l.NodeId).ThenBy(l => l.Field).ToArray();
        var revision = Revision(graph);
        var stamp = Hash($"{revision}|{kind}|{id}|{direction}|{relatedKind}|{relatedId}");
        if (!Offset(cursor, stamp, out var offset)) return StaleCursor();
        limit = Math.Clamp(limit, 1, 100);
        return Ok(new DependencyEvidenceDto(revision, match.Paths.Select(p => Path(p, resources)).ToArray(),
            items.Skip(offset).Take(limit).ToArray(), items.Length, Next(stamp, offset, limit, items.Length)));
    }

    private ConflictObjectResult StaleCursor() => Conflict(new { code = "DEPENDENCIES_CHANGED" });
    private static DependencyObjectDto Object(DependencyResource r) => new(r.Kind, r.Id, r.Name, r.Context, r.Enabled, Hash(r.Revision));
    private static DependencyLocationDto Location(DependencyLocation l) => new(l.Source.Kind, l.Source.Id, l.NodeId,
        l.Label, l.Field, l.Disabled, Hash(l.Revision), Guid.TryParse(l.Via, out var id) ? id : null);
    private static DependencyPathDto Path(DependencyPath p, IReadOnlyDictionary<DependencyKey, DependencyResource> resources)
        => new(p.Links.Select(l => new DependencyLegDto(Object(resources[l.Source]), Object(resources[l.Target]), l.Relation,
            l.Locations.Count, l.Locations.Take(3).Select(Location).ToArray())).ToArray());
    private static string Revision(DependencyGraph graph) => Hash(JsonSerializer.Serialize(new
    {
        resources = graph.Resources.OrderBy(r => r.Kind).ThenBy(r => r.Id),
        graph.Links, graph.Problems,
    }));
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..24];
    private static bool Offset(string? cursor, string stamp, out int offset)
    {
        offset = 0;
        if (cursor is null) return true;
        var parts = cursor.Split('.');
        return cursor.Length <= 64 && parts.Length == 2 && parts[0] == stamp
            && int.TryParse(parts[1], out offset) && offset >= 0 && offset <= 1_000_000;
    }
    private static string? Next(string stamp, int offset, int limit, int total) => offset + limit < total ? $"{stamp}.{offset + limit}" : null;
}

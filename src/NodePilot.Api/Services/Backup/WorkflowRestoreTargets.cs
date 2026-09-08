using NodePilot.Core.Models;

namespace NodePilot.Api.Services.Backup;

/// <summary>Matches each backup workflow to at most one original target, reserving exact ids.</summary>
internal sealed class WorkflowRestoreTargets(IEnumerable<Guid> sourceIds)
{
    private readonly HashSet<Guid> _sourceIds = sourceIds.ToHashSet();
    private readonly Dictionary<Guid, Workflow> _byId = [];
    private readonly Dictionary<(Guid FolderId, string Name), List<Workflow>> _byName = [];

    public void Add(Workflow workflow)
    {
        _byId.Add(workflow.Id, workflow);
        var key = (workflow.FolderId, workflow.Name);
        if (!_byName.TryGetValue(key, out var rows)) _byName[key] = rows = [];
        rows.Add(workflow);
    }

    public Workflow? Match(Guid sourceId, Guid folderId, string name, bool consume = true)
    {
        var target = _byId.GetValueOrDefault(sourceId);
        if (target is null && _byName.TryGetValue((folderId, name), out var rows))
            target = rows.FirstOrDefault(row => !consume || !_sourceIds.Contains(row.Id));
        if (target is not null && consume)
        {
            _byId.Remove(target.Id);
            _byName[(target.FolderId, target.Name)].Remove(target);
        }
        return target;
    }
}

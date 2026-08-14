using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Engine.PowerShell;

namespace NodePilot.Engine.Tests.Helpers;

/// <summary>
/// <see cref="IPowerShellExecutionEngine"/> double that reports a fixed engine type and
/// availability and always succeeds. Used by the engine-selection tests, which care about
/// which engine the factory picks, never about what it executes.
/// </summary>
public sealed class FakeEngine(string engineType, bool available) : IPowerShellExecutionEngine
{
    public string EngineType => engineType;
    public bool IsAvailable => available;
    public Task<PowerShellExecutionResult> ExecuteAsync(PowerShellExecutionRequest request, CancellationToken ct)
        => Task.FromResult(new PowerShellExecutionResult { Success = true });
}

/// <summary>
/// In-memory <see cref="IGlobalVariableStore"/>. Constructed with the name/value pairs a test
/// wants resolvable (none = an empty store); <see cref="SetSecret"/> marks a name secret so
/// redaction paths can be exercised. Every mutating member throws — these tests only read.
/// </summary>
public sealed class StubGlobalVariableStore : IGlobalVariableStore
{
    private readonly Dictionary<string, string> _values;
    private readonly HashSet<string> _secretNames = new(StringComparer.Ordinal);

    public StubGlobalVariableStore(params (string Key, string Value)[] values)
        => _values = values.ToDictionary(p => p.Key, p => p.Value);

    public void SetSecret(string name, bool isSecret)
    {
        if (isSecret) _secretNames.Add(name); else _secretNames.Remove(name);
    }

    public Task<IReadOnlyDictionary<string, string>> GetAllResolvedAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyDictionary<string, string>>(_values);

    public Task<GlobalVariableResolutionResult> GetAllResolvedDetailedAsync(CancellationToken ct)
        => Task.FromResult(new GlobalVariableResolutionResult(_values, new HashSet<string>()));

    public Task<IReadOnlyList<GlobalVariable>> GetAllAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<GlobalVariable>>(
            _values.Select(kv => new GlobalVariable
            {
                Id = Guid.NewGuid(),
                Name = kv.Key,
                Value = kv.Value,
                IsSecret = _secretNames.Contains(kv.Key),
            }).ToList());

    public Task<string?> GetValueAsync(string name, CancellationToken ct)
        => Task.FromResult<string?>(_values.TryGetValue(name, out var v) ? v : null);

    public Task<GlobalVariable> CreateAsync(string name, string value, bool isSecret, string? description, Guid folderId, string? updatedBy, CancellationToken ct)
        => throw new NotSupportedException();

    public Task UpdateAsync(Guid id, string name, string? value, bool isSecret, string? description, Guid? folderId, string? updatedBy, CancellationToken ct)
        => throw new NotSupportedException();

    public Task MoveToFolderAsync(Guid id, Guid folderId, string? updatedBy, CancellationToken ct)
        => throw new NotSupportedException();

    public Task DeleteAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();

    public Task<ReencryptionSummary> ReencryptAllSecretsAsync(CancellationToken ct)
        => Task.FromResult(new ReencryptionSummary(0, 0, Array.Empty<ReencryptionSkip>()));
}

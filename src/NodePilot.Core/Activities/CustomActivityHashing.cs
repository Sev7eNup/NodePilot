using System.Security.Cryptography;
using System.Text;
using NodePilot.Core.Models;

namespace NodePilot.Core.Activities;

/// <summary>
/// Computes the stable hash recorded in a step's <see cref="CustomActivityProvenance"/>. Covers the
/// script template plus every option that changes execution behaviour, so two executions with the
/// same hash provably ran the same code+config — independent of the version counter.
/// </summary>
public static class CustomActivityHashing
{
    public static string Compute(CustomActivityDefinition def)
    {
        var sb = new StringBuilder()
            .Append(def.ScriptTemplate).Append('␟')
            .Append(def.Engine).Append('␟')
            .Append(def.RunsRemote ? '1' : '0').Append('␟')
            .Append(def.Isolated ? '1' : '0').Append('␟')
            .Append(def.MemoryLimitMb?.ToString() ?? "").Append('␟')
            .Append(def.MaxProcesses?.ToString() ?? "").Append('␟')
            .Append(def.DefaultTimeoutSeconds?.ToString() ?? "").Append('␟')
            .Append(def.SuccessExitCodes ?? "").Append('␟')
            .Append(def.InputParametersJson).Append('␟')
            .Append(def.OutputParametersJson);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexStringLower(bytes);
    }

    public static CustomActivityProvenance ProvenanceOf(CustomActivityDefinition def) =>
        new(def.Key, def.Version, Compute(def));
}

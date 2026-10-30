using System.Text;

namespace NodePilot.Engine.Debug;

public static class DebugOverrides
{
    public static string? Validate(IReadOnlyDictionary<string, string>? overrides)
    {
        if (overrides is null) return null;
        if (overrides.Count > 256) return "Debug override: at most 256 entries are allowed.";
        foreach (var (key, value) in overrides)
        {
            if (string.IsNullOrWhiteSpace(key)) return "Debug override: empty keys are not allowed.";
            if (key.StartsWith("globals.", StringComparison.OrdinalIgnoreCase))
                return "Debug override: global variables are read-only.";
            if (key.StartsWith("__", StringComparison.Ordinal))
                return "Debug override: reserved engine keys cannot be overridden.";
            if (value is null || Encoding.UTF8.GetByteCount(value) > 64 * 1024)
                return "Debug override: values must be non-null and at most 65536 UTF-8 bytes.";
        }
        return null;
    }
}

using System.Text.Json;

namespace NodePilot.Engine;

internal static class JsonSerializerDefaults
{
    internal static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
}

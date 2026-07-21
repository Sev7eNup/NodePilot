using Xunit;

namespace NodePilot.Engine.Tests.Helpers;

/// <summary>
/// xUnit Fact that is reported as <b>Skipped</b> (not silently passed) on non-Windows hosts.
/// Process isolation relies on Windows Job Objects, so these tests can only run on Windows — but
/// an early <c>return</c> would make them masquerade as covered on a mis-targeted CI leg. Setting
/// <see cref="FactAttribute.Skip"/> in the constructor makes the skip visible in the test count.
/// </summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = "Windows-only: process isolation requires Windows Job Objects.";
    }
}

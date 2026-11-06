using System.Management.Automation.Runspaces;
using FluentAssertions;
using NodePilot.Remote;
using Xunit;

namespace NodePilot.Engine.Tests.Remote;

/// <summary>
/// Objects from a target arrive deserialized, as property bags whose own ToString is empty. The
/// session must render them the way PowerShell does instead of as blank lines.
/// </summary>
public class WinRmSessionOutputTests
{
    [Fact]
    public async Task ExecuteScriptAsync_CustomAndDeserializedObjects_RenderTheirText()
    {
        using var runspace = RunspaceFactory.CreateRunspace();
        runspace.Open();
        await using var session = new WinRmSession(runspace, "test-local");

        var result = await session.ExecuteScriptAsync(
            "[pscustomobject]@{ a = 1 }\n" +
            "[System.Management.Automation.PSSerializer]::Deserialize([System.Management.Automation.PSSerializer]::Serialize((Get-Item C:\\Windows)))\n" +
            "'plain'",
            timeoutSeconds: 30, ct: TestContext.Current.CancellationToken);

        result.Success.Should().BeTrue(result.ErrorOutput);
        result.Output.Split('\n').Select(line => line.Trim()).Should().Equal("@{a=1}", "C:\\Windows", "plain");
    }
}

using System.Text.Json;
using FluentAssertions;
using NodePilot.Core.WorkflowDefinitions;
using Xunit;

namespace NodePilot.Engine.Tests.Examples;

/// <summary>
/// The tech-demo workflows under scripts/tech-demo are created through the API by seed.ps1, so
/// they have to pass the same checks as any saved workflow.
/// </summary>
public class TechDemoWorkflowTests
{
    [Theory]
    [InlineData("main.json")]
    [InlineData("child.json")]
    [InlineData("planar.json")]
    [InlineData("xl.json")]
    public async Task Definition_IsAccepted_AndStartsProgramsByAbsolutePath(string file)
    {
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(RepositoryRoot(), "scripts", "tech-demo", file),
            TestContext.Current.CancellationToken));

        var validation = WorkflowDefinitionStructuralValidator.Validate(document.RootElement);
        validation.IsValid.Should().BeTrue("{0} must be accepted on save: {1}", file, validation.Error);

        // startProgram rejects a relative path at run time; the structural check does not see it.
        var programs = document.RootElement.GetProperty("nodes").EnumerateArray()
            .Select(node => node.GetProperty("data"))
            .Where(data => data.GetProperty("activityType").GetString() == "startProgram")
            .Select(data => data.GetProperty("config").GetProperty("filePath").GetString());
        programs.Should().OnlyContain(path => Path.IsPathFullyQualified(path!));
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "NodePilot.slnx")))
                return directory.FullName;
        throw new DirectoryNotFoundException("NodePilot repository root was not found");
    }
}

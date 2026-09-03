using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace NodePilot.Api.Tests.Architecture;

/// <summary>
/// Pins the browser-side upload gates on the workflows page to the <c>RequestSizeLimit</c> of the
/// endpoints they post to.
///
/// <para>The frontend checks a file's size before reading it, so a huge file cannot exhaust tab
/// memory. That check is a mirror, and a mirror that falls behind the server silently shrinks the
/// product: the UI refuses a file the API would have accepted, and the message names a limit that
/// exists nowhere else. Raising a server ceiling without the mirror is exactly the drift this
/// catches.</para>
/// </summary>
public class ImportSizeLimitFrontendSyncTests
{
    [Theory]
    [InlineData("import", "MAX_IMPORT_BYTES")]
    [InlineData("import-scorch", "MAX_SCORCH_BYTES")]
    public void FrontendUploadGate_MatchesEndpointRequestSizeLimit(string route, string constant)
    {
        var serverMib = ReadRequestSizeLimitMib(route);
        var frontendMib = ReadFrontendGateMib(constant);

        frontendMib.Should().Be(serverMib,
            $"the browser gate {constant} must match the {route} endpoint's RequestSizeLimit — " +
            "a smaller value rejects uploads the server accepts, a larger one lets the tab read a " +
            "file the server will refuse with 413");
    }

    /// <summary>Reads the MiB factor from the <c>[RequestSizeLimit(N * 1024 * 1024)]</c> attribute
    /// directly above the action that handles <paramref name="route"/>.</summary>
    private static int ReadRequestSizeLimitMib(string route)
    {
        var file = Path.Combine(FindRepoRoot(), "src", "NodePilot.Api", "Controllers",
            "WorkflowImportExportController.cs");
        var src = File.ReadAllText(file);

        var match = Regex.Match(
            src,
            $@"\[HttpPost\(""{Regex.Escape(route)}""\)\](?:.|\n)*?\[RequestSizeLimit\((\d+) \* 1024 \* 1024\)\]",
            RegexOptions.None,
            TimeSpan.FromSeconds(5));

        match.Success.Should().BeTrue($"POST {route} must declare a RequestSizeLimit in {file}");
        return int.Parse(match.Groups[1].Value);
    }

    private static int ReadFrontendGateMib(string constant)
    {
        var file = Path.Combine(FindRepoRoot(), "src", "nodepilot-ui", "src", "pages", "WorkflowsPage.tsx");
        var src = File.ReadAllText(file);

        var match = Regex.Match(
            src,
            $@"const {Regex.Escape(constant)} = (\d+) \* 1024 \* 1024;",
            RegexOptions.None,
            TimeSpan.FromSeconds(5));

        match.Success.Should().BeTrue($"{constant} must be declared in {file}");
        return int.Parse(match.Groups[1].Value);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 10 && dir is not null; i++, dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "NodePilot.slnx")))
                return dir.FullName;
        throw new InvalidOperationException($"Could not locate NodePilot.slnx walking up from {AppContext.BaseDirectory}");
    }
}

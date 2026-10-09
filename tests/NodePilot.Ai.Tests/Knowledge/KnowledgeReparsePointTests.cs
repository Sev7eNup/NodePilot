using System.Diagnostics;
using FluentAssertions;
using NodePilot.Ai.Knowledge;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Ai.Tests.Knowledge;

public sealed class KnowledgeReparsePointTests : IDisposable
{
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "np-knowledge-links-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _directoryLinks = [];

    public KnowledgeReparsePointTests() => Directory.CreateDirectory(_workspace);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FileLink_CannotExposeAnOutsideSecretAsMarkdown(bool source)
    {
        var root = Directory.CreateDirectory(Path.Combine(_workspace, "corpus")).FullName;
        var secret = Path.Combine(_workspace, "appsettings.json");
        File.WriteAllText(secret, "outside-secret-marker");
        File.CreateSymbolicLink(Path.Combine(root, "guide.md"), secret);

        Read(root, "guide.md", source).Ok.Should().BeFalse();
        Search(root, "outside-secret-marker", source).Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DirectoryJunction_CannotExposeOutsideDocuments(bool source)
    {
        var root = Directory.CreateDirectory(Path.Combine(_workspace, "corpus")).FullName;
        var outside = Directory.CreateDirectory(Path.Combine(_workspace, "private")).FullName;
        File.WriteAllText(Path.Combine(outside, "private.md"), "outside-secret-marker");
        Junction(Path.Combine(root, "linked"), outside);

        Read(root, "linked/private.md", source).Ok.Should().BeFalse();
        Search(root, "outside-secret-marker", source).Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Search_CyclicJunctionAmongIneligibleFiles_Terminates(bool source)
    {
        var root = Directory.CreateDirectory(Path.Combine(_workspace, "corpus")).FullName;
        File.WriteAllText(Path.Combine(root, "ignored.txt"), "needlemarker");
        Junction(Path.Combine(root, "cycle"), root);

        var results = await Task.Run(() => Search(root, "needlemarker", source), TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        results.Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConfiguredRootJunction_WithTrailingSeparator_RemainsUsable(bool source)
    {
        var actual = Directory.CreateDirectory(Path.Combine(_workspace, "actual")).FullName;
        File.WriteAllText(Path.Combine(actual, "guide.md"), "allowed-marker");
        var root = Path.Combine(_workspace, "selected-root");
        Junction(root, actual);
        root += Path.DirectorySeparatorChar;

        Read(root, "guide.md", source).Content.Should().Be("allowed-marker");
        Search(root, "allowed-marker", source).Should().ContainSingle(hit => hit.Path == "guide.md");
    }

    private static StaticOptionsMonitor<AiKnowledgeOptions> Options(string root) => new(new AiKnowledgeOptions
    {
        DocsRootPath = root,
        SourceCodeRootPath = root,
    });

    private static KnowledgeFileResult Read(string root, string path, bool source) => source
        ? new SourceCodeKnowledgeReader(Options(root)).Read(path)
        : new DocsKnowledgeReader(Options(root)).Read(path);

    private static IReadOnlyList<KnowledgeSearchHit> Search(string root, string query, bool source) => source
        ? new SourceCodeKnowledgeReader(Options(root)).Search(query)
        : new DocsKnowledgeReader(Options(root)).Search(query);

    private void Junction(string link, string target)
    {
        var start = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in new[] { "/c", "mklink", "/J", link, target }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Cannot create test junction.");
        process.WaitForExit(5_000).Should().BeTrue();
        process.ExitCode.Should().Be(0, process.StandardError.ReadToEnd());
        _directoryLinks.Add(link);
        File.GetAttributes(link).Should().HaveFlag(FileAttributes.ReparsePoint);
    }

    public void Dispose()
    {
        foreach (var link in _directoryLinks) Directory.Delete(link);
        Directory.Delete(_workspace, recursive: true);
    }
}

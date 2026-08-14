using System.Text.Json;
using FluentAssertions;
using NodePilot.Core.Interfaces;
using NodePilot.Engine.Activities;
using NodePilot.Engine.Tests.Helpers;
using Xunit;

namespace NodePilot.Engine.Tests.Activities;

public class XmlQueryActivityTests
{
    private readonly XmlQueryActivity _activity = new();

    private static JsonElement Cfg(object obj) =>
        JsonDocument.Parse(JsonSerializer.Serialize(obj)).RootElement;

    private static StepExecutionContext Ctx() =>
        new() { WorkflowExecutionId = Guid.NewGuid(), StepId = "xml-1" };

    private const string BooksXml =
        "<books><book><title>A</title></book><book><title>B</title></book></books>";

    [Fact]
    public async Task ExecuteAsync_InlineXml_SingleMatch_ReturnsFirstMatchText()
    {
        var cfg = Cfg(new { source = "inline", content = BooksXml, xpath = "//title", resultMode = "single" });
        var result = await _activity.ExecuteAsync(Ctx(), cfg, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Output.Should().Be("A");
        result.OutputParameters["result"].Should().Be("A");
        result.OutputParameters["count"].Should().Be("1");
    }

    [Fact]
    public async Task ExecuteAsync_InlineXml_AllMode_ReturnsJsonArrayOfMatches()
    {
        var cfg = Cfg(new { source = "inline", content = BooksXml, xpath = "//title", resultMode = "all" });
        var result = await _activity.ExecuteAsync(Ctx(), cfg, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Output.Should().Contain("\"A\"").And.Contain("\"B\"");
        result.OutputParameters["count"].Should().Be("2");
    }

    [Fact]
    public async Task ExecuteAsync_NoMatchSingleMode_SucceedsWithEmptyOutput()
    {
        var cfg = Cfg(new { source = "inline", content = BooksXml, xpath = "//nonexistent", resultMode = "single" });
        var result = await _activity.ExecuteAsync(Ctx(), cfg, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Output.Should().Be("");
        result.OutputParameters["count"].Should().Be("0");
    }

    [Fact]
    public async Task ExecuteAsync_InvalidXml_ReturnsFailure()
    {
        var cfg = Cfg(new { source = "inline", content = "<not-closed>", xpath = "//anything" });
        var result = await _activity.ExecuteAsync(Ctx(), cfg, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorOutput.Should().Contain("XmlQuery error");
    }

    [Fact]
    public async Task ExecuteAsync_InvalidXpath_ReturnsFailure()
    {
        var cfg = Cfg(new { source = "inline", content = BooksXml, xpath = "///[bad" });
        var result = await _activity.ExecuteAsync(Ctx(), cfg, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorOutput.Should().Contain("XmlQuery error");
    }

    [Fact]
    public async Task ExecuteAsync_MissingXpath_ReturnsFailure()
    {
        var cfg = Cfg(new { source = "inline", content = BooksXml });
        var result = await _activity.ExecuteAsync(Ctx(), cfg, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorOutput.Should().Contain("xpath");
    }

    [Fact]
    public async Task ExecuteAsync_FileSource_NonexistentPath_ReturnsFailure()
    {
        var cfg = Cfg(new { source = "file", path = "C:\\definitely\\not-here.xml", xpath = "//x" });
        var result = await _activity.ExecuteAsync(Ctx(), cfg, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorOutput.Should().Contain("not found");
    }

    [Fact]
    public async Task ExecuteAsync_FileSource_ReadsFileAndQueries()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tempFile, BooksXml);
            var cfg = Cfg(new { source = "file", path = tempFile, xpath = "//title", resultMode = "all" });
            var result = await _activity.ExecuteAsync(Ctx(), cfg, CancellationToken.None);

            result.Success.Should().BeTrue();
            result.OutputParameters["count"].Should().Be("2");
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [WindowsFact]
    public async Task ExecuteAsync_FileSourceWithoutInjectedConfigRejectsDirectorySymlink()
    {
        var stage = Path.Combine(Path.GetTempPath(), "nodepilot-xml-link-" + Guid.NewGuid().ToString("N"));
        var outside = Path.Combine(stage, "outside");
        var link = Path.Combine(stage, "link");
        Directory.CreateDirectory(outside);
        await File.WriteAllTextAsync(Path.Combine(outside, "payload.xml"), "<root />");
        try
        {
            try { Directory.CreateSymbolicLink(link, outside); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                return;
            }

            var result = await new XmlQueryActivity().ExecuteAsync(
                Ctx(),
                Cfg(new { source = "file", path = Path.Combine(link, "payload.xml"), xpath = "/root" }),
                CancellationToken.None);

            result.Success.Should().BeFalse();
            result.ErrorOutput.Should().Contain("reparse point");
        }
        finally
        {
            DeleteDirectoryLink(link);
            try { Directory.Delete(stage, recursive: true); } catch { }
        }
    }

    private static void DeleteDirectoryLink(string link)
    {
        try
        {
            if ((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0)
                Directory.Delete(link);
        }
        catch { }
    }

    [Fact]
    public async Task ExecuteAsync_FileSource_RejectsOversizedFileBeforeRead()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            await using (var stream = File.Create(tempFile))
                stream.SetLength((8 * 1024 * 1024) + 1);

            var cfg = Cfg(new { source = "file", path = tempFile, xpath = "//title" });
            var result = await _activity.ExecuteAsync(Ctx(), cfg, CancellationToken.None);

            result.Success.Should().BeFalse();
            result.ErrorOutput.Should().Contain("exceeds");
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task ExecuteAsync_WithNamespaces_ResolvesPrefixedXpath()
    {
        const string nsXml = """
            <root xmlns:bk="http://example.com/books">
              <bk:book><bk:title>Hello</bk:title></bk:book>
            </root>
            """;
        var cfg = Cfg(new
        {
            source = "inline",
            content = nsXml,
            xpath = "//bk:title",
            resultMode = "single",
            namespaces = new Dictionary<string, string> { ["bk"] = "http://example.com/books" },
        });

        var result = await _activity.ExecuteAsync(Ctx(), cfg, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Output.Should().Be("Hello");
    }
}

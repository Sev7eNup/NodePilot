using FluentAssertions;
using NodePilot.Switcher.Services;
using Xunit;

namespace NodePilot.Switcher.Tests;

public sealed class AllowListReaderTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"switcher-{Guid.NewGuid():N}");

    public AllowListReaderTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private async Task<IReadOnlyList<string>> ReadAsync(string content)
    {
        var path = Path.Combine(_directory, "allowlist.txt");
        await File.WriteAllTextAsync(path, content, TestContext.Current.CancellationToken);
        return await new AllowListReader().ReadAsync(path, CancellationToken.None);
    }

    [Fact]
    public async Task ReadAsync_AcceptsCommaSemicolonAndNewlinesAndRemovesDuplicates()
    {
        var path = Path.Combine(_directory, "allowlist.txt");
        await File.WriteAllTextAsync(path, "Alpha, Beta;Gamma\r\nalpha\nDelta", TestContext.Current.CancellationToken);

        var result = await new AllowListReader().ReadAsync(path, CancellationToken.None);

        result.Should().Equal("Alpha", "Beta", "Gamma", "Delta");
    }

    [Theory]
    [InlineData("\"Backup Job\"", "Backup Job")]
    [InlineData("'Backup Job'", "Backup Job")]
    [InlineData("Bob's Job", "Bob's Job")]
    [InlineData("  'Bob''s Job'  ", "Bob's Job")]
    [InlineData("\"say \"\"hi\"\"\"", "say \"hi\"")]
    [InlineData("'a, b; c'", "a, b; c")]
    public async Task ReadAsync_HandlesQuotedAndApostropheNames(string content, string expected)
    {
        var result = await ReadAsync(content);

        result.Should().Equal(expected);
    }

    [Fact]
    public async Task ReadAsync_MixesQuotedAndPlainEntriesAcrossSeparators()
    {
        var result = await ReadAsync("Alpha, 'Be,ta' ;\r\n\"Gamma\"\r\nBob's Job");

        result.Should().Equal("Alpha", "Be,ta", "Gamma", "Bob's Job");
    }

    [Theory]
    [InlineData("'open", "*line 1*closing quote*")]
    [InlineData("Alpha\n'a\nb'", "*line 2*span lines*")]
    [InlineData("'a' b", "*unexpected text*")]
    [InlineData("''", "*empty*")]
    public async Task ReadAsync_RejectsMalformedQuotes(string content, string message)
    {
        var action = () => ReadAsync(content);

        await action.Should().ThrowAsync<InvalidOperationException>().WithMessage(message);
    }

    [Fact]
    public async Task ReadAsync_RejectsEmptyList()
    {
        var path = Path.Combine(_directory, "allowlist.txt");
        await File.WriteAllTextAsync(path, " , ; \r\n", TestContext.Current.CancellationToken);

        var action = () => new AllowListReader().ReadAsync(path, CancellationToken.None);

        await action.Should().ThrowAsync<InvalidOperationException>().WithMessage("*empty*");
    }
}

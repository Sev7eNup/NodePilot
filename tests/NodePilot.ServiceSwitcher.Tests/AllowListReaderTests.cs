using FluentAssertions;
using NodePilot.ServiceSwitcher.Services;
using Xunit;

namespace NodePilot.ServiceSwitcher.Tests;

public sealed class AllowListReaderTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"switcher-{Guid.NewGuid():N}");

    public AllowListReaderTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public async Task ReadAsync_AcceptsCommaSemicolonAndNewlinesAndRemovesDuplicates()
    {
        var path = Path.Combine(_directory, "allowlist.txt");
        await File.WriteAllTextAsync(path, "Alpha, Beta;Gamma\r\nalpha\nDelta", TestContext.Current.CancellationToken);

        var result = await new AllowListReader().ReadAsync(path, CancellationToken.None);

        result.Should().Equal("Alpha", "Beta", "Gamma", "Delta");
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

using FluentAssertions;
using NodePilot.Api.Logging;
using Serilog.Formatting.Compact;
using Xunit;

namespace NodePilot.Api.Tests.Logging;

/// <summary>
/// Format-string dispatch for Serilog's structured sinks. The mapping is the
/// only thing the operator-facing <c>Logging:Format</c> setting depends on,
/// so wrong cases here would silently drop us back to plain text in production.
/// </summary>
public class LogFormattersTests
{
    [Theory]
    [InlineData("text")]
    [InlineData("TEXT")]
    [InlineData("plain")]
    [InlineData("")]
    [InlineData(null)]
    public void Create_TextOrUnknown_ReturnsNull(string? format)
    {
        // null-formatter is the contract — the caller falls back to the plain
        // outputTemplate path, so anything not recognised collapses to "text".
        LogFormatters.Create(format).Should().BeNull();
    }

    [Theory]
    [InlineData("cmtrace")]
    [InlineData("CmTrace")]
    [InlineData("CMTRACE")]
    public void Create_CmTrace_ReturnsCmTraceFormatter(string format)
    {
        var formatter = LogFormatters.Create(format);

        formatter.Should().NotBeNull();
        formatter.Should().BeOfType<CmTraceFormatter>();
    }

    [Theory]
    [InlineData("json")]
    [InlineData("JSON")]
    [InlineData("Json")]
    public void Create_Json_ReturnsCompactJsonFormatter(string format)
    {
        var formatter = LogFormatters.Create(format);

        formatter.Should().NotBeNull();
        formatter.Should().BeOfType<CompactJsonFormatter>();
    }
}

using System.Text;
using FluentAssertions;
using NodePilot.Ai;
using Xunit;

// CA2022: the wrapper is always fed a MemoryStream in these tests, which returns the full
// requested count in a single Read/ReadAsync — asserting n == payload length is exact here.
#pragma warning disable CA2022

namespace NodePilot.Ai.Tests;

/// <summary>
/// Direct unit tests for the internal <see cref="LengthLimitedStream"/> — a response-body-size
/// cap wrapper (security-audit finding L-4) that protects <c>JsonDocument.ParseAsync</c> from
/// gigabyte-sized upstream responses. Covers the Read/ReadAsync paths, the byte-cap throw, and
/// the NotSupported members without exercising the full HTTP path through
/// <see cref="OpenAiCompatibleLlmClient"/>. Reachable here via
/// <c>InternalsVisibleTo("NodePilot.Ai.Tests")</c>.
/// </summary>
public sealed class LengthLimitedStreamTests
{
    private static LengthLimitedStream Wrap(byte[] data, long maxBytes) =>
        new LengthLimitedStream(new MemoryStream(data), maxBytes);

    [Fact]
    public void CanRead_ReflectsInner_CanSeekAndCanWrite_AreFalse()
    {
        using var s = Wrap(new byte[] { 1, 2, 3 }, 100);

        s.CanRead.Should().BeTrue();   // inner MemoryStream is readable
        s.CanSeek.Should().BeFalse();  // wrapper is forward-only
        s.CanWrite.Should().BeFalse(); // read-only wrapper
    }

    [Fact]
    public void Length_Getter_Throws_NotSupported()
    {
        using var s = Wrap(new byte[] { 1 }, 100);

        s.Invoking(x => _ = x.Length).Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void Position_Get_TracksBytesRead_Set_Throws()
    {
        using var s = Wrap(new byte[] { 1, 2, 3, 4, 5 }, 100);
        s.Position.Should().Be(0);

        var read = s.Read(new byte[3], 0, 3);
        read.Should().Be(3);
        s.Position.Should().Be(3); // Position == cumulative bytes read

        s.Invoking(x => x.Position = 0).Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void Read_WithinLimit_ReturnsData_AndAdvancesPosition()
    {
        var payload = Encoding.UTF8.GetBytes("hello");
        using var s = Wrap(payload, 100);

        var buffer = new byte[payload.Length];
        var n = s.Read(buffer, 0, buffer.Length);

        n.Should().Be(payload.Length);
        buffer.Should().Equal(payload);
        s.Position.Should().Be(payload.Length);
    }

    [Fact]
    public void Read_ExactlyAtLimit_DoesNotThrow()
    {
        var payload = new byte[8];
        using var s = Wrap(payload, 8); // cap == payload size → boundary is inclusive

        var n = s.Read(new byte[8], 0, 8);

        n.Should().Be(8);
        s.Position.Should().Be(8);
    }

    [Fact]
    public void Read_PastLimit_Throws_WithBodyLimitMessage()
    {
        var payload = new byte[10];
        using var s = Wrap(payload, 5);

        s.Invoking(x => x.Read(new byte[10], 0, 10))
         .Should().Throw<InvalidOperationException>()
         .WithMessage("*Body-Limit*");
    }

    [Fact]
    public void Read_AccumulatesAcrossCalls_ThrowsWhenCumulativeExceedsLimit()
    {
        var payload = new byte[10];
        using var s = Wrap(payload, 8);

        var first = s.Read(new byte[5], 0, 5); // cumulative 5 — ok
        first.Should().Be(5);
        s.Position.Should().Be(5);

        // Second read pushes cumulative to 10 > 8 → trips the cap.
        s.Invoking(x => x.Read(new byte[5], 0, 5))
         .Should().Throw<InvalidOperationException>()
         .WithMessage("*Body-Limit*");
    }

    [Fact]
    public async Task ReadAsync_Memory_WithinLimit_ReturnsData()
    {
        var payload = Encoding.UTF8.GetBytes("abcdefghij"); // 10 bytes
        using var s = Wrap(payload, 100);

        var buffer = new byte[payload.Length];
        var n = await s.ReadAsync(buffer.AsMemory());

        n.Should().Be(payload.Length);
        buffer.Should().Equal(payload);
        s.Position.Should().Be(payload.Length);
    }

    [Fact]
    public async Task ReadAsync_Memory_PastLimit_Throws()
    {
        var payload = new byte[10];
        using var s = Wrap(payload, 4);

        Func<Task> act = async () => await s.ReadAsync(new byte[10].AsMemory());

        await act.Should().ThrowAsync<InvalidOperationException>()
                 .WithMessage("*Body-Limit*");
    }

    [Fact]
    public async Task ReadAsync_ByteArrayOverload_WithinLimit_ReturnsData()
    {
        var payload = new byte[6];
        using var s = Wrap(payload, 100);

        var n = await s.ReadAsync(new byte[6], 0, 6, CancellationToken.None);

        n.Should().Be(6);
        s.Position.Should().Be(6);
    }

    [Fact]
    public async Task ReadAsync_ByteArrayOverload_PastLimit_Throws()
    {
        var payload = new byte[6];
        using var s = Wrap(payload, 3);

        Func<Task> act = async () => await s.ReadAsync(new byte[6], 0, 6, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
                 .WithMessage("*Body-Limit*");
    }

    [Fact]
    public void WriteSideMembers_AllThrow_NotSupported()
    {
        using var s = Wrap(new byte[] { 1 }, 100);

        s.Invoking(x => x.Flush()).Should().Throw<NotSupportedException>();
        s.Invoking(x => x.Seek(0, SeekOrigin.Begin)).Should().Throw<NotSupportedException>();
        s.Invoking(x => x.SetLength(10)).Should().Throw<NotSupportedException>();
        s.Invoking(x => x.Write(new byte[] { 1 }, 0, 1)).Should().Throw<NotSupportedException>();
    }
}

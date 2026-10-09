using System.Text;
using FluentAssertions;
using NodePilot.Engine.PowerShell;
using Xunit;

namespace NodePilot.Engine.Tests.PowerShell;

public class ProcessExecutionEngineDrainTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DrainReads_OutputBufferedBeforeHeldEof_PreservesAlreadyReadText(bool errorStream)
    {
        var expected = errorStream ? "error detail without newline"
            : PowerShellScriptWrapper.StartMarker + "\n" + PowerShellScriptWrapper.ErrorMarker + "\n" + new string('x', 100_000);
        using var stream = new HeldOpenStream(Encoding.UTF8.GetBytes(expected));
        using var reader = new StreamReader(stream, Encoding.UTF8);
        using var emptyReader = new StringReader("");
        var captured = new ProcessExecutionEngine.OutputCapture(reader, TestContext.Current.CancellationToken);
        var empty = new ProcessExecutionEngine.OutputCapture(emptyReader, TestContext.Current.CancellationToken);
        await stream.WaitingForEof.Task.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            var result = await ProcessExecutionEngine.DrainReadsAsync(errorStream ? empty : captured,
                errorStream ? captured : empty, TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);

            result.DrainTimedOut.Should().BeTrue();
            (errorStream ? result.Stderr : result.Stdout).Should().Be(expected,
                "all delivered chunks must survive without EOF or a final newline");
        }
        finally
        {
            stream.AllowEof.TrySetResult();
            await captured.Completion;
        }
    }

    [Fact]
    public async Task DrainReads_BothReadsReachEof_ReturnsOutputsWithNoTimeout()
    {
        using var stdoutReader = new StringReader("out");
        using var stderrReader = new StringReader("err");
        var (stdout, stderr, timedOut) = await ProcessExecutionEngine.DrainReadsAsync(
            new(stdoutReader, TestContext.Current.CancellationToken), new(stderrReader, TestContext.Current.CancellationToken),
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        timedOut.Should().BeFalse();
        stdout.Should().Be("out");
        stderr.Should().Be("err");
    }

    [Fact]
    public async Task DrainReads_AbandonedReadLaterFaults_PreservesItsSnapshot()
    {
        using var stream = new HeldOpenStream("captured"u8.ToArray());
        using var reader = new StreamReader(stream);
        using var emptyReader = new StringReader("");
        var stdout = new ProcessExecutionEngine.OutputCapture(reader, TestContext.Current.CancellationToken);
        var stderr = new ProcessExecutionEngine.OutputCapture(emptyReader, TestContext.Current.CancellationToken);
        await stream.WaitingForEof.Task.WaitAsync(TestContext.Current.CancellationToken);
        var result = await ProcessExecutionEngine.DrainReadsAsync(stdout, stderr,
            TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);

        result.Stdout.Should().Be("captured");
        result.DrainTimedOut.Should().BeTrue();
        stream.AllowEof.SetException(new ObjectDisposedException("reader"));
        await FluentActions.Awaiting(() => stdout.Completion).Should().ThrowAsync<ObjectDisposedException>();
        stdout.Text.Should().Be("captured");
    }

    [Fact]
    public async Task DrainReads_FaultedReaderBesideHeldEof_PropagatesReadFailure()
    {
        using var stdoutStream = new HeldOpenStream("started"u8.ToArray());
        using var stderrStream = new HeldOpenStream([]);
        using var stdoutReader = new StreamReader(stdoutStream);
        using var stderrReader = new StreamReader(stderrStream);
        var stdout = new ProcessExecutionEngine.OutputCapture(stdoutReader, TestContext.Current.CancellationToken);
        var stderr = new ProcessExecutionEngine.OutputCapture(stderrReader, TestContext.Current.CancellationToken);
        await Task.WhenAll(stdoutStream.WaitingForEof.Task, stderrStream.WaitingForEof.Task);
        stderrStream.AllowEof.SetException(new IOException("pipe read failed"));
        await FluentActions.Awaiting(() => stderr.Completion).Should().ThrowAsync<IOException>();
        try
        {
            await FluentActions.Awaiting(() => ProcessExecutionEngine.DrainReadsAsync(stdout, stderr,
                TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken))
                .Should().ThrowAsync<IOException>().WithMessage("pipe read failed");
        }
        finally
        {
            stdoutStream.AllowEof.TrySetResult();
            await stdout.Completion;
        }
    }

    [Fact]
    public async Task DrainReads_CallerCancels_PropagatesOperationCanceled()
    {
        using var cancellation = new CancellationTokenSource();
        using var stream = new HeldOpenStream([]);
        using var reader = new StreamReader(stream);
        using var emptyReader = new StringReader("");
        var stdout = new ProcessExecutionEngine.OutputCapture(reader, cancellation.Token);
        var stderr = new ProcessExecutionEngine.OutputCapture(emptyReader, cancellation.Token);
        var drain = ProcessExecutionEngine.DrainReadsAsync(stdout, stderr, TimeSpan.FromSeconds(30), cancellation.Token);
        await stream.WaitingForEof.Task.WaitAsync(TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => drain);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stdout.Completion);
    }

    private sealed class HeldOpenStream(byte[] prefix) : Stream
    {
        private int _offset;
        public TaskCompletionSource WaitingForEof { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowEof { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_offset < prefix.Length)
            {
                var count = Math.Min(buffer.Length, prefix.Length - _offset);
                prefix.AsMemory(_offset, count).CopyTo(buffer);
                _offset += count;
                return count;
            }
            WaitingForEof.TrySetResult();
            await AllowEof.Task.WaitAsync(ct);
            return 0;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

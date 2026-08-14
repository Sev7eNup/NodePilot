using System.Net;
using System.Net.Sockets;
using System.Text;

namespace NodePilot.TestCommons;

/// <summary>
/// Minimal in-process SMTP server for tests that need to prove a mail was actually handed to a
/// server — the <c>emailNotification</c> activity, the admin-settings SMTP probe, alerting sinks.
/// Listens on an ephemeral loopback port, speaks just enough ESMTP to complete one delivery
/// (EHLO/HELO → MAIL FROM → RCPT TO → DATA → QUIT) and records what it saw in a
/// <see cref="SmtpSession"/>. Consolidates the private copies that previously lived in
/// Api.Tests and Engine.Tests.
/// </summary>
public sealed class FakeSmtpServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly TaskCompletionSource<SmtpSession> _sessionTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _cts = new();
    private Task? _acceptLoop;

    /// <summary>Ephemeral loopback port the server bound to.</summary>
    public int Port { get; }

    private FakeSmtpServer(TcpListener listener, int port)
    {
        _listener = listener;
        Port = port;
    }

    public static Task<FakeSmtpServer> StartAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = new FakeSmtpServer(listener, port);
        server._acceptLoop = Task.Run(() => server.AcceptAsync(server._cts.Token));
        return Task.FromResult(server);
    }

    /// <summary>Waits for the single recorded session, or throws once <paramref name="timeout"/> elapses.</summary>
    public async Task<SmtpSession> AwaitSessionAsync(TimeSpan timeout)
    {
        var completed = await Task.WhenAny(_sessionTcs.Task, Task.Delay(timeout));
        if (completed != _sessionTcs.Task)
            throw new TimeoutException("Fake SMTP server did not record a session in time.");
        return await _sessionTcs.Task;
    }

    private async Task AcceptAsync(CancellationToken ct)
    {
        try
        {
            using var client = await _listener.AcceptTcpClientAsync(ct);
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            await using var writer = new StreamWriter(stream, Encoding.ASCII) { AutoFlush = true, NewLine = "\r\n" };

            var session = new SmtpSession();
            var dataPayload = new StringBuilder();

            await writer.WriteLineAsync("220 fake.smtp.test ESMTP ready");

            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line is null) break;

                if (line.StartsWith("EHLO", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("HELO", StringComparison.OrdinalIgnoreCase))
                {
                    await writer.WriteLineAsync("250-fake.smtp.test");
                    await writer.WriteLineAsync("250-SIZE 10485760");
                    await writer.WriteLineAsync("250 OK");
                }
                else if (line.StartsWith("MAIL FROM", StringComparison.OrdinalIgnoreCase))
                {
                    session.MailFrom = line;
                    await writer.WriteLineAsync("250 OK");
                }
                else if (line.StartsWith("RCPT TO", StringComparison.OrdinalIgnoreCase))
                {
                    session.RcptTo = line;
                    await writer.WriteLineAsync("250 OK");
                }
                else if (line.Equals("DATA", StringComparison.OrdinalIgnoreCase))
                {
                    await writer.WriteLineAsync("354 End data with <CRLF>.<CRLF>");
                    while (true)
                    {
                        var dataLine = await reader.ReadLineAsync(ct);
                        if (dataLine is null || dataLine == ".") break;
                        dataPayload.AppendLine(dataLine);
                    }
                    session.DataReceived = true;
                    session.DataPayload = dataPayload.ToString();
                    await writer.WriteLineAsync("250 OK message accepted");
                }
                else if (line.StartsWith("QUIT", StringComparison.OrdinalIgnoreCase))
                {
                    await writer.WriteLineAsync("221 Bye");
                    break;
                }
                else
                {
                    await writer.WriteLineAsync("250 OK");
                }
            }

            _sessionTcs.TrySetResult(session);
        }
        catch (OperationCanceledException)
        {
            _sessionTcs.TrySetCanceled();
        }
        catch (Exception ex)
        {
            _sessionTcs.TrySetException(ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { /* already stopped */ }
        if (_acceptLoop is not null)
        {
            try { await _acceptLoop; } catch { /* shutdown noise */ }
        }
        _cts.Dispose();
    }
}

/// <summary>What <see cref="FakeSmtpServer"/> observed during the one session it accepts.</summary>
public sealed class SmtpSession
{
    public string MailFrom { get; set; } = "";
    public string RcptTo { get; set; } = "";
    public bool DataReceived { get; set; }
    public string DataPayload { get; set; } = "";
}

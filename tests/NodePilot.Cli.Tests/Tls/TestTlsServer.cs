using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace NodePilot.Cli.Tests.Tls;

/// <summary>
/// Minimal HTTPS listener on loopback. Enough to make a real handshake happen so the tests cover
/// the validation callback as it is actually reached through HttpClientHandler, not just the
/// decision function.
/// </summary>
internal sealed class TestTlsServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly X509Certificate2 _certificate;
    private readonly CancellationTokenSource _cts = new();

    public TestTlsServer(X509Certificate2 certificate)
    {
        _certificate = certificate;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        BaseAddress = new Uri($"https://localhost:{((IPEndPoint)_listener.LocalEndpoint).Port}/");
        _ = Task.Run(AcceptLoopAsync);
    }

    public Uri BaseAddress { get; }

    /// <summary>When set, later connections are dropped before the TLS handshake gets anywhere.</summary>
    public bool DropConnections { get; set; }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token);
            }
            catch (Exception)
            {
                return;
            }

            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            if (DropConnections) return;

            try
            {
                await using var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
                await ssl.AuthenticateAsServerAsync(_certificate, false, checkCertificateRevocation: false);

                // One read is enough: the test client sends a single small request.
                var buffer = new byte[4096];
                await ssl.ReadAtLeastAsync(buffer, 1, throwOnEndOfStream: false, _cts.Token);

                var body = "{}"u8.ToArray();
                var head = Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                await ssl.WriteAsync(head, _cts.Token);
                await ssl.WriteAsync(body, _cts.Token);
                await ssl.FlushAsync(_cts.Token);
            }
            catch (Exception)
            {
                // A client that rejects the certificate tears the connection down mid-handshake;
                // that is the case under test, not a server fault.
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        _cts.Dispose();
        _certificate.Dispose();
    }
}

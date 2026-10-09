using System.Net;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using NodePilot.Switcher.Configuration;
using NodePilot.Switcher.Services;
using Xunit;

namespace NodePilot.Switcher.Tests;

public sealed class ScorchRedirectTests
{
    [Theory]
    [InlineData("read")]
    [InlineData("start")]
    [InlineData("stop")]
    public async Task RealTransport_RejectsForeignRedirectBeforeSending(string operation)
    {
        await using var foreign = new LocalServer(_ => (200, null, "{\"value\":[]}"));
        await using var origin = new LocalServer(_ => (307, foreign.Address + "outside", ""));
        using var client = new ScorchApiClient(new ScorchWorkloadConfiguration("allow.txt", origin.Address));

        var action = () => InvokeAsync(client, operation);

        await action.Should().ThrowAsync<InvalidOperationException>().WithMessage("*configured API origin*");
        foreign.RequestCount.Should().Be(0);
    }

    [Theory]
    [InlineData("read", "GET")]
    [InlineData("start", "POST")]
    [InlineData("stop", "PATCH")]
    public async Task RealTransport_FollowsSameOrigin307AndPreservesMethodAndBody(string operation, string method)
    {
        string? redirectedMethod = null;
        string? redirectedBody = null;
        await using var origin = new LocalServer(request =>
        {
            if (request.Url!.AbsolutePath != "/final") return (307, "/final", "");
            redirectedMethod = request.HttpMethod;
            using var reader = new StreamReader(request.InputStream);
            redirectedBody = reader.ReadToEnd();
            return (200, null, "{\"value\":[]}");
        });
        using var client = new ScorchApiClient(new ScorchWorkloadConfiguration("allow.txt", origin.Address,
            StopJobMethod: "PATCH"));

        await InvokeAsync(client, operation);

        redirectedMethod.Should().Be(method);
        if (operation == "start") redirectedBody.Should().Contain("runbookId");
        if (operation == "stop") redirectedBody.Should().Be("{}");
        origin.RequestCount.Should().Be(2);
    }

    [Fact]
    public async Task RealTransport_BoundsRedirectLoops()
    {
        await using var origin = new LocalServer(_ => (302, "/again", ""));
        using var client = new ScorchApiClient(new ScorchWorkloadConfiguration("allow.txt", origin.Address));

        var action = () => client.ListRunbooksAsync(TestContext.Current.CancellationToken);

        await action.Should().ThrowAsync<InvalidOperationException>().WithMessage("*redirect*");
        origin.RequestCount.Should().BeLessThanOrEqualTo(11);
    }

    private static Task InvokeAsync(ScorchApiClient client, string operation) => operation switch
    {
        "start" => client.StartRunbookAsync(Guid.NewGuid(), ["server"], TestContext.Current.CancellationToken),
        "stop" => client.StopJobAsync(Guid.NewGuid(), TestContext.Current.CancellationToken),
        _ => client.ListRunbooksAsync(TestContext.Current.CancellationToken),
    };

    // Real redirect behavior, with anonymous loopback listeners: no Windows credential challenge.
    private sealed class LocalServer : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Task _pump;
        private int _requests;
        private volatile bool _disposing;
        public string Address { get; }
        public int RequestCount => Volatile.Read(ref _requests);

        public LocalServer(Func<HttpListenerRequest, (int Status, string? Location, string Body)> respond)
        {
            var portProbe = new TcpListener(IPAddress.Loopback, 0);
            portProbe.Start();
            var port = ((IPEndPoint)portProbe.LocalEndpoint).Port;
            portProbe.Stop();
            Address = $"http://127.0.0.1:{port}/";
            _listener.Prefixes.Add(Address);
            _listener.Start();
            _pump = PumpAsync(respond);
        }

        private async Task PumpAsync(Func<HttpListenerRequest, (int Status, string? Location, string Body)> respond)
        {
            try
            {
                while (_listener.IsListening)
                {
                    var context = await _listener.GetContextAsync();
                    Interlocked.Increment(ref _requests);
                    var response = respond(context.Request);
                    context.Response.StatusCode = response.Status;
                    if (response.Location is not null) context.Response.RedirectLocation = response.Location;
                    var body = Encoding.UTF8.GetBytes(response.Body);
                    context.Response.ContentLength64 = body.Length;
                    await context.Response.OutputStream.WriteAsync(body);
                    context.Response.Close();
                }
            }
            catch (HttpListenerException) when (_disposing) { }
            catch (ObjectDisposedException) when (_disposing) { }
        }

        public async ValueTask DisposeAsync()
        {
            _disposing = true;
            _listener.Close();
            await _pump;
        }
    }
}

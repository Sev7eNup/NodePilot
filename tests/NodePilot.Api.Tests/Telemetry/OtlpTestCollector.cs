using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging;
using Xunit;

namespace NodePilot.Api.Tests.Telemetry;

internal sealed class OtlpTestCollector(WebApplication app) : IAsyncDisposable
{
    private readonly TaskCompletionSource<ExportRequest> _firstRequest = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string Endpoint => app.Urls.Single();
    public ConcurrentQueue<ExportRequest> Requests { get; } = new();

    public static async Task<OtlpTestCollector> StartAsync(HttpProtocols protocol)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
            options.Listen(IPAddress.Loopback, 0, listen => listen.Protocols = protocol));
        var app = builder.Build();
        var collector = new OtlpTestCollector(app);
        app.Run(collector.ReceiveAsync);
        try
        {
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            startup.CancelAfter(TimeSpan.FromSeconds(10));
            await app.StartAsync(startup.Token);
            return collector;
        }
        catch
        {
            await collector.DisposeAsync();
            throw;
        }
    }

    public Task<ExportRequest> WaitForRequestAsync() =>
        _firstRequest.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

    private async Task ReceiveAsync(HttpContext context)
    {
        using var body = new MemoryStream();
        await context.Request.Body.CopyToAsync(body, context.RequestAborted);
        var request = new ExportRequest(
            context.Request.Method,
            context.Request.Path.Value ?? "",
            context.Request.Protocol,
            context.Request.ContentType ?? "",
            context.Request.Headers.ToDictionary(pair => pair.Key, pair => pair.Value.ToString(), StringComparer.OrdinalIgnoreCase),
            body.ToArray());
        Requests.Enqueue(request);
        _firstRequest.TrySetResult(request);

        if (request.ContentType.StartsWith("application/grpc", StringComparison.Ordinal))
        {
            context.Response.ContentType = "application/grpc";
            context.Response.DeclareTrailer("grpc-status");
            // An empty ExportLogsServiceResponse in an uncompressed gRPC frame.
            await context.Response.Body.WriteAsync(new byte[5], context.RequestAborted);
            context.Response.AppendTrailer("grpc-status", "0");
        }
        else
        {
            context.Response.ContentType = "application/x-protobuf";
            context.Response.ContentLength = 0;
        }
    }

    public async ValueTask DisposeAsync() =>
        await app.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

    internal sealed record ExportRequest(
        string Method,
        string Path,
        string Protocol,
        string ContentType,
        Dictionary<string, string> Headers,
        byte[] Body);
}

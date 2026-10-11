using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NodePilot.Api.Controllers;
using NodePilot.Api.Diagnostics;
using NodePilot.Api.Dtos;
using NodePilot.Api.Tests.TestSupport;
using NodePilot.Core.Audit;
using NodePilot.Engine.Security;
using Serilog.Events;
using Xunit;
using static NodePilot.Api.Tests.Logging.AppLogTestLines;

namespace NodePilot.Api.Tests.Controllers;

public sealed class AppLogControllerTests : IDisposable
{
    // Noon local time in January: the controller reads files by local date and offset.
    private static readonly DateTime LocalNoon = new(2026, 1, 15, 12, 0, 0, DateTimeKind.Unspecified);
    private static readonly DateTimeOffset Now = new(LocalNoon, TimeZoneInfo.Local.GetUtcOffset(LocalNoon));
    private static readonly DateOnly Today = new(2026, 1, 15);

    private readonly string _root = Directory.CreateTempSubdirectory("np-app-log-ctl-").FullName;
    private readonly CapturingAuditWriter _audit = new();
    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();
    }

    private AppLogController Controller(IAppLogFileResolver? resolver = null) => new(
        resolver ?? new AppLogFileResolver(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Logging:File:Path"] = Path.Combine(_root, "nodepilot-.log"),
            }).Build(), new StubEnvironment(_root)),
        new OutputRedactor(),
        _audit,
        NullLogger<AppLogController>.Instance,
        new FixedClock(Now));

    private string Segment(DateOnly date, string suffix, IEnumerable<string> lines)
    {
        var path = Path.Combine(_root, $"nodepilot-{date:yyyyMMdd}{suffix}.log");
        File.WriteAllText(path, string.Join("\r\n", lines) + "\r\n", new UTF8Encoding(false));
        return path;
    }

    private static string Cm(DateTimeOffset at, LogEventLevel level, string message)
        => CmTrace(Event(level, message, at)).Single();

    private static AppLogResponse Ok(ActionResult<AppLogResponse> result)
        => (AppLogResponse)((OkObjectResult)result.Result!).Value!;

    [Fact]
    public void Get_FiltersBeforeTake_AndSummaryIgnoresFiltersAndTake()
    {
        var lines = new List<string> { Cm(Now.AddHours(-30), LogEventLevel.Error, "outside the window") };
        for (var i = 0; i < 5; i++) lines.Add(Cm(Now.AddMinutes(-50 + i), LogEventLevel.Information, $"info {i}"));
        for (var i = 0; i < 3; i++) lines.Add(Cm(Now.AddMinutes(-40 + i), LogEventLevel.Warning, $"warn {i}"));
        for (var i = 0; i < 4; i++) lines.Add(Cm(Now.AddMinutes(-30 + i), LogEventLevel.Error, $"err {i}"));
        Segment(Today.AddDays(-1), "", [lines[0]]);
        Segment(Today, "", lines.Skip(1));

        var response = Ok(Controller().Get(hours: 24, minLevel: "warning", take: 2));

        response.Summary.Should().Be(new AppLogSummary(Errors: 4, Warnings: 3, Total: 12));
        response.Results.Should().Be(new AppLogResults(Matched: 7, Returned: 2, Truncated: true));
        response.Entries.Select(e => e.Message).Should().Equal("err 3", "err 2");
        response.Entries.Should().OnlyContain(e => e.Level == "Error");
        response.Scan.Complete.Should().BeTrue();
    }

    [Fact]
    public void Get_RedactsMessageAndDetails_AndSearchCannotProbeTheSecret()
    {
        Segment(Today, "", [
            Cm(Now.AddMinutes(-5), LogEventLevel.Warning, "connect to db1 failed password=hunter2"),
            Cm(Now.AddMinutes(-4), LogEventLevel.Warning, "unrelated"),
        ]);
        var controller = Controller();

        var all = Ok(controller.Get(hours: 1));
        var probe = Ok(controller.Get(hours: 1, q: "hunter2"));
        var byHost = Ok(controller.Get(hours: 1, q: "DB1"));

        all.Entries.Select(e => e.Message).Should().Contain(m => m.Contains("password=***"))
            .And.NotContain(m => m.Contains("hunter2"));
        probe.Results.Matched.Should().Be(0);
        byHost.Entries.Should().ContainSingle();
    }

    [Fact]
    public void Get_WindowWithoutOlderFiles_DoesNotClaimCompleteCoverage()
    {
        Segment(Today, "", [Cm(Now.AddMinutes(-5), LogEventLevel.Information, "only today")]);

        var response = Ok(Controller().Get(hours: 24));

        response.Scan.Complete.Should().BeFalse();
        response.Scan.CoverageStart.Should().Be(Now.AddMinutes(-5));
        response.Summary.Errors.Should().Be(0);
    }

    [Fact]
    public void Get_UnknownMinLevel_IsRejected()
    {
        var result = Controller().Get(minLevel: "loud");

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public void Get_ReadFailure_ReturnsCorrelationIdWithoutLeakingThePath()
    {
        var result = Controller(new ThrowingResolver()).Get();

        var status = result.Result.Should().BeOfType<ObjectResult>().Subject;
        status.StatusCode.Should().Be(500);
        System.Text.Json.JsonSerializer.Serialize(status.Value).Should()
            .NotContain("secret-share").And.Contain("APP_LOG_READ_FAILED");
    }

    [Fact]
    public async Task Download_MergesSegmentsInRollOrder_Redacts_AndAudits()
    {
        Segment(Today, "", ["first-segment"]);
        Segment(Today, "_001", [
            "second-segment password=hunter2",
            "-----BEGIN RSA PRIVATE KEY-----",
            "MIIEowIBAAKCAQEAu1SU1LfVLPHCozMxH2Mo4lgOEePzNm0tRgeLezV6ffAt0gun",
            "-----END RSA PRIVATE KEY-----",
            "after-key"]);
        var controller = Controller();

        var result = await controller.Download("2026-01-15", TestContext.Current.CancellationToken);

        var body = await Execute(result);
        body.Should().Contain("first-segment").And.Contain("password=***").And.Contain("after-key")
            .And.NotContain("hunter2").And.NotContain("MIIEow").And.NotContain("BEGIN RSA");
        body.IndexOf("first-segment", StringComparison.Ordinal).Should()
            .BeLessThan(body.IndexOf("second-segment", StringComparison.Ordinal));
        _audit.Calls.Should().ContainSingle().Which.Action.Should().Be(AuditActions.AppLogDownloaded);
        _audit.Calls[0].Details.Should().Contain("\"segments\":2").And.Contain("\"redacted\":true");
    }

    [Fact]
    public async Task Download_SetsRedactedFileName()
    {
        Segment(Today, "", ["line"]);
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        var result = await Controller().Download("2026-01-15", TestContext.Current.CancellationToken);
        await result.ExecuteResultAsync(new ActionContext { HttpContext = context });

        context.Response.Headers.ContentDisposition.ToString().Should()
            .Contain("nodepilot-app-2026-01-15.redacted.log");
        context.Response.ContentType.Should().StartWith("text/plain");
    }

    [Fact]
    public async Task Download_InvalidDate_IsBadRequest_MissingDay_IsNotFound()
    {
        var controller = Controller();

        (await controller.Download("15.01.2026", TestContext.Current.CancellationToken))
            .Should().BeOfType<BadRequestObjectResult>();
        (await controller.Download("2026-01-14", TestContext.Current.CancellationToken))
            .Should().BeOfType<NotFoundResult>();
        _audit.Calls.Should().BeEmpty();
    }

    private static async Task<string> Execute(IActionResult result)
    {
        var context = new DefaultHttpContext();
        var body = new MemoryStream();
        context.Response.Body = body;
        await result.ExecuteResultAsync(new ActionContext { HttpContext = context });
        return Encoding.UTF8.GetString(body.ToArray());
    }

    private sealed class ThrowingResolver : IAppLogFileResolver
    {
        public IReadOnlyList<string> GetFilesForDate(DateOnly date)
            => throw new InvalidOperationException(@"\\secret-share\logs is unreachable");
    }
}

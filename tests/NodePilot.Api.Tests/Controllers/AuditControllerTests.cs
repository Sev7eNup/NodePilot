using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NodePilot.Api.Controllers;
using NodePilot.Api.Dtos;
using NodePilot.Core.Models;
using NodePilot.Data;
using Xunit;

namespace NodePilot.Api.Tests.Controllers;

public class AuditControllerTests
{
    private static NodePilotDbContext CreateContext() => NodePilot.TestCommons.TestDbFactory.Create();

    private static AuditLogEntry Entry(
        string action, DateTime ts,
        Guid? resourceId = null, string? resourceType = null,
        Guid? userId = null, string? username = null, string? ipAddress = null)
        => new AuditLogEntry
        {
            Id = Guid.NewGuid(),
            Timestamp = ts,
            Action = action,
            ResourceType = resourceType,
            ResourceId = resourceId,
            UserId = userId,
            Username = username,
            IpAddress = ipAddress,
        };

    private static AuditPageResponse PageOf(ActionResult<AuditPageResponse> result)
        => (AuditPageResponse)((OkObjectResult)result.Result!).Value!;

    [Fact]
    public async Task GetAll_ReturnsNewestFirst_RespectsTakeCap()
    {
        var db = CreateContext();
        for (int i = 0; i < 10; i++)
            db.AuditLog.Add(Entry("WORKFLOW_CREATED", DateTime.UtcNow.AddMinutes(-i)));
        await db.SaveChangesAsync();

        var controller = new AuditController(db);
        var result = await controller.GetAll(
            action: null, resourceType: null, resourceId: null, userId: null,
            ipAddress: null, since: null, until: null,
            afterTs: null, afterId: null, take: 3, CancellationToken.None);

        var page = PageOf(result);
        page.Items.Should().HaveCount(3);
        page.Items.Should().BeInDescendingOrder(r => r.Timestamp);
    }

    [Fact]
    public async Task GetAll_FilterByAction_OnlyMatching()
    {
        var db = CreateContext();
        db.AuditLog.Add(Entry("WORKFLOW_CREATED", DateTime.UtcNow));
        db.AuditLog.Add(Entry("LOGIN_FAILED", DateTime.UtcNow));
        db.AuditLog.Add(Entry("WORKFLOW_UPDATED", DateTime.UtcNow));
        await db.SaveChangesAsync();

        var controller = new AuditController(db);
        var result = await controller.GetAll(
            action: "LOGIN_FAILED", resourceType: null, resourceId: null, userId: null,
            ipAddress: null, since: null, until: null,
            afterTs: null, afterId: null, take: 100, CancellationToken.None);

        var page = PageOf(result);
        page.Items.Should().ContainSingle();
        page.Items[0].Action.Should().Be("LOGIN_FAILED");
    }

    [Fact]
    public async Task GetAll_FilterByResourceId_OnlyMatching()
    {
        var db = CreateContext();
        var target = Guid.NewGuid();
        db.AuditLog.Add(Entry("WORKFLOW_CREATED", DateTime.UtcNow, resourceId: target));
        db.AuditLog.Add(Entry("WORKFLOW_CREATED", DateTime.UtcNow, resourceId: Guid.NewGuid()));
        await db.SaveChangesAsync();

        var controller = new AuditController(db);
        var result = await controller.GetAll(
            action: null, resourceType: null, resourceId: target, userId: null,
            ipAddress: null, since: null, until: null,
            afterTs: null, afterId: null, take: 100, CancellationToken.None);

        var page = PageOf(result);
        page.Items.Should().ContainSingle();
        page.Items[0].ResourceId.Should().Be(target);
    }

    [Fact]
    public async Task GetAll_FilterBySinceAndUntil_WindowsCorrectly()
    {
        var db = CreateContext();
        var ref0 = DateTime.UtcNow;
        db.AuditLog.Add(Entry("X", ref0.AddHours(-3))); // out (too old)
        db.AuditLog.Add(Entry("X", ref0.AddHours(-1))); // in
        db.AuditLog.Add(Entry("X", ref0.AddHours(+1))); // out (too new)
        await db.SaveChangesAsync();

        var controller = new AuditController(db);
        var result = await controller.GetAll(
            action: null, resourceType: null, resourceId: null, userId: null,
            ipAddress: null, since: ref0.AddHours(-2), until: ref0,
            afterTs: null, afterId: null, take: 100, CancellationToken.None);

        var page = PageOf(result);
        page.Items.Should().ContainSingle();
    }

    [Fact]
    public async Task GetAll_FilterByIpAddress_OnlyMatching()
    {
        var db = CreateContext();
        db.AuditLog.Add(Entry("X", DateTime.UtcNow, ipAddress: "10.0.0.1"));
        db.AuditLog.Add(Entry("X", DateTime.UtcNow, ipAddress: "10.0.0.2"));
        db.AuditLog.Add(Entry("X", DateTime.UtcNow, ipAddress: null));
        await db.SaveChangesAsync();

        var controller = new AuditController(db);
        var result = await controller.GetAll(
            action: null, resourceType: null, resourceId: null, userId: null,
            ipAddress: "10.0.0.2", since: null, until: null,
            afterTs: null, afterId: null, take: 100, CancellationToken.None);

        var page = PageOf(result);
        page.Items.Should().ContainSingle();
        page.Items[0].IpAddress.Should().Be("10.0.0.2");
    }

    [Fact]
    public async Task GetAll_ReturnsUsernameAndIpAddress_OnEveryRow()
    {
        var db = CreateContext();
        db.AuditLog.Add(Entry("X", DateTime.UtcNow, username: "alice", ipAddress: "::1"));
        await db.SaveChangesAsync();

        var controller = new AuditController(db);
        var result = await controller.GetAll(
            action: null, resourceType: null, resourceId: null, userId: null,
            ipAddress: null, since: null, until: null,
            afterTs: null, afterId: null, take: 100, CancellationToken.None);

        var page = PageOf(result);
        page.Items.Should().ContainSingle();
        page.Items[0].Username.Should().Be("alice");
        page.Items[0].IpAddress.Should().Be("::1");
    }

    [Fact]
    public async Task GetAll_FullPageWithMoreBehind_ReturnsNextCursor()
    {
        var db = CreateContext();
        // 5 rows, take=3 → page 1 returns 3 with more behind → cursor must be set.
        for (int i = 0; i < 5; i++)
            db.AuditLog.Add(Entry("X", DateTime.UtcNow.AddMinutes(-i)));
        await db.SaveChangesAsync();

        var controller = new AuditController(db);
        var result = await controller.GetAll(
            action: null, resourceType: null, resourceId: null, userId: null,
            ipAddress: null, since: null, until: null,
            afterTs: null, afterId: null, take: 3, CancellationToken.None);

        var page = PageOf(result);
        page.Items.Should().HaveCount(3);
        page.NextCursor.Should().NotBeNull();
        page.NextCursor!.Timestamp.Should().Be(page.Items[^1].Timestamp);
        page.NextCursor.Id.Should().Be(page.Items[^1].Id);
    }

    [Fact]
    public async Task GetAll_ExactlyFullPage_NoMoreBehind_DoesNotReturnPhantomCursor()
    {
        // Regression: previously `rows.Count == take` set the cursor even when there were
        // no more rows behind it, causing the UI to render a Load-More button that fetched
        // an empty page. The take+1 probe must distinguish this from a genuinely full page.
        var db = CreateContext();
        for (int i = 0; i < 3; i++)
            db.AuditLog.Add(Entry("X", DateTime.UtcNow.AddMinutes(-i)));
        await db.SaveChangesAsync();

        var controller = new AuditController(db);
        var result = await controller.GetAll(
            action: null, resourceType: null, resourceId: null, userId: null,
            ipAddress: null, since: null, until: null,
            afterTs: null, afterId: null, take: 3, CancellationToken.None);

        var page = PageOf(result);
        page.Items.Should().HaveCount(3);
        page.NextCursor.Should().BeNull("page is exactly full but no rows remain — phantom cursor would cause an empty next fetch");
    }

    [Fact]
    public async Task GetAll_ShortPage_NoCursor()
    {
        var db = CreateContext();
        db.AuditLog.Add(Entry("X", DateTime.UtcNow));
        db.AuditLog.Add(Entry("X", DateTime.UtcNow));
        await db.SaveChangesAsync();

        var controller = new AuditController(db);
        var result = await controller.GetAll(
            action: null, resourceType: null, resourceId: null, userId: null,
            ipAddress: null, since: null, until: null,
            afterTs: null, afterId: null, take: 100, CancellationToken.None);

        var page = PageOf(result);
        page.Items.Should().HaveCount(2);
        page.NextCursor.Should().BeNull();
    }

    [Fact]
    public async Task ExportStream_Csv_HeaderAndRowsWritten()
    {
        var db = CreateContext();
        db.AuditLog.Add(Entry("WORKFLOW_CREATED", DateTime.UtcNow, username: "alice", ipAddress: "10.0.0.1"));
        db.AuditLog.Add(Entry("LOGIN_FAILED", DateTime.UtcNow, username: "bob", ipAddress: "10.0.0.2"));
        await db.SaveChangesAsync();

        var controller = new AuditController(db);
        var httpCtx = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        var bodyStream = new MemoryStream();
        httpCtx.Response.Body = bodyStream;
        controller.ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext { HttpContext = httpCtx };

        await controller.ExportStream(format: "csv", action: null, resourceType: null, resourceId: null,
            userId: null, ipAddress: null, since: null, until: null, ct: CancellationToken.None);

        var body = System.Text.Encoding.UTF8.GetString(bodyStream.ToArray());
        body.Should().StartWith("Id,Timestamp,UserId,Username,Action,ResourceType,ResourceId,IpAddress,Details");
        body.Should().Contain("WORKFLOW_CREATED").And.Contain("LOGIN_FAILED");
        body.Should().Contain("alice").And.Contain("bob");
        body.Should().Contain("10.0.0.1").And.Contain("10.0.0.2");
        httpCtx.Response.ContentType.Should().StartWith("text/csv");
    }

    [Fact]
    public async Task ExportStream_Ndjson_OneObjectPerLine()
    {
        var db = CreateContext();
        db.AuditLog.Add(Entry("X", DateTime.UtcNow, username: "u1"));
        db.AuditLog.Add(Entry("Y", DateTime.UtcNow, username: "u2"));
        await db.SaveChangesAsync();

        var controller = new AuditController(db);
        var httpCtx = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        var bodyStream = new MemoryStream();
        httpCtx.Response.Body = bodyStream;
        controller.ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext { HttpContext = httpCtx };

        await controller.ExportStream(format: "ndjson", action: null, resourceType: null, resourceId: null,
            userId: null, ipAddress: null, since: null, until: null, ct: CancellationToken.None);

        var body = System.Text.Encoding.UTF8.GetString(bodyStream.ToArray());
        var lines = body.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines.Should().HaveCount(2, "one row → one NDJSON object per line, no CSV header");
        foreach (var line in lines)
        {
            Action act = () => System.Text.Json.JsonDocument.Parse(line);
            act.Should().NotThrow("every line must be parseable JSON for SIEM-pipe consumption");
        }
        httpCtx.Response.ContentType.Should().StartWith("application/x-ndjson");
    }

    [Fact]
    public async Task ExportStream_UnsupportedFormat_Returns400()
    {
        var db = CreateContext();
        var controller = new AuditController(db);
        var httpCtx = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        httpCtx.Response.Body = new MemoryStream();
        controller.ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext { HttpContext = httpCtx };

        await controller.ExportStream(format: "xml", action: null, resourceType: null, resourceId: null,
            userId: null, ipAddress: null, since: null, until: null, ct: CancellationToken.None);

        httpCtx.Response.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task ExportStream_Csv_QuotesFieldWithComma()
    {
        var db = CreateContext();
        db.AuditLog.Add(new AuditLogEntry
        {
            Id = Guid.NewGuid(),
            Timestamp = DateTime.UtcNow,
            Action = "X",
            Details = "value,with,commas",
        });
        await db.SaveChangesAsync();

        var controller = new AuditController(db);
        var httpCtx = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        var bodyStream = new MemoryStream();
        httpCtx.Response.Body = bodyStream;
        controller.ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext { HttpContext = httpCtx };

        await controller.ExportStream(format: "csv", action: null, resourceType: null, resourceId: null,
            userId: null, ipAddress: null, since: null, until: null, ct: CancellationToken.None);

        var body = System.Text.Encoding.UTF8.GetString(bodyStream.ToArray());
        body.Should().Contain("\"value,with,commas\"",
            "RFC 4180: fields containing commas must be wrapped in double quotes");
    }

    [Fact]
    public async Task GetAll_CursorPagination_TraversesAllRows_NoDuplicates_NoGaps()
    {
        var db = CreateContext();
        // 10 rows; 4 share a timestamp to exercise the (Timestamp, Id) tie-break.
        var baseTs = DateTime.UtcNow;
        var tie = baseTs.AddMinutes(-5);
        var ids = new HashSet<Guid>();
        for (int i = 0; i < 6; i++)
        {
            var e = Entry("X", baseTs.AddMinutes(-i));
            ids.Add(e.Id);
            db.AuditLog.Add(e);
        }
        for (int j = 0; j < 4; j++)
        {
            var e = Entry("X", tie);
            ids.Add(e.Id);
            db.AuditLog.Add(e);
        }
        await db.SaveChangesAsync();

        var controller = new AuditController(db);
        var collected = new List<AuditEntryResponse>();
        DateTime? afterTs = null;
        Guid? afterId = null;
        for (int page = 0; page < 10; page++)
        {
            var result = await controller.GetAll(
                action: null, resourceType: null, resourceId: null, userId: null,
                ipAddress: null, since: null, until: null,
                afterTs: afterTs, afterId: afterId, take: 3, CancellationToken.None);
            var p = PageOf(result);
            collected.AddRange(p.Items);
            if (p.NextCursor is null) break;
            afterTs = p.NextCursor.Timestamp;
            afterId = p.NextCursor.Id;
        }

        collected.Should().HaveCount(10);
        collected.Select(r => r.Id).Should().OnlyHaveUniqueItems();
        new HashSet<Guid>(collected.Select(r => r.Id)).Should().BeEquivalentTo(ids);
    }
}

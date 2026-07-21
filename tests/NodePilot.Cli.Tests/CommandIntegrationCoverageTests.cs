using FluentAssertions;
using NodePilot.Cli;
using NodePilot.Cli.Tests.Infra;
using Spectre.Console;
using Spectre.Console.Testing;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using Xunit;

namespace NodePilot.Cli.Tests;

/// <summary>
/// End-to-end command coverage for verbs not previously harnessed: settings test smtp/llm,
/// workflow step-test + step-test-context, and the backup export/preview/restore/manifest
/// surface. Asserts real behaviour — validation guards that fire before any HTTP call,
/// exit-code mapping from the server's success flag, and the multipart upload path — not
/// just "command ran".
/// </summary>
[Collection(CommandTestCollection.Name)]
public class CommandIntegrationCoverageTests
{
    // ===== settings test smtp / llm =========================================

    [Fact]
    public void SettingsTestSmtp_NoFile_FailsBeforeHttp()
    {
        using var h = new CommandTestHarness();
        var result = h.Run("settings", "test", "smtp");
        result.ExitCode.Should().Be(ExitCodes.Error);
        result.StdErr.Should().Contain("--file");
        h.Server.LogEntries.Should().NotContain(e =>
            e.RequestMessage.AbsolutePath == "/api/admin/settings/test/smtp");
    }

    [Fact]
    public void SettingsTestSmtp_ProbeOk_ReturnsSuccess()
    {
        using var h = new CommandTestHarness();
        h.Server.Given(Request.Create().WithPath("/api/admin/settings/test/smtp").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                ok = true, message = "Connected to mail:25", durationMs = 42.0, errorKind = (string?)null,
            }));

        var file = WriteTemp("""{"settings":{"host":"mail","port":25},"toAddress":"a@b.c"}""");
        try
        {
            var result = h.Run("settings", "test", "smtp", "--file", file);
            result.ExitCode.Should().Be(ExitCodes.Success);
            result.Output.Should().Contain("Connected to mail:25");
        }
        finally { Del(file); }
    }

    [Fact]
    public void SettingsTestSmtp_ProbeFailed_ReturnsErrorExit()
    {
        using var h = new CommandTestHarness();
        h.Server.Given(Request.Create().WithPath("/api/admin/settings/test/smtp").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                ok = false, message = "Auth failed", durationMs = 11.0, errorKind = "SmtpException",
            }));

        var file = WriteTemp("""{"settings":{"host":"mail","port":25}}""");
        try
        {
            var result = h.Run("settings", "test", "smtp", "--file", file);
            // ok=false → the command maps the probe result to a non-zero exit code.
            result.ExitCode.Should().Be(ExitCodes.Error);
        }
        finally { Del(file); }
    }

    [Fact]
    public void SettingsTestSmtp_InvalidJsonFile_FailsBeforeHttp()
    {
        using var h = new CommandTestHarness();
        var file = WriteTemp("{ this is not json");
        try
        {
            var result = h.Run("settings", "test", "smtp", "--file", file);
            result.ExitCode.Should().Be(ExitCodes.Error);
            result.StdErr.Should().Contain("JSON");
            h.Server.LogEntries.Should().NotContain(e =>
                e.RequestMessage.AbsolutePath == "/api/admin/settings/test/smtp");
        }
        finally { Del(file); }
    }

    [Fact]
    public void SettingsTestLlm_ProbeOk_ReturnsSuccess()
    {
        using var h = new CommandTestHarness();
        h.Server.Given(Request.Create().WithPath("/api/admin/settings/test/llm").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                ok = true, message = "Model reachable", durationMs = 88.0, errorKind = (string?)null,
            }));

        var file = WriteTemp("""{"settings":{"baseUrl":"https://api.openai.com","model":"gpt"}}""");
        try
        {
            var result = h.Run("settings", "test", "llm", "--file", file);
            result.ExitCode.Should().Be(ExitCodes.Success);
            result.Output.Should().Contain("Model reachable");
        }
        finally { Del(file); }
    }

    // ===== workflow step-test ===============================================

    [Fact]
    public void WorkflowStepTest_Success_ResolvesAndPostsAndExitsZero()
    {
        using var h = new CommandTestHarness();
        var id = Guid.NewGuid();
        h.Server.Given(Request.Create().WithPath($"/api/workflows/{id}").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(SampleWorkflow(id)));
        h.Server.Given(Request.Create().WithPath($"/api/workflows/{id}/steps/step-1/test").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                success = true, output = "OK", errorOutput = (string?)null,
                outputParameters = new Dictionary<string, string> { ["hostName"] = "WEB01" },
                durationMs = 12.0, errorMessage = (string?)null,
            }));

        var result = h.Run("workflow", "step-test", id.ToString(), "step-1", "-m", "checkDisk.output=7");
        result.ExitCode.Should().Be(ExitCodes.Success);
        result.Output.Should().Contain("\"success\":true").And.Contain("WEB01");
    }

    [Fact]
    public void WorkflowStepTest_BadMockFormat_FailsBeforeHttp()
    {
        using var h = new CommandTestHarness();
        var id = Guid.NewGuid();
        h.Server.Given(Request.Create().WithPath($"/api/workflows/{id}").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(SampleWorkflow(id)));

        // "noEquals" has no '=' → the mock parser rejects it before any test POST.
        var result = h.Run("workflow", "step-test", id.ToString(), "step-1", "-m", "noEquals");
        result.ExitCode.Should().Be(ExitCodes.Error);
        result.StdErr.Should().Contain("key=value");
        h.Server.LogEntries.Should().NotContain(e =>
            e.RequestMessage.AbsolutePath.EndsWith("/test"));
    }

    [Fact]
    public void WorkflowStepTest_ServerReportsFailure_MapsToRunFailedExit()
    {
        using var h = new CommandTestHarness();
        var id = Guid.NewGuid();
        h.Server.Given(Request.Create().WithPath($"/api/workflows/{id}").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(SampleWorkflow(id)));
        h.Server.Given(Request.Create().WithPath($"/api/workflows/{id}/steps/step-1/test").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                success = false, output = (string?)null, errorOutput = "boom",
                outputParameters = new Dictionary<string, string>(),
                durationMs = 5.0, errorMessage = "script threw",
            }));

        var result = h.Run("workflow", "step-test", id.ToString(), "step-1");
        result.ExitCode.Should().Be(ExitCodes.RunFailed);
    }

    [Fact]
    public void WorkflowStepTestContext_RendersContextJson()
    {
        using var h = new CommandTestHarness();
        var id = Guid.NewGuid();
        var execId = Guid.NewGuid();
        h.Server.Given(Request.Create().WithPath($"/api/workflows/{id}").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(SampleWorkflow(id)));
        h.Server.Given(Request.Create().WithPath($"/api/workflows/{id}/steps/step-1/test-context").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                executionId = execId, executedAt = DateTime.UtcNow, status = "Succeeded",
                variables = new[]
                {
                    new { key = "checkDisk.output", origin = "upstream", source = "step", value = "7" },
                },
            }));

        var result = h.Run("workflow", "step-test-context", id.ToString(), "step-1");
        result.ExitCode.Should().Be(ExitCodes.Success);
        result.Output.Should().Contain("checkDisk.output");
    }

    [Fact]
    public void WorkflowStepTestContext_ListRuns_HitsRunsEndpoint()
    {
        using var h = new CommandTestHarness();
        var id = Guid.NewGuid();
        h.Server.Given(Request.Create().WithPath($"/api/workflows/{id}").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(SampleWorkflow(id)));
        h.Server.Given(Request.Create().WithPath($"/api/workflows/{id}/steps/step-1/test-context/runs").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new[]
            {
                new { executionId = Guid.NewGuid(), startedAt = DateTime.UtcNow, status = "Succeeded", triggeredBy = "alice", stepRan = true },
            }));

        var result = h.Run("workflow", "step-test-context", id.ToString(), "step-1", "--list-runs");
        result.ExitCode.Should().Be(ExitCodes.Success);
        result.Output.Should().Contain("alice");
    }

    // ===== backup manifest / export / preview / restore =====================

    [Fact]
    public void BackupManifest_RendersSectionCounts()
    {
        using var h = new CommandTestHarness();
        h.Server.Given(Request.Create().WithPath("/api/backup/manifest").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                sections = new[]
                {
                    new { section = "workflows", count = 12 },
                    new { section = "credentials", count = 3 },
                },
            }));

        var (exit, output) = RunWithStaticConsole(h, "backup", "manifest");
        exit.Should().Be(ExitCodes.Success);
        output.Should().Contain("workflows").And.Contain("credentials");
    }

    [Fact]
    public void BackupExport_NoOut_FailsBeforeHttp()
    {
        using var h = new CommandTestHarness();
        var result = h.Run("backup", "export", "--passphrase-env", "X");
        result.ExitCode.Should().Be(ExitCodes.Error);
        result.StdErr.Should().Contain("--out");
    }

    [Fact]
    public void BackupExport_NoPassphraseSource_FailsBeforeHttp()
    {
        using var h = new CommandTestHarness();
        var outFile = Path.Combine(Path.GetTempPath(), "np-bk-" + Guid.NewGuid().ToString("N") + ".npbackup");
        // No passphrase env/file given and stdin is redirected under the test host → resolver
        // returns the "no passphrase source" error before any HTTP call.
        var result = h.Run("backup", "export", "--out", outFile, "--sections", "workflows");
        result.ExitCode.Should().Be(ExitCodes.Error);
        result.StdErr.Should().Contain("passphrase");
    }

    [Fact]
    public void BackupExport_Success_WritesArchiveFile()
    {
        using var h = new CommandTestHarness();
        var bytes = new byte[] { 1, 2, 3, 4, 5 };
        h.Server.Given(Request.Create().WithPath("/api/backup/export").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("X-Backup-Warnings", "0")
                .WithBody(bytes));

        var pwFile = WriteTemp("hunter2");
        var outFile = Path.Combine(Path.GetTempPath(), "np-bk-" + Guid.NewGuid().ToString("N") + ".npbackup");
        try
        {
            var result = h.Run("backup", "export", "--out", outFile,
                "--sections", "workflows,credentials", "--passphrase-file", pwFile);
            result.ExitCode.Should().Be(ExitCodes.Success);
            File.Exists(outFile).Should().BeTrue();
            File.ReadAllBytes(outFile).Should().Equal(bytes);
        }
        finally { Del(pwFile); Del(outFile); }
    }

    [Fact]
    public void BackupPreview_FileNotFound_FailsBeforeHttp()
    {
        using var h = new CommandTestHarness();
        var result = h.Run("backup", "preview", Path.Combine(Path.GetTempPath(), "does-not-exist.npbackup"));
        result.ExitCode.Should().Be(ExitCodes.Error);
        result.StdErr.Should().Contain("nicht gefunden");
    }

    [Fact]
    public void BackupPreview_Success_RendersSectionTable()
    {
        using var h = new CommandTestHarness();
        h.Server.Given(Request.Create().WithPath("/api/backup/preview").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                integrityVerified = true, appVersion = "2026.6",
                sections = new[] { new { section = "workflows", inBackup = 12, @new = 5, conflicts = 2 } },
                warnings = Array.Empty<string>(),
            }));

        var file = WriteTempBytes(new byte[] { 9, 9, 9 });
        try
        {
            var (exit, output) = RunWithStaticConsole(h, "backup", "preview", file);
            exit.Should().Be(ExitCodes.Success);
            output.Should().Contain("workflows");
        }
        finally { Del(file); }
    }

    [Fact]
    public void BackupRestore_FileNotFound_FailsBeforeHttp()
    {
        using var h = new CommandTestHarness();
        var result = h.Run("backup", "restore", Path.Combine(Path.GetTempPath(), "nope.npbackup"), "--yes");
        result.ExitCode.Should().Be(ExitCodes.Error);
        result.StdErr.Should().Contain("nicht gefunden");
    }

    [Fact]
    public void BackupRestore_NoYesNonInteractive_RefusesBeforeHttp()
    {
        using var h = new CommandTestHarness();
        var pwFile = WriteTemp("hunter2");
        var file = WriteTempBytes(new byte[] { 1, 2, 3 });
        try
        {
            // stdin redirected under the test host + no --yes → destructive guard refuses.
            var result = h.Run("backup", "restore", file, "--passphrase-file", pwFile);
            result.ExitCode.Should().Be(ExitCodes.Error);
            result.StdErr.Should().Contain("--yes");
            h.Server.LogEntries.Should().NotContain(e =>
                e.RequestMessage.AbsolutePath == "/api/backup/restore");
        }
        finally { Del(pwFile); Del(file); }
    }

    [Fact]
    public void BackupRestore_WithYes_PostsMultipartAndReportsSections()
    {
        using var h = new CommandTestHarness();
        h.Server.Given(Request.Create().WithPath("/api/backup/restore").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                sections = new[] { new { section = "workflows", created = 5, overwritten = 1, skipped = 2, renamed = 0 } },
                settings = new { applied = true, message = "ok" },
                warnings = new[] { "credential 'x' could not be rewrapped" },
            }));

        var pwFile = WriteTemp("hunter2");
        var file = WriteTempBytes(new byte[] { 1, 2, 3 });
        try
        {
            var (exit, output) = RunWithStaticConsole(h, "backup", "restore", file, "--passphrase-file", pwFile, "--yes", "--policy", "skip,users=overwrite");
            exit.Should().Be(ExitCodes.Success);
            output.Should().Contain("workflows");
            h.Server.LogEntries.Should().Contain(e =>
                e.RequestMessage.AbsolutePath == "/api/backup/restore");
        }
        finally { Del(pwFile); Del(file); }
    }

    // ===== table-mode rendering (exercises the WriteData table callbacks) ====
    // The tests above run with the harness's default -o json, which skips the table
    // renderer lambdas. These pass -o table explicitly so the grid/table builders run.

    [Fact]
    public void SettingsSystemInfo_Table_RendersAllRows()
    {
        using var h = new CommandTestHarness();
        h.Server.Given(Request.Create().WithPath("/api/admin/settings/system-info").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                appVersion = "2026.6.1", overridesPath = "/srv/np", databaseProvider = "postgres",
                databaseHost = "db01", secretsProvider = "Dpapi", clusterEnabled = true,
                clusterNodeId = "node-a", clusterIsLeader = true,
                jwtIssuer = "nodepilot", jwtAudience = "nodepilot-api",
            }));

        var result = h.Run("settings", "system-info", "-o", "table");
        result.ExitCode.Should().Be(ExitCodes.Success);
        result.Output.Should().Contain("2026.6.1").And.Contain("postgres").And.Contain("node-a");
    }

    [Fact]
    public void WorkflowStepTest_Table_RendersGridOutputAndParams()
    {
        using var h = new CommandTestHarness();
        var id = Guid.NewGuid();
        h.Server.Given(Request.Create().WithPath($"/api/workflows/{id}").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(SampleWorkflow(id)));
        h.Server.Given(Request.Create().WithPath($"/api/workflows/{id}/steps/step-1/test").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                success = true, output = "stdout-line", errorOutput = "stderr-line",
                outputParameters = new Dictionary<string, string> { ["hostName"] = "WEB01" },
                durationMs = 12.0, errorMessage = (string?)null,
            }));

        var result = h.Run("workflow", "step-test", id.ToString(), "step-1", "-o", "table");
        result.ExitCode.Should().Be(ExitCodes.Success);
        // The table callback renders the grid + Output/ErrorOutput sections + params table.
        result.Output.Should().Contain("success").And.Contain("stdout-line")
              .And.Contain("stderr-line").And.Contain("hostName").And.Contain("WEB01");
    }

    [Fact]
    public void WorkflowStepTestContext_Table_RendersVariables()
    {
        using var h = new CommandTestHarness();
        var id = Guid.NewGuid();
        h.Server.Given(Request.Create().WithPath($"/api/workflows/{id}").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(SampleWorkflow(id)));
        h.Server.Given(Request.Create().WithPath($"/api/workflows/{id}/steps/step-1/test-context").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                executionId = Guid.NewGuid(), executedAt = DateTime.UtcNow, status = "Succeeded",
                variables = new[]
                {
                    new { key = "checkDisk.output", origin = "upstream", source = "step", value = "7" },
                    new { key = "nullVar", origin = "upstream", source = "step", value = (string?)null },
                },
            }));

        var result = h.Run("workflow", "step-test-context", id.ToString(), "step-1", "-o", "table");
        result.ExitCode.Should().Be(ExitCodes.Success);
        result.Output.Should().Contain("checkDisk.output").And.Contain("Succeeded");
        result.Output.Should().Contain("<null>"); // null value rendered as placeholder
    }

    [Fact]
    public void WorkflowStepTestContext_ListRuns_Table_RendersRunRows()
    {
        using var h = new CommandTestHarness();
        var id = Guid.NewGuid();
        h.Server.Given(Request.Create().WithPath($"/api/workflows/{id}").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(SampleWorkflow(id)));
        h.Server.Given(Request.Create().WithPath($"/api/workflows/{id}/steps/step-1/test-context/runs").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new[]
            {
                new { executionId = Guid.NewGuid(), startedAt = DateTime.UtcNow, status = "Succeeded", triggeredBy = "alice", stepRan = true },
                new { executionId = Guid.NewGuid(), startedAt = DateTime.UtcNow, status = "Failed", triggeredBy = (string?)null, stepRan = false },
            }));

        var result = h.Run("workflow", "step-test-context", id.ToString(), "step-1", "--list-runs", "-o", "table");
        result.ExitCode.Should().Be(ExitCodes.Success);
        result.Output.Should().Contain("alice").And.Contain("Succeeded").And.Contain("Failed");
    }

    [Fact]
    public void SettingsTestSmtp_Table_RendersResultGrid()
    {
        using var h = new CommandTestHarness();
        h.Server.Given(Request.Create().WithPath("/api/admin/settings/test/smtp").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                ok = false, message = "Auth failed", durationMs = 11.0, errorKind = "SmtpException",
            }));

        var file = WriteTemp("""{"settings":{"host":"mail","port":25}}""");
        try
        {
            var result = h.Run("settings", "test", "smtp", "--file", file, "-o", "table");
            result.ExitCode.Should().Be(ExitCodes.Error);
            result.Output.Should().Contain("Auth failed").And.Contain("SmtpException");
        }
        finally { Del(file); }
    }

    // ===== helpers ==========================================================

    private static string WriteTemp(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), "np-ct-" + Guid.NewGuid().ToString("N") + ".tmp");
        File.WriteAllText(path, content);
        return path;
    }

    private static string WriteTempBytes(byte[] content)
    {
        var path = Path.Combine(Path.GetTempPath(), "np-ct-" + Guid.NewGuid().ToString("N") + ".bin");
        File.WriteAllBytes(path, content);
        return path;
    }

    private static void Del(string path) { try { File.Delete(path); } catch { /* best-effort */ } }

    /// <summary>
    /// Runs a command that writes its table to the *static* <see cref="AnsiConsole"/>
    /// (backup manifest/preview/restore do). Redirects the static console to a width-pinned
    /// <see cref="TestConsole"/> so layout never hits the "could not determine terminal width"
    /// path under a redirected test host, and returns its captured output alongside the exit code.
    /// </summary>
    private static (int ExitCode, string Output) RunWithStaticConsole(CommandTestHarness h, params string[] args)
    {
        var prev = AnsiConsole.Console;
        var tc = new TestConsole();
        tc.Profile.Width = 240;
        AnsiConsole.Console = tc;
        try
        {
            var r = h.Run(args);
            return (r.ExitCode, r.AnyOutput + tc.Output);
        }
        finally { AnsiConsole.Console = prev; }
    }

    private static string SampleWorkflow(Guid id) => $$"""
    {
      "id": "{{id}}",
      "name": "wf",
      "description": null,
      "definitionJson": "{}",
      "version": 1,
      "isEnabled": true,
      "createdAt": "2026-01-01T00:00:00Z",
      "updatedAt": "2026-01-01T00:00:00Z",
      "createdBy": null,
      "updatedBy": null,
      "activityCount": 0,
      "triggerTypes": [],
      "lastExecution": null,
      "successCount": 0,
      "totalCount": 0,
      "avgDurationMs": null,
      "checkedOutByUserId": null,
      "checkedOutByUserName": null,
      "checkedOutAt": null
    }
    """;
}

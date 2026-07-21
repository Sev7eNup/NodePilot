using FluentAssertions;
using NodePilot.Cli;
using NodePilot.Cli.Tests.Infra;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using Xunit;

namespace NodePilot.Cli.Tests;

/// <summary>
/// End-to-end command tests for the resource-CRUD branches (machines/credentials/globals/
/// users) and the workflow/exec endpoints added after the CLI's initial release. Same shape
/// as <see cref="CommandIntegrationTests"/> — primes WireMock, runs the command via
/// <see cref="CommandTestHarness"/>, asserts on exit code.
/// </summary>
[Collection(CommandTestCollection.Name)]
public class CommandIntegrationResourcesTests
{
    // ---- Workflow force-unlock + version + import-scorch + stats -----------

    [Fact]
    public void WorkflowForceUnlock_NonInteractive_BreaksLockReturnsZero()
    {
        using var h = new CommandTestHarness();
        var id = Guid.NewGuid();
        h.Server.Given(Request.Create().WithPath($"/api/workflows/{id}").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(Single(id, "Build")));
        h.Server.Given(Request.Create().WithPath($"/api/workflows/{id}/force-unlock").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(Single(id, "Build")));

        var result = h.Run("workflow", "force-unlock", id.ToString());
        result.ExitCode.Should().Be(ExitCodes.Success);
    }

    [Fact]
    public void WorkflowForceUnlock_Forbidden_ReturnsPermissionDenied()
    {
        using var h = new CommandTestHarness();
        var id = Guid.NewGuid();
        h.Server.Given(Request.Create().WithPath($"/api/workflows/{id}").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(Single(id, "Build")));
        h.Server.Given(Request.Create().WithPath($"/api/workflows/{id}/force-unlock").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(403));

        var result = h.Run("workflow", "force-unlock", id.ToString());
        result.ExitCode.Should().Be(ExitCodes.PermissionDenied);
    }

    [Fact]
    public void WorkflowVersionGet_RendersDetail()
    {
        using var h = new CommandTestHarness();
        var id = Guid.NewGuid();
        h.Server.Given(Request.Create().WithPath($"/api/workflows/{id}").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(Single(id, "Build")));
        h.Server.Given(Request.Create().WithPath($"/api/workflows/{id}/versions/2").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                version = 2, name = "Build", description = (string?)null,
                definitionJson = "{}", createdAt = DateTime.UtcNow, createdBy = "admin",
                changeNote = (string?)"hotfix", isCurrent = false,
            }));

        var result = h.Run("workflow", "version", id.ToString(), "2");
        result.ExitCode.Should().Be(ExitCodes.Success);
    }

    [Fact]
    public void WorkflowImportScorch_FromFile_ReturnsZero()
    {
        using var h = new CommandTestHarness();
        var path = Path.Combine(h.ConfigDir, "policy.ois_export");
        File.WriteAllText(path, "<Policy/>");
        h.Server.Given(Request.Create().WithPath("/api/workflows/import-scorch").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                created = 1,
                workflows = new[]
                {
                    new
                    {
                        id = Guid.NewGuid(), name = "Migrated", originalName = (string?)null,
                        activityCount = 3, heuristicCount = 0, fallbackCount = 0,
                    },
                },
                variables = Array.Empty<object>(),
                warnings = Array.Empty<string>(),
                errors = Array.Empty<string>(),
            }));

        var result = h.Run("workflow", "import-scorch", "--file", path);
        result.ExitCode.Should().Be(ExitCodes.Success);
    }

    [Fact]
    public void WorkflowImportScorch_FileMissing_ReturnsError()
    {
        using var h = new CommandTestHarness();
        var result = h.Run("workflow", "import-scorch", "--file", "missing.xml");
        result.ExitCode.Should().Be(ExitCodes.Error);
    }

    [Fact]
    public void WorkflowImportScorch_ServerReportsErrors_ReturnsError()
    {
        using var h = new CommandTestHarness();
        var path = Path.Combine(h.ConfigDir, "broken.ois_export");
        File.WriteAllText(path, "<Policy/>");
        h.Server.Given(Request.Create().WithPath("/api/workflows/import-scorch").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                created = 0,
                workflows = Array.Empty<object>(),
                variables = Array.Empty<object>(),
                warnings = Array.Empty<string>(),
                errors = new[] { "could not parse runbook 'A'" },
            }));

        var result = h.Run("workflow", "import-scorch", "--file", path);
        result.ExitCode.Should().Be(ExitCodes.Error);
    }

    [Fact]
    public void WorkflowStats_ResolvesByName_ReturnsZero()
    {
        using var h = new CommandTestHarness();
        var id = Guid.NewGuid();
        h.Server.Given(Request.Create().WithPath($"/api/workflows/{id}").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(Single(id, "Build")));
        h.Server.Given(Request.Create().WithPath($"/api/workflows/{id}/step-stats").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new Dictionary<string, object>
            {
                ["step-1"] = new
                {
                    totalRuns = 10, failedRuns = 1, failureRate = 0.1,
                    avgDurationMs = 200L, p95DurationMs = 500L, lastDurationMs = 180L,
                },
            }));

        var result = h.Run("workflow", "stats", id.ToString());
        result.ExitCode.Should().Be(ExitCodes.Success);
    }

    // ---- Exec resume / paused-steps ----------------------------------------

    [Fact]
    public void ExecResume_DefaultMode_ReturnsZero()
    {
        using var h = new CommandTestHarness();
        var id = Guid.NewGuid();
        h.Server.Given(Request.Create().WithPath($"/api/executions/{id}/resume").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(204));

        var result = h.Run("exec", "resume", id.ToString(), "--step", "step-1");
        result.ExitCode.Should().Be(ExitCodes.Success);
    }

    [Fact]
    public void ExecResume_MissingStep_ReturnsError()
    {
        using var h = new CommandTestHarness();
        var id = Guid.NewGuid();
        var result = h.Run("exec", "resume", id.ToString());
        result.ExitCode.Should().Be(ExitCodes.Error);
    }

    [Fact]
    public void ExecResume_BadOverride_ReturnsError()
    {
        using var h = new CommandTestHarness();
        var id = Guid.NewGuid();
        var result = h.Run("exec", "resume", id.ToString(), "--step", "step-1", "--override", "noequals");
        result.ExitCode.Should().Be(ExitCodes.Error);
    }

    [Fact]
    public void ExecResume_WithOverride_PassesAlong()
    {
        using var h = new CommandTestHarness();
        var id = Guid.NewGuid();
        h.Server.Given(Request.Create().WithPath($"/api/executions/{id}/resume").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(204));

        var result = h.Run("exec", "resume", id.ToString(), "--step", "step-1",
            "--mode", "stepOver", "--override", "env=prod");
        result.ExitCode.Should().Be(ExitCodes.Success);
    }

    [Fact]
    public void ExecPausedSteps_Empty_ReturnsZero()
    {
        using var h = new CommandTestHarness();
        var id = Guid.NewGuid();
        h.Server.Given(Request.Create().WithPath($"/api/executions/{id}/paused-steps").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(Array.Empty<string>()));

        var result = h.Run("exec", "paused-steps", id.ToString());
        result.ExitCode.Should().Be(ExitCodes.Success);
    }

    // ---- Machine -----------------------------------------------------------

    [Fact]
    public void MachineList_ReturnsZero()
    {
        using var h = new CommandTestHarness();
        h.Server.Given(Request.Create().WithPath("/api/machines").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(Array.Empty<object>()));

        var result = h.Run("machine", "list");
        result.ExitCode.Should().Be(ExitCodes.Success);
    }

    [Fact]
    public void MachineGet_ReturnsZero()
    {
        using var h = new CommandTestHarness();
        var id = Guid.NewGuid();
        h.Server.Given(Request.Create().WithPath($"/api/machines/{id}").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(Machine(id)));

        var result = h.Run("machine", "get", id.ToString());
        result.ExitCode.Should().Be(ExitCodes.Success);
    }

    [Fact]
    public void MachineCreate_MissingFields_ReturnsError()
    {
        using var h = new CommandTestHarness();
        var result = h.Run("machine", "create", "--name", "only-name");
        result.ExitCode.Should().Be(ExitCodes.Error);
    }

    [Fact]
    public void MachineCreate_AllFields_ReturnsZero()
    {
        using var h = new CommandTestHarness();
        var id = Guid.NewGuid();
        h.Server.Given(Request.Create().WithPath("/api/machines").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(201).WithBodyAsJson(Machine(id)));

        var result = h.Run("machine", "create", "--name", "win-01", "--hostname", "win-01.lab", "--ssl");
        result.ExitCode.Should().Be(ExitCodes.Success);
    }

    [Fact]
    public void MachineUpdate_PartialPatch_MergesWithGet()
    {
        using var h = new CommandTestHarness();
        var id = Guid.NewGuid();
        h.Server.Given(Request.Create().WithPath($"/api/machines/{id}").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(Machine(id)));
        h.Server.Given(Request.Create().WithPath($"/api/machines/{id}").UsingPut())
            .RespondWith(Response.Create().WithStatusCode(204));

        var result = h.Run("machine", "update", id.ToString(), "--hostname", "renamed.lab");
        result.ExitCode.Should().Be(ExitCodes.Success);
    }

    [Fact]
    public void MachineDelete_NonInteractive_DeletesWithoutPrompt()
    {
        using var h = new CommandTestHarness();
        var id = Guid.NewGuid();
        h.Server.Given(Request.Create().WithPath($"/api/machines/{id}").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(Machine(id)));
        h.Server.Given(Request.Create().WithPath($"/api/machines/{id}").UsingDelete())
            .RespondWith(Response.Create().WithStatusCode(204));

        var result = h.Run("machine", "delete", id.ToString());
        result.ExitCode.Should().Be(ExitCodes.Success);
    }

    [Fact]
    public void MachineTest_Success_ReturnsZero()
    {
        using var h = new CommandTestHarness();
        var id = Guid.NewGuid();
        h.Server.Given(Request.Create().WithPath($"/api/machines/{id}/test").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                success = true, computerName = "WIN-01",
                error = (string?)null, credentialUsed = "svc",
            }));

        var result = h.Run("machine", "test", id.ToString());
        result.ExitCode.Should().Be(ExitCodes.Success);
    }

    [Fact]
    public void MachineTest_Failure_ReturnsError()
    {
        using var h = new CommandTestHarness();
        var id = Guid.NewGuid();
        h.Server.Given(Request.Create().WithPath($"/api/machines/{id}/test").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                success = false, computerName = (string?)null,
                error = "WinRM unreachable", credentialUsed = "svc",
            }));

        var result = h.Run("machine", "test", id.ToString());
        result.ExitCode.Should().Be(ExitCodes.Error);
    }

    // ---- Credential --------------------------------------------------------

    [Fact]
    public void CredentialList_ReturnsZero()
    {
        using var h = new CommandTestHarness();
        h.Server.Given(Request.Create().WithPath("/api/credentials").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(Array.Empty<object>()));

        var result = h.Run("credential", "list");
        result.ExitCode.Should().Be(ExitCodes.Success);
    }

    [Fact]
    public void CredentialCreate_MissingPassword_ReturnsError()
    {
        using var h = new CommandTestHarness();
        var result = h.Run("credential", "create", "--name", "svc", "--username", "svc-build");
        result.ExitCode.Should().Be(ExitCodes.Error);
    }

    [Fact]
    public void CredentialCreate_WithPassword_ReturnsZero()
    {
        using var h = new CommandTestHarness();
        var id = Guid.NewGuid();
        h.Server.Given(Request.Create().WithPath("/api/credentials").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(201).WithBodyAsJson(new
            {
                id, name = "svc", username = "svc-build", domain = (string?)null,
            }));

        var result = h.Run("credential", "create",
            "--name", "svc", "--username", "svc-build", "--password", "pw12345678");
        result.ExitCode.Should().Be(ExitCodes.Success);
    }

    [Fact]
    public void CredentialUpdate_ChangesNameOnly_ReturnsZero()
    {
        using var h = new CommandTestHarness();
        var id = Guid.NewGuid();
        h.Server.Given(Request.Create().WithPath($"/api/credentials/{id}").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                id, name = "svc", username = "svc-build", domain = (string?)null,
            }));
        h.Server.Given(Request.Create().WithPath($"/api/credentials/{id}").UsingPut())
            .RespondWith(Response.Create().WithStatusCode(204));

        var result = h.Run("credential", "update", id.ToString(), "--name", "svc-renamed");
        result.ExitCode.Should().Be(ExitCodes.Success);
    }

    [Fact]
    public void CredentialCreate_WithExpires_PostsExpiresAt()
    {
        using var h = new CommandTestHarness();
        var id = Guid.NewGuid();
        h.Server.Given(Request.Create().WithPath("/api/credentials").UsingPost()
                .WithBody(b => b != null && b.Contains("expiresAt") && b.Contains("2026-12-31")))
            .RespondWith(Response.Create().WithStatusCode(201).WithBodyAsJson(new
            {
                id, name = "svc", username = "svc-build", domain = (string?)null, expiresAt = "2026-12-31T00:00:00Z",
            }));

        var result = h.Run("credential", "create",
            "--name", "svc", "--username", "svc-build", "--password", "pw12345678",
            "--expires", "2026-12-31");
        result.ExitCode.Should().Be(ExitCodes.Success);
    }

    [Fact]
    public void CredentialUpdate_NoExpires_ClearsExpiresAt()
    {
        using var h = new CommandTestHarness();
        var id = Guid.NewGuid();
        h.Server.Given(Request.Create().WithPath($"/api/credentials/{id}").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                id, name = "svc", username = "svc-build", domain = (string?)null, expiresAt = "2026-12-31T00:00:00Z",
            }));
        h.Server.Given(Request.Create().WithPath($"/api/credentials/{id}").UsingPut()
                .WithBody(b => b != null && b.Contains("\"expiresAt\":null")))
            .RespondWith(Response.Create().WithStatusCode(204));

        var result = h.Run("credential", "update", id.ToString(), "--no-expires");
        result.ExitCode.Should().Be(ExitCodes.Success);
    }

    [Fact]
    public void CredentialDelete_NonInteractive_ReturnsZero()
    {
        using var h = new CommandTestHarness();
        var id = Guid.NewGuid();
        h.Server.Given(Request.Create().WithPath($"/api/credentials/{id}").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                id, name = "svc", username = "svc-build", domain = (string?)null,
            }));
        h.Server.Given(Request.Create().WithPath($"/api/credentials/{id}").UsingDelete())
            .RespondWith(Response.Create().WithStatusCode(204));

        var result = h.Run("credential", "delete", id.ToString());
        result.ExitCode.Should().Be(ExitCodes.Success);
    }

    // ---- Globals -----------------------------------------------------------

    [Fact]
    public void GlobalsList_ReturnsZero()
    {
        using var h = new CommandTestHarness();
        h.Server.Given(Request.Create().WithPath("/api/global-variables").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(Array.Empty<object>()));

        var result = h.Run("globals", "list");
        result.ExitCode.Should().Be(ExitCodes.Success);
    }

    [Fact]
    public void GlobalsCreate_PlainValue_ReturnsZero()
    {
        using var h = new CommandTestHarness();
        var id = Guid.NewGuid();
        h.Server.Given(Request.Create().WithPath("/api/global-variables").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(201).WithBodyAsJson(new
            {
                id, name = "ENV", value = "stg", isSecret = false, description = (string?)null,
                createdAt = DateTime.UtcNow, updatedAt = DateTime.UtcNow, updatedBy = (string?)null,
            }));

        var result = h.Run("globals", "create", "--name", "ENV", "--value", "stg");
        result.ExitCode.Should().Be(ExitCodes.Success);
    }

    [Fact]
    public void GlobalsCreate_MissingValue_ReturnsError()
    {
        using var h = new CommandTestHarness();
        var result = h.Run("globals", "create", "--name", "ENV");
        result.ExitCode.Should().Be(ExitCodes.Error);
    }

    [Fact]
    public void GlobalsUpdate_FoundsViaList_ReturnsZero()
    {
        using var h = new CommandTestHarness();
        var id = Guid.NewGuid();
        h.Server.Given(Request.Create().WithPath("/api/global-variables").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new[]
            {
                new
                {
                    id, name = "ENV", value = "stg", isSecret = false, description = (string?)null,
                    createdAt = DateTime.UtcNow, updatedAt = DateTime.UtcNow, updatedBy = (string?)null,
                },
            }));
        h.Server.Given(Request.Create().WithPath($"/api/global-variables/{id}").UsingPut())
            .RespondWith(Response.Create().WithStatusCode(204));

        var result = h.Run("globals", "update", id.ToString(), "--value", "prod");
        result.ExitCode.Should().Be(ExitCodes.Success);
    }

    [Fact]
    public void GlobalsDelete_NonInteractive_ReturnsZero()
    {
        using var h = new CommandTestHarness();
        var id = Guid.NewGuid();
        h.Server.Given(Request.Create().WithPath($"/api/global-variables/{id}").UsingDelete())
            .RespondWith(Response.Create().WithStatusCode(204));

        var result = h.Run("globals", "delete", id.ToString());
        result.ExitCode.Should().Be(ExitCodes.Success);
    }

    [Fact]
    public void GlobalsFolderList_ReturnsZero()
    {
        using var h = new CommandTestHarness();
        h.Server.Given(Request.Create().WithPath("/api/global-variable-folders").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new[]
            {
                new { id = Guid.NewGuid(), parentFolderId = (Guid?)null, name = "Root", path = "/", depth = 0, createdAt = DateTime.UtcNow, createdByUserId = (Guid?)null, variableCount = 0 },
            }));

        var result = h.Run("globals", "folder", "list");
        result.ExitCode.Should().Be(ExitCodes.Success);
    }

    [Fact]
    public void GlobalsFolderCreate_UnderRoot_ReturnsZero()
    {
        using var h = new CommandTestHarness();
        h.Server.Given(Request.Create().WithPath("/api/global-variable-folders").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(201).WithBodyAsJson(new
            {
                id = Guid.NewGuid(), parentFolderId = (Guid?)null, name = "Databases", path = "/Databases", depth = 1,
                createdAt = DateTime.UtcNow, createdByUserId = (Guid?)null, variableCount = 0,
            }));

        var result = h.Run("globals", "folder", "create", "--name", "Databases");
        result.ExitCode.Should().Be(ExitCodes.Success);
    }

    [Fact]
    public void GlobalsFolderCreate_ParentByPath_ResolvesAndReturnsZero()
    {
        using var h = new CommandTestHarness();
        var parentId = Guid.NewGuid();
        // The resolver lists folders to turn "/Environment" into the parent id.
        h.Server.Given(Request.Create().WithPath("/api/global-variable-folders").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new[]
            {
                new { id = parentId, parentFolderId = (Guid?)null, name = "Environment", path = "/Environment", depth = 1, createdAt = DateTime.UtcNow, createdByUserId = (Guid?)null, variableCount = 0 },
            }));
        h.Server.Given(Request.Create().WithPath("/api/global-variable-folders").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(201).WithBodyAsJson(new
            {
                id = Guid.NewGuid(), parentFolderId = parentId, name = "Prod", path = "/Environment/Prod", depth = 2,
                createdAt = DateTime.UtcNow, createdByUserId = (Guid?)null, variableCount = 0,
            }));

        var result = h.Run("globals", "folder", "create", "--name", "Prod", "--parent", "/Environment");
        result.ExitCode.Should().Be(ExitCodes.Success);
    }

    [Fact]
    public void GlobalsMoveVariable_ByFolderId_ReturnsZero()
    {
        using var h = new CommandTestHarness();
        var id = Guid.NewGuid();
        var folderId = Guid.NewGuid();
        h.Server.Given(Request.Create().WithPath($"/api/global-variables/{id}/move-folder").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(204));

        var result = h.Run("globals", "move-folder", id.ToString(), "--folder", folderId.ToString());
        result.ExitCode.Should().Be(ExitCodes.Success);
    }

    [Fact]
    public void GlobalsExport_WritesToStdout_ReturnsZero()
    {
        using var h = new CommandTestHarness();
        var id = Guid.NewGuid();
        h.Server.Given(Request.Create().WithPath("/api/global-variables").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new[]
            {
                new { id, name = "SMTP_HOST", value = "mail.example.com", isSecret = false, description = "smtp", createdAt = DateTime.UtcNow, updatedAt = DateTime.UtcNow, updatedBy = (string?)null },
            }));

        var result = h.Run("globals", "export");
        result.ExitCode.Should().Be(ExitCodes.Success);
    }

    [Fact]
    public void GlobalsExport_WritesToFile_ReturnsZero()
    {
        using var h = new CommandTestHarness();
        var tmp = Path.Combine(Path.GetTempPath(), $"np-globals-test-{Guid.NewGuid()}.json");
        try
        {
            h.Server.Given(Request.Create().WithPath("/api/global-variables").UsingGet())
                .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new[]
                {
                    new { id = Guid.NewGuid(), name = "ENV", value = "stg", isSecret = false, description = (string?)null, createdAt = DateTime.UtcNow, updatedAt = DateTime.UtcNow, updatedBy = (string?)null },
                }));

            var result = h.Run("globals", "export", "--file", tmp);
            result.ExitCode.Should().Be(ExitCodes.Success);
            File.Exists(tmp).Should().BeTrue();
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }

    [Fact]
    public void GlobalsImport_Create_ReturnsZero()
    {
        using var h = new CommandTestHarness();
        var tmp = Path.Combine(Path.GetTempPath(), $"np-globals-import-{Guid.NewGuid()}.json");
        try
        {
            File.WriteAllText(tmp, """[{"name":"NEW_VAR","value":"hello","isSecret":false,"description":null}]""");
            // List returns empty → all entries will be created
            h.Server.Given(Request.Create().WithPath("/api/global-variables").UsingGet())
                .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(Array.Empty<object>()));
            h.Server.Given(Request.Create().WithPath("/api/global-variables").UsingPost())
                .RespondWith(Response.Create().WithStatusCode(201).WithBodyAsJson(new
                {
                    id = Guid.NewGuid(), name = "NEW_VAR", value = "hello", isSecret = false,
                    description = (string?)null, createdAt = DateTime.UtcNow, updatedAt = DateTime.UtcNow, updatedBy = (string?)null,
                }));

            var result = h.Run("globals", "import", "--file", tmp);
            result.ExitCode.Should().Be(ExitCodes.Success);
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }

    [Fact]
    public void GlobalsImport_Skip_ExistingWithoutUpsert_ReturnsZero()
    {
        using var h = new CommandTestHarness();
        var id = Guid.NewGuid();
        var tmp = Path.Combine(Path.GetTempPath(), $"np-globals-import-skip-{Guid.NewGuid()}.json");
        try
        {
            File.WriteAllText(tmp, """[{"name":"ENV","value":"stg","isSecret":false,"description":null}]""");
            h.Server.Given(Request.Create().WithPath("/api/global-variables").UsingGet())
                .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new[]
                {
                    new { id, name = "ENV", value = "stg", isSecret = false, description = (string?)null, createdAt = DateTime.UtcNow, updatedAt = DateTime.UtcNow, updatedBy = (string?)null },
                }));
            // PUT must NOT be called — no stub registered, WireMock would return 404 if hit

            var result = h.Run("globals", "import", "--file", tmp);
            result.ExitCode.Should().Be(ExitCodes.Success);
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }

    [Fact]
    public void GlobalsImport_Upsert_ExistingEntry_ReturnsZero()
    {
        using var h = new CommandTestHarness();
        var id = Guid.NewGuid();
        var tmp = Path.Combine(Path.GetTempPath(), $"np-globals-import-upsert-{Guid.NewGuid()}.json");
        try
        {
            File.WriteAllText(tmp, """[{"name":"ENV","value":"prod","isSecret":false,"description":null}]""");
            h.Server.Given(Request.Create().WithPath("/api/global-variables").UsingGet())
                .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new[]
                {
                    new { id, name = "ENV", value = "stg", isSecret = false, description = (string?)null, createdAt = DateTime.UtcNow, updatedAt = DateTime.UtcNow, updatedBy = (string?)null },
                }));
            h.Server.Given(Request.Create().WithPath($"/api/global-variables/{id}").UsingPut())
                .RespondWith(Response.Create().WithStatusCode(204));

            var result = h.Run("globals", "import", "--file", tmp, "--upsert");
            result.ExitCode.Should().Be(ExitCodes.Success);
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }

    [Fact]
    public void GlobalsImport_DryRun_MakesNoApiCalls()
    {
        using var h = new CommandTestHarness();
        var tmp = Path.Combine(Path.GetTempPath(), $"np-globals-import-dry-{Guid.NewGuid()}.json");
        try
        {
            File.WriteAllText(tmp, """[{"name":"NEW_VAR","value":"hello","isSecret":false,"description":null}]""");
            // No server stubs registered — any real API call would throw a connection error

            var result = h.Run("globals", "import", "--file", tmp, "--dry-run");
            result.ExitCode.Should().Be(ExitCodes.Success);
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }

    [Fact]
    public void GlobalsImport_MissingFile_ReturnsError()
    {
        using var h = new CommandTestHarness();
        var result = h.Run("globals", "import", "--file", "/nonexistent/path/globals.json");
        result.ExitCode.Should().Be(ExitCodes.Error);
    }

    // ---- Users -------------------------------------------------------------

    [Fact]
    public void UserList_ReturnsZero()
    {
        using var h = new CommandTestHarness();
        h.Server.Given(Request.Create().WithPath("/api/users").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(Array.Empty<object>()));

        var result = h.Run("user", "list");
        result.ExitCode.Should().Be(ExitCodes.Success);
    }

    [Fact]
    public void UserCreate_AllFields_ReturnsZero()
    {
        using var h = new CommandTestHarness();
        var id = Guid.NewGuid();
        h.Server.Given(Request.Create().WithPath("/api/users").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(201).WithBodyAsJson(new
            {
                id, username = "alice", role = "Operator", isActive = true, createdAt = DateTime.UtcNow,
            }));

        var result = h.Run("user", "create",
            "--username", "alice", "--password", "pw12345678", "--role", "Operator");
        result.ExitCode.Should().Be(ExitCodes.Success);
    }

    [Fact]
    public void UserCreate_MissingFields_ReturnsError()
    {
        using var h = new CommandTestHarness();
        var result = h.Run("user", "create", "--username", "alice");
        result.ExitCode.Should().Be(ExitCodes.Error);
    }

    [Fact]
    public void UserUpdate_NoChanges_ReturnsError()
    {
        using var h = new CommandTestHarness();
        var id = Guid.NewGuid();
        var result = h.Run("user", "update", id.ToString());
        result.ExitCode.Should().Be(ExitCodes.Error);
    }

    [Fact]
    public void UserUpdate_RoleChange_ReturnsZero()
    {
        using var h = new CommandTestHarness();
        var id = Guid.NewGuid();
        h.Server.Given(Request.Create().WithPath($"/api/users/{id}").UsingPut())
            .RespondWith(Response.Create().WithStatusCode(204));

        var result = h.Run("user", "update", id.ToString(), "--role", "Viewer");
        result.ExitCode.Should().Be(ExitCodes.Success);
    }

    [Fact]
    public void UserDelete_NonInteractive_ReturnsZero()
    {
        using var h = new CommandTestHarness();
        var id = Guid.NewGuid();
        h.Server.Given(Request.Create().WithPath($"/api/users/{id}").UsingDelete())
            .RespondWith(Response.Create().WithStatusCode(204));

        var result = h.Run("user", "delete", id.ToString());
        result.ExitCode.Should().Be(ExitCodes.Success);
    }

    // ---- Dashboard / Observability -----------------------------------------

    [Fact]
    public void Dashboard_RendersSummary()
    {
        using var h = new CommandTestHarness();
        h.Server.Given(Request.Create().WithPath("/api/stats/dashboard").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                workflowsTotal = 1, workflowsEnabled = 1,
                machinesTotal = 1, machinesReachable = 1,
                executionsTotal = 1,
                last24h = new { total = 0, succeeded = 0, failed = 0, running = 0, cancelled = 0 },
                last24hBuckets = Array.Empty<object>(),
                topWorkflows = Array.Empty<object>(),
                running = Array.Empty<object>(),
                recent = Array.Empty<object>(),
                armedTriggers = Array.Empty<object>(),
            }));

        var result = h.Run("dashboard");
        result.ExitCode.Should().Be(ExitCodes.Success);
    }

    [Fact]
    public void ObservabilitySummary_NotConfigured_ReturnsZero()
    {
        using var h = new CommandTestHarness();
        h.Server.Given(Request.Create().WithPath("/api/observability/summary").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                available = false, panels = Array.Empty<object>(),
            }));

        var result = h.Run("observability", "summary");
        result.ExitCode.Should().Be(ExitCodes.Success);
    }

    // ---- Sample bodies -----------------------------------------------------

    private static object Machine(Guid id) => new
    {
        id, name = "win-01", hostname = "win-01.lab",
        winRmPort = 5985, useSsl = false,
        defaultCredentialId = (Guid?)null, tags = (string?)null,
        lastConnectivityCheck = (DateTime?)DateTime.UtcNow, isReachable = true,
    };

    private static string Single(Guid id, string name) => $$"""
    { "id": "{{id}}", "name": "{{name}}", "description": null,
      "definitionJson": "{}", "version": 1, "isEnabled": false,
      "createdAt": "2026-04-01T00:00:00Z", "updatedAt": "2026-04-01T00:00:00Z",
      "createdBy": null, "updatedBy": null, "activityCount": 1, "triggerTypes": [],
      "lastExecution": null, "successCount": 0, "totalCount": 0, "avgDurationMs": null,
      "checkedOutByUserId": null, "checkedOutByUserName": null, "checkedOutAt": null }
    """;
}

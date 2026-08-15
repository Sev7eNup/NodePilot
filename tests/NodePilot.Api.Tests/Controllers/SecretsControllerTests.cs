using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NodePilot.Api.Controllers;
using NodePilot.Api.Dtos;
using NodePilot.Api.Services;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.Data.Security;
using NodePilot.Api.Tests.TestSupport;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Api.Tests.Controllers;

/// <summary>
/// Behaviour-level tests for the bulk re-encrypt endpoint. The controller is thin —
/// it forwards to two store methods and assembles a response — but the response shape
/// is the API contract operators script against (CI / Ansible parses
/// <c>partialSuccess</c> + the skip arrays). The tests pin both the happy path
/// (200 + partialSuccess=false) and the partial-success path (207 + skipped names
/// surfaced) because earlier the controller silently dropped skip information and
/// returned a misleading 200.
/// </summary>
public class SecretsControllerTests
{
    private static SecretsController Build(
        ReencryptionSummary credResult,
        ReencryptionSummary globalsResult,
        NodePilotDbContext db,
        out Mock<ICredentialStore> credMock,
        out Mock<IGlobalVariableStore> globalsMock,
        WorkflowVersionDefinitionProtector? versionDefinitions = null)
    {
        credMock = new Mock<ICredentialStore>();
        credMock.Setup(s => s.ReencryptAllCredentialsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(credResult);
        globalsMock = new Mock<IGlobalVariableStore>();
        globalsMock.Setup(s => s.ReencryptAllSecretsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(globalsResult);
        return new SecretsController(
            credMock.Object, globalsMock.Object, db,
            versionDefinitions ?? VersionDefinitions(), NoopAuditWriter.Instance);
    }

    private static WorkflowVersionDefinitionProtector VersionDefinitions() =>
        new(new AesGcmSecretProtector(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray()),
            NullLogger<WorkflowVersionDefinitionProtector>.Instance);

    [Fact]
    public async Task Reencrypt_AllCleanSuccess_Returns200_WithPartialSuccessFalse()
    {
        using var db = TestDbFactory.Create();
        var ctrl = Build(
            new ReencryptionSummary(Rewritten: 47, Skipped: 0, SkippedDetails: Array.Empty<ReencryptionSkip>()),
            new ReencryptionSummary(Rewritten: 12, Skipped: 0, SkippedDetails: Array.Empty<ReencryptionSkip>()),
            db,
            out _, out _);

        var result = await ctrl.Reencrypt(CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var body = ok.Value.Should().BeOfType<ReencryptResult>().Subject;
        body.CredentialsRewritten.Should().Be(47);
        body.GlobalSecretsRewritten.Should().Be(12);
        body.WorkflowVersionsRewritten.Should().Be(0);
        body.PartialSuccess.Should().BeFalse(
            "every row converted cleanly — operator should see 200 + a clean partialSuccess=false");
    }

    [Fact]
    public async Task Reencrypt_SomeRowsSkipped_Returns207_WithDetails()
    {
        using var db = TestDbFactory.Create();
        var brokenCredId = Guid.NewGuid();
        var brokenGlobalId = Guid.NewGuid();
        var ctrl = Build(
            new ReencryptionSummary(
                Rewritten: 5,
                Skipped: 1,
                SkippedDetails: new[] { new ReencryptionSkip(brokenCredId, "broken-svc", "CryptographicException") }),
            new ReencryptionSummary(
                Rewritten: 3,
                Skipped: 1,
                SkippedDetails: new[] { new ReencryptionSkip(brokenGlobalId, "STRIPE_KEY", "FormatException") }),
            db,
            out _, out _);

        var result = await ctrl.Reencrypt(CancellationToken.None);

        var status = result.Result.Should().BeOfType<ObjectResult>().Subject;
        status.StatusCode.Should().Be(StatusCodes.Status207MultiStatus,
            "partial success must use 207 so CI / Ansible can branch on the status line " +
            "without parsing the body — 200 would falsely signal a clean migration");

        var body = status.Value.Should().BeOfType<ReencryptResult>().Subject;
        body.PartialSuccess.Should().BeTrue();
        body.CredentialsSkipped.Should().Be(1);
        body.GlobalSecretsSkipped.Should().Be(1);
        body.CredentialSkipDetails.Should().ContainSingle(s => s.Id == brokenCredId && s.Name == "broken-svc");
        body.GlobalSecretSkipDetails.Should().ContainSingle(s => s.Id == brokenGlobalId && s.Name == "STRIPE_KEY");
    }

    [Fact]
    public async Task Reencrypt_NothingToDo_Returns200_WithZeros()
    {
        using var db = TestDbFactory.Create();
        // Empty deployment (or already-fully-migrated): both sweeps return zeros.
        // Still a clean success — no skips to flag.
        var ctrl = Build(
            new ReencryptionSummary(0, 0, Array.Empty<ReencryptionSkip>()),
            new ReencryptionSummary(0, 0, Array.Empty<ReencryptionSkip>()),
            db,
            out _, out _);

        var result = await ctrl.Reencrypt(CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var body = ok.Value.Should().BeOfType<ReencryptResult>().Subject;
        body.CredentialsRewritten.Should().Be(0);
        body.GlobalSecretsRewritten.Should().Be(0);
        body.WorkflowVersionsRewritten.Should().Be(0);
        body.PartialSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task Reencrypt_CallsBothStores_OnceEach()
    {
        using var db = TestDbFactory.Create();
        // Pin the contract: the endpoint MUST sweep both surfaces. A regression that
        // forgot one would silently leave half the rotation incomplete.
        var ctrl = Build(
            new ReencryptionSummary(1, 0, Array.Empty<ReencryptionSkip>()),
            new ReencryptionSummary(1, 0, Array.Empty<ReencryptionSkip>()),
            db,
            out var credMock, out var globalsMock);

        await ctrl.Reencrypt(CancellationToken.None);

        credMock.Verify(s => s.ReencryptAllCredentialsAsync(It.IsAny<CancellationToken>()), Times.Once);
        globalsMock.Verify(s => s.ReencryptAllSecretsAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Reencrypt_IncludesWorkflowHistoryInAdditiveCounters()
    {
        using var db = TestDbFactory.Create();
        const string legacyDefinition =
            """{"nodes":[{"data":{"config":{"script":"Write-Output 'history-literal'"}}}],"edges":[]}""";
        var workflow = new Workflow { Id = Guid.NewGuid(), Name = "wf", DefinitionJson = "{}" };
        db.Workflows.Add(workflow);
        db.WorkflowVersions.Add(new WorkflowVersion
        {
            Id = Guid.NewGuid(), WorkflowId = workflow.Id, Version = 1, Name = workflow.Name,
            DefinitionJson = legacyDefinition,
        });
        await db.SaveChangesAsync();
        var versionDefinitions = VersionDefinitions();
        var ctrl = Build(
            new ReencryptionSummary(0, 0, Array.Empty<ReencryptionSkip>()),
            new ReencryptionSummary(0, 0, Array.Empty<ReencryptionSkip>()),
            db, out _, out _, versionDefinitions);

        var result = await ctrl.Reencrypt(CancellationToken.None);

        var body = result.Result.Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeOfType<ReencryptResult>().Subject;
        body.WorkflowVersionsRewritten.Should().Be(1);
        body.WorkflowVersionsSkipped.Should().Be(0);
        db.ChangeTracker.Clear();
        var stored = db.WorkflowVersions.Single().DefinitionJson;
        stored.Should().NotContain("history-literal");
        versionDefinitions.Unprotect(stored).Should().Be(legacyDefinition);
    }
}

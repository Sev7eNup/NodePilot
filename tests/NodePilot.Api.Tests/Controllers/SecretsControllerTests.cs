using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using NodePilot.Api.Controllers;
using NodePilot.Api.Dtos;
using NodePilot.Core.Interfaces;
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
        out Mock<ICredentialStore> credMock,
        out Mock<IGlobalVariableStore> globalsMock)
    {
        credMock = new Mock<ICredentialStore>();
        credMock.Setup(s => s.ReencryptAllCredentialsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(credResult);
        globalsMock = new Mock<IGlobalVariableStore>();
        globalsMock.Setup(s => s.ReencryptAllSecretsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(globalsResult);
        return new SecretsController(credMock.Object, globalsMock.Object, NoopAuditWriter.Instance);
    }

    [Fact]
    public async Task Reencrypt_AllCleanSuccess_Returns200_WithPartialSuccessFalse()
    {
        var ctrl = Build(
            new ReencryptionSummary(Rewritten: 47, Skipped: 0, SkippedDetails: Array.Empty<ReencryptionSkip>()),
            new ReencryptionSummary(Rewritten: 12, Skipped: 0, SkippedDetails: Array.Empty<ReencryptionSkip>()),
            out _, out _);

        var result = await ctrl.Reencrypt(CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var body = ok.Value.Should().BeOfType<ReencryptResult>().Subject;
        body.CredentialsRewritten.Should().Be(47);
        body.GlobalSecretsRewritten.Should().Be(12);
        body.PartialSuccess.Should().BeFalse(
            "every row converted cleanly — operator should see 200 + a clean partialSuccess=false");
    }

    [Fact]
    public async Task Reencrypt_SomeRowsSkipped_Returns207_WithDetails()
    {
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
        // Empty deployment (or already-fully-migrated): both sweeps return zeros.
        // Still a clean success — no skips to flag.
        var ctrl = Build(
            new ReencryptionSummary(0, 0, Array.Empty<ReencryptionSkip>()),
            new ReencryptionSummary(0, 0, Array.Empty<ReencryptionSkip>()),
            out _, out _);

        var result = await ctrl.Reencrypt(CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var body = ok.Value.Should().BeOfType<ReencryptResult>().Subject;
        body.CredentialsRewritten.Should().Be(0);
        body.GlobalSecretsRewritten.Should().Be(0);
        body.PartialSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task Reencrypt_CallsBothStores_OnceEach()
    {
        // Pin the contract: the endpoint MUST sweep both surfaces. A regression that
        // forgot one would silently leave half the rotation incomplete.
        var ctrl = Build(
            new ReencryptionSummary(1, 0, Array.Empty<ReencryptionSkip>()),
            new ReencryptionSummary(1, 0, Array.Empty<ReencryptionSkip>()),
            out var credMock, out var globalsMock);

        await ctrl.Reencrypt(CancellationToken.None);

        credMock.Verify(s => s.ReencryptAllCredentialsAsync(It.IsAny<CancellationToken>()), Times.Once);
        globalsMock.Verify(s => s.ReencryptAllSecretsAsync(It.IsAny<CancellationToken>()), Times.Once);
    }
}

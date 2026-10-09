using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NodePilot.Api.Configuration;
using NodePilot.Api.Controllers;
using NodePilot.Api.Dtos;
using NodePilot.Api.Services;
using NodePilot.Api.Tests.TestSupport;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Data.Security;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Api.Tests.Controllers;

public sealed class SecretRotationCoverageTests
{
    private static readonly byte[] OldKey = Enumerable.Repeat((byte)19, 32).ToArray();
    private static readonly byte[] NewKey = Enumerable.Repeat((byte)27, 32).ToArray();

    [Fact]
    public async Task Reencrypt_BrokenAdditionalFamilies_Returns207WithEverySkip()
    {
        using var db = TestDbFactory.Create();
        await Services.DatabaseSecretRotationTests.Seed(db, dispatch: false, [1, 2, 3]);
        await Services.DatabaseSecretRotationTests.Seed(db, dispatch: true, [1, 2, 3]);
        var path = Path.Combine(Path.GetTempPath(), "nodepilot-rotation-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, "{\"Password\":\"enc:v1:bad-base64\"}");
            var protector = new AesGcmSecretProtector(NewKey);
            var credentials = new Mock<ICredentialStore>();
            credentials.Setup(x => x.ReencryptAllCredentialsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new ReencryptionSummary(0, 0, []));
            var globals = new Mock<IGlobalVariableStore>();
            globals.Setup(x => x.ReencryptAllSecretsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new ReencryptionSummary(0, 0, []));
            var controller = new SecretsController(credentials.Object, globals.Object, db,
                new WorkflowVersionDefinitionProtector(protector, NullLogger<WorkflowVersionDefinitionProtector>.Instance),
                NoopAuditWriter.Instance, protector, new RuntimeOverridesWriter(path, NullLogger<RuntimeOverridesWriter>.Instance));

            var response = (await controller.Reencrypt(CancellationToken.None)).Result.Should().BeOfType<ObjectResult>().Subject;

            response.StatusCode.Should().Be(207);
            var body = response.Value.Should().BeOfType<ReencryptResult>().Subject;
            body.PartialSuccess.Should().BeTrue();
            body.NotificationRoutesSkipped.Should().Be(1);
            body.DispatchParametersSkipped.Should().Be(1);
            body.RuntimeSettingsFilesSkipped.Should().Be(1);
            body.NotificationRouteSkipDetails.Should().HaveCount(1);
            body.DispatchParameterSkipDetails.Should().HaveCount(1);
            body.RuntimeSettingsFileSkipDetails.Should().HaveCount(1);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void BootstrapDuringRotation_LoadsRuntimeCiphertextWithLegacyKey()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Secrets:Provider"] = "AesGcm", ["Secrets:MasterKey"] = Convert.ToBase64String(NewKey),
            ["Secrets:LegacyProvider"] = "AesGcm", ["Secrets:LegacyMasterKey"] = Convert.ToBase64String(OldKey),
        }).Build();
        var protector = SecretProtectorBootstrapFactory.FromConfigSnapshot(config);
        var encrypted = EncryptingJsonConfigurationProvider.EncryptForPersist("smtp-password", new AesGcmSecretProtector(OldKey));
        var provider = new EncryptingJsonConfigurationProvider(new EncryptingJsonConfigurationSource(
            "unused.json", protector, optional: false, reloadOnChange: false));
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(
            new JsonObject { ["Smtp"] = new JsonObject { ["Password"] = encrypted } }.ToJsonString()));

        provider.Load(stream);

        provider.TryGet("Smtp:Password", out var value).Should().BeTrue();
        value.Should().Be("smtp-password");
        new AesGcmSecretProtector(NewKey).Unprotect(protector.Protect("next-secret")).Should().Be("next-secret");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reencrypt_RoutesAndPendingParameters_AreReadableAfterLegacyKeyRemoval(bool dispatch)
    {
        using var db = TestDbFactory.Create();
        var old = new AesGcmSecretProtector(OldKey);
        var current = new AesGcmSecretProtector(NewKey);
        var migrating = new MigratingSecretProtector(current, old);
        var id = Guid.NewGuid();
        if (dispatch)
        {
            var workflow = new Workflow { Id = Guid.NewGuid(), Name = "pending-workflow", DefinitionJson = "{}" };
            db.Workflows.Add(workflow);
            db.WorkflowExecutions.Add(new WorkflowExecution { Id = id, WorkflowId = workflow.Id });
            db.ExecutionDispatchOutbox.Add(new ExecutionDispatchOutboxItem
            {
                ExecutionId = id, WorkflowId = workflow.Id, ProtectedParameters = old.Protect("{\"password\":\"secret\"}"),
            });
        }
        else
        {
            var rule = new NotificationRule { Id = Guid.NewGuid(), Name = "rotation-rule" };
            db.NotificationRules.Add(rule);
            db.NotificationRoutes.Add(new NotificationRoute
            {
                Id = id, NotificationRuleId = rule.Id, Secret = Convert.ToBase64String(old.Protect("route-secret")),
            });
        }
        await db.SaveChangesAsync();
        var credentials = new Mock<ICredentialStore>();
        credentials.Setup(x => x.ReencryptAllCredentialsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new ReencryptionSummary(0, 0, []));
        var globals = new Mock<IGlobalVariableStore>();
        globals.Setup(x => x.ReencryptAllSecretsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new ReencryptionSummary(0, 0, []));
        var controller = new SecretsController(credentials.Object, globals.Object, db,
            new WorkflowVersionDefinitionProtector(migrating, NullLogger<WorkflowVersionDefinitionProtector>.Instance),
            NoopAuditWriter.Instance, migrating,
            new RuntimeOverridesWriter(Path.Combine(Path.GetTempPath(), "nodepilot-rotation-" + Guid.NewGuid().ToString("N"), "runtime.json"),
                NullLogger<RuntimeOverridesWriter>.Instance));

        (await controller.Reencrypt(CancellationToken.None)).Result.Should().BeOfType<OkObjectResult>();

        db.ChangeTracker.Clear();
        var stored = dispatch ? db.ExecutionDispatchOutbox.Single().ProtectedParameters!
            : Convert.FromBase64String(db.NotificationRoutes.Single().Secret!);
        current.Unprotect(stored).Should().Be(dispatch ? "{\"password\":\"secret\"}" : "route-secret");
    }
}

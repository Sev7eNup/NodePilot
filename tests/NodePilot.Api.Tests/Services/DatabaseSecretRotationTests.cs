using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using NodePilot.Api.Services;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.Data.Security;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Api.Tests.Services;

public sealed class DatabaseSecretRotationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentCiphertextChange_IsNotOverwritten_AndIsReportedAsSkip(bool dispatch)
    {
        using var db = TestDbFactory.Create();
        var old = new AesGcmSecretProtector(Enumerable.Repeat((byte)3, 32).ToArray());
        var current = new AesGcmSecretProtector(Enumerable.Repeat((byte)9, 32).ToArray());
        var id = await Seed(db, dispatch, old.Protect("old"));
        var replacement = current.Protect("concurrent-value");
        var protector = new Mock<ISecretProtector>();
        protector.Setup(x => x.Unprotect(It.IsAny<byte[]>())).Returns((byte[] bytes) =>
        {
            if (dispatch)
                db.ExecutionDispatchOutbox.Where(x => x.ExecutionId == id)
                    .ExecuteUpdate(s => s.SetProperty(x => x.ProtectedParameters, replacement));
            else
                db.NotificationRoutes.Where(x => x.Id == id)
                    .ExecuteUpdate(s => s.SetProperty(x => x.Secret, Convert.ToBase64String(replacement)));
            return old.Unprotect(bytes);
        });
        protector.Setup(x => x.Protect(It.IsAny<string>())).Returns((string value) => current.Protect(value));

        var result = await Rotate(db, dispatch, protector.Object, CancellationToken.None);

        result.Rewritten.Should().Be(0);
        result.SkippedDetails.Should().ContainSingle(x => x.Id == id && x.Reason == "ConcurrentUpdate");
        db.ChangeTracker.Clear();
        current.Unprotect(dispatch ? db.ExecutionDispatchOutbox.Single().ProtectedParameters!
            : Convert.FromBase64String(db.NotificationRoutes.Single().Secret!)).Should().Be("concurrent-value");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BadCiphertext_DoesNotStopLaterPages_AndErrorsDoNotContainSecretText(bool dispatch)
    {
        using var db = TestDbFactory.Create();
        var protector = new AesGcmSecretProtector(Enumerable.Repeat((byte)4, 32).ToArray());
        for (var i = 0; i < 102; i++) await Seed(db, dispatch, i == 50 ? [1, 2, 3] : protector.Protect("secret"));

        var result = await Rotate(db, dispatch, protector, CancellationToken.None);

        result.Rewritten.Should().Be(101);
        result.Skipped.Should().Be(1);
        result.SkippedDetails.Single().Reason.Should().Be("CryptographicException");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CallerCancellation_IsNotConvertedIntoSuccessfulRotation(bool dispatch)
    {
        using var db = TestDbFactory.Create();
        var inner = new AesGcmSecretProtector(Enumerable.Repeat((byte)3, 32).ToArray());
        var original = inner.Protect("original");
        await Seed(db, dispatch, original);
        using var cts = new CancellationTokenSource();
        var protector = new Mock<ISecretProtector>();
        protector.Setup(x => x.Unprotect(It.IsAny<byte[]>())).Returns((byte[] bytes) => { cts.Cancel(); return inner.Unprotect(bytes); });
        protector.Setup(x => x.Protect(It.IsAny<string>())).Returns((string value) => inner.Protect(value));

        var act = () => Rotate(db, dispatch, protector.Object, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        db.ChangeTracker.Clear();
        (dispatch ? db.ExecutionDispatchOutbox.Single().ProtectedParameters!
            : Convert.FromBase64String(db.NotificationRoutes.Single().Secret!)).Should().Equal(original);
    }

    internal static async Task<Guid> Seed(NodePilotDbContext db, bool dispatch, byte[] cipher)
    {
        var id = Guid.NewGuid();
        if (dispatch)
        {
            var workflow = new Workflow { Id = Guid.NewGuid(), Name = "pending", DefinitionJson = "{}" };
            db.Workflows.Add(workflow);
            db.WorkflowExecutions.Add(new WorkflowExecution { Id = id, WorkflowId = workflow.Id });
            db.ExecutionDispatchOutbox.Add(new ExecutionDispatchOutboxItem { ExecutionId = id, WorkflowId = workflow.Id, ProtectedParameters = cipher });
        }
        else
        {
            var rule = new NotificationRule { Id = Guid.NewGuid(), Name = "route-" + id };
            db.NotificationRules.Add(rule);
            db.NotificationRoutes.Add(new NotificationRoute { Id = id, NotificationRuleId = rule.Id, Secret = Convert.ToBase64String(cipher) });
        }
        await db.SaveChangesAsync();
        return id;
    }

    private static Task<ReencryptionSummary> Rotate(NodePilotDbContext db, bool dispatch, ISecretProtector protector, CancellationToken ct)
        => dispatch ? DatabaseSecretRotation.ReencryptDispatchParametersAsync(db, protector, ct)
            : DatabaseSecretRotation.ReencryptNotificationRoutesAsync(db, protector, ct);
}

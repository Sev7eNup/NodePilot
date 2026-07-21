using FluentAssertions;
using NodePilot.Core.Audit;
using NodePilot.Engine.Security;
using Xunit;

namespace NodePilot.Engine.Tests.Audit;

/// <summary>
/// Pins the redaction + 4 KiB cap behavior of <see cref="AuditStager"/>. This is the
/// whole reason every audit-write path now flows through the stager — the previous
/// direct <c>AuditLog.Add</c> bypasses in CredentialStore / TriggerOrchestrator /
/// DbAdminController skipped both protections.
/// </summary>
public class AuditStagerTests
{
    [Fact]
    public void Build_AssignsId_TimestampNow_AndCopiesActor()
    {
        var stager = new AuditStager();
        var actor = new AuditActor(Guid.NewGuid(), "alice", "10.0.0.1");

        var entry = stager.Build("WORKFLOW_PUBLISHED", actor, "Workflow", Guid.NewGuid(), "{}");

        entry.Id.Should().NotBeEmpty();
        entry.Timestamp.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
        entry.UserId.Should().Be(actor.UserId);
        entry.Username.Should().Be("alice");
        entry.IpAddress.Should().Be("10.0.0.1");
        entry.Action.Should().Be("WORKFLOW_PUBLISHED");
    }

    [Fact]
    public void Build_AppliesRedactor_ToDetails()
    {
        // OutputRedactor is the production IAuditDetailsRedactor — same instance Engine
        // uses for script output. Pinning that the audit path uses it too means a
        // CredentialStore call site that accidentally surfaces a password in Details
        // can no longer leak past the regex pass.
        var redactor = new OutputRedactor();
        var stager = new AuditStager(redactor);

        var entry = stager.Build(
            "CREDENTIAL_DECRYPTED",
            AuditActor.System,
            details: "{\"password\":\"hunter2\",\"name\":\"prod-db\"}");

        entry.Details.Should().NotContain("hunter2");
        entry.Details.Should().Contain("***");
        entry.Details.Should().Contain("prod-db", "non-secret fields must survive redaction");
    }

    [Fact]
    public void Build_TruncatesOversizedDetails_AtCap()
    {
        var stager = new AuditStager();
        var huge = new string('x', AuditStager.MaxDetailsChars * 2);

        var entry = stager.Build("WORKFLOW_CREATED", AuditActor.System, details: huge);

        entry.Details.Should().NotBeNull();
        entry.Details!.Length.Should().Be(AuditStager.MaxDetailsChars);
        entry.Details.Should().EndWith(AuditStager.TruncationMarker);
    }

    [Fact]
    public void Build_PreservesShortDetails_Verbatim()
    {
        var stager = new AuditStager();
        const string payload = "{\"workflowId\":\"abc\",\"action\":\"published\"}";

        var entry = stager.Build("WORKFLOW_PUBLISHED", AuditActor.System, details: payload);

        entry.Details.Should().Be(payload);
    }

    [Fact]
    public void Build_NullDetails_PassesThroughAsNull()
    {
        var stager = new AuditStager();
        var entry = stager.Build("WORKFLOW_DELETED", AuditActor.System, details: null);
        entry.Details.Should().BeNull();
    }

    [Fact]
    public void Build_SystemActor_LeavesUserAndIpNull()
    {
        var stager = new AuditStager();
        var entry = stager.Build("TRIGGER_FIRE_SUPPRESSED", AuditActor.System,
            "Workflow", Guid.NewGuid(), "{\"reason\":\"disabled\"}");

        entry.UserId.Should().BeNull();
        entry.Username.Should().BeNull();
        entry.IpAddress.Should().BeNull();
    }
}

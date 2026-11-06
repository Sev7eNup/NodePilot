using FluentAssertions;
using NodePilot.Core.Audit;
using Xunit;

namespace NodePilot.Engine.Tests.Security;

public sealed class AuditEventClassificationTests
{
    [Theory]
    [InlineData(AuditActions.UserLdapRefusedBootstrap)]
    [InlineData(AuditActions.UserWindowsRefusedBootstrap)]
    [InlineData(AuditActions.UserDirectoryAccessRefused)]
    public void DirectoryRefusal_IsAlwaysFailure(string action)
    {
        AuditEventClassification.Outcome(action).Should().Be("failure");
        AuditEventClassification.Outcome(action, "{\"success\":true}").Should().Be("failure");
    }
}

using FluentAssertions;
using NodePilot.Api.Security;
using NodePilot.Core.Enums;
using NodePilot.Core.Models;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Api.Tests.Security;

public sealed class BreakGlassAccountPolicyTests
{
    private static User BreakGlassAdmin(string username = "recovery") => new()
    {
        Id = Guid.NewGuid(),
        Username = username,
        Provider = AuthProvider.Local,
        PasswordHash = "hash",
        Role = UserRole.Admin,
        IsActive = true,
        IsBreakGlass = true,
    };

    [Theory]
    [InlineData("Provider", "Ldap")]
    [InlineData("Role", "Operator")]
    [InlineData("isactive", false)]
    [InlineData("IsTombstoned", true)]
    [InlineData("IsBreakGlass", "False")]
    [InlineData("PasswordHash", "")]
    [InlineData("PasswordHash", null)]
    public void IsRecoveryCapableAfterColumnUpdate_ColumnThatBreaksRecovery_ReturnsFalse(string column, object? value)
        => BreakGlassAccountPolicy.IsRecoveryCapableAfterColumnUpdate(BreakGlassAdmin(), column, value)
            .Should().BeFalse();

    [Theory]
    [InlineData("Username", "renamed")]
    [InlineData("Role", "Admin")]
    [InlineData("IsActive", true)]
    [InlineData("PasswordHash", "new-hash")]
    public void IsRecoveryCapableAfterColumnUpdate_ColumnThatKeepsRecovery_ReturnsTrue(string column, object? value)
        => BreakGlassAccountPolicy.IsRecoveryCapableAfterColumnUpdate(BreakGlassAdmin(), column, value)
            .Should().BeTrue();

    [Fact]
    public async Task MutationPreservesRecoveryAsync_MutatedUserStillQualifies_ReturnsTrueWithoutOthers()
    {
        await using var db = TestDbFactory.Create();
        var user = BreakGlassAdmin();
        db.Users.Add(user);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        (await BreakGlassAccountPolicy.MutationPreservesRecoveryAsync(db, user, TestContext.Current.CancellationToken))
            .Should().BeTrue();
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("inactive", false)]
    [InlineData("tombstoned", false)]
    [InlineData("not-break-glass", false)]
    [InlineData("no-password", false)]
    [InlineData("external", false)]
    [InlineData("operator", false)]
    public async Task MutationPreservesRecoveryAsync_MutatedUserLosesRecovery_DependsOnAnotherQualifyingAdmin(
        string? flaw, bool expected)
    {
        await using var db = TestDbFactory.Create();
        var mutated = BreakGlassAdmin();
        var other = BreakGlassAdmin("other");
        switch (flaw)
        {
            case "inactive": other.IsActive = false; break;
            case "tombstoned": other.IsTombstoned = true; break;
            case "not-break-glass": other.IsBreakGlass = false; break;
            case "no-password": other.PasswordHash = ""; break;
            case "external": other.Provider = AuthProvider.Oidc; break;
            case "operator": other.Role = UserRole.Operator; break;
        }
        db.Users.AddRange(mutated, other);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        mutated.IsActive = false;

        (await BreakGlassAccountPolicy.MutationPreservesRecoveryAsync(db, mutated, TestContext.Current.CancellationToken))
            .Should().Be(expected);
    }
}

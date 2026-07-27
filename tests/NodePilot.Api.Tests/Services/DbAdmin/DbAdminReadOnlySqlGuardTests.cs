using FluentAssertions;
using NodePilot.Api.Services.DbAdmin;
using Xunit;

namespace NodePilot.Api.Tests.Services.DbAdmin;

/// <summary>
/// Direct coverage for the lexical read-only guard that sits below every DbAdmin caller
/// (controller, MCP tool, text2sql reader). The interesting cases are the boundaries: write
/// keywords must be rejected, but SQL functions whose names merely *look* like write verbs
/// must stay usable — a false positive there silently breaks legitimate read queries.
/// </summary>
public class DbAdminReadOnlySqlGuardTests
{
    [Theory]
    [InlineData("SELECT REPLACE(Name, 'a', 'b') FROM Workflows")]
    [InlineData("SELECT replace(Name, 'a', 'b') AS renamed FROM Workflows")]
    [InlineData("SELECT Id FROM Workflows WHERE REPLACE(Name, ' ', '') = 'x'")]
    public void Validate_AllowsReplaceStringFunction(string sql)
    {
        // REPLACE() is a standard string function on PostgreSQL, SQL Server and SQLite.
        var act = () => DbAdminReadOnlySqlGuard.Validate(sql);
        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_StillRejectsReplaceInto()
    {
        // The MySQL write form stays blocked through the INTO token.
        var act = () => DbAdminReadOnlySqlGuard.Validate("REPLACE INTO Workflows (Id) VALUES ('x')");
        act.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData("INSERT INTO Workflows (Id) VALUES ('x')")]
    [InlineData("UPDATE Workflows SET Name = 'x'")]
    [InlineData("DELETE FROM Workflows")]
    [InlineData("DROP TABLE Workflows")]
    [InlineData("SELECT Id INTO Copy FROM Workflows")]
    [InlineData("SELECT Id FROM Workflows FOR UPDATE")]
    public void Validate_RejectsWriteStatements(string sql)
    {
        var act = () => DbAdminReadOnlySqlGuard.Validate(sql);
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Validate_RejectsDangerousRoutine()
    {
        var act = () => DbAdminReadOnlySqlGuard.Validate("SELECT * FROM OPENROWSET('x', 'y', 'z')");
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*not allowed in read mode*");
    }

    [Fact]
    public void Validate_AllowsWriteKeywordInsideStringLiteral()
    {
        var act = () => DbAdminReadOnlySqlGuard.Validate("SELECT Id FROM Workflows WHERE Name = 'DELETE me'");
        act.Should().NotThrow();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_RejectsEmptySql(string sql)
    {
        var act = () => DbAdminReadOnlySqlGuard.Validate(sql);
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Validate_RejectsMultipleStatements()
    {
        var act = () => DbAdminReadOnlySqlGuard.Validate("SELECT 1; SELECT 2");
        act.Should().Throw<InvalidOperationException>();
    }

    // --- Tokenizer support for the row-projection guard (security audit 2026-07-26) ---

    [Fact]
    public void Tokenize_EmitsCastOperator_SoRowCastsAreDetectable()
    {
        DbAdminReadOnlySqlGuard.ReferencesAnyIdentifier(
                "SELECT u::text FROM Users u",
                new HashSet<string>([DbAdminReadOnlySqlGuard.CastOperator]))
            .Should().BeTrue();
    }

    [Fact]
    public void Tokenize_CastOperatorInsideStringLiteral_IsNotAToken()
    {
        DbAdminReadOnlySqlGuard.ReferencesAnyIdentifier(
                "SELECT Id FROM Workflows WHERE Name = 'a::b'",
                new HashSet<string>([DbAdminReadOnlySqlGuard.CastOperator]))
            .Should().BeFalse();
    }

    [Fact]
    public void Validate_StillAcceptsCastOperator_ItIsNotAKeyword()
    {
        var act = () => DbAdminReadOnlySqlGuard.Validate("SELECT Id::text FROM Workflows");
        act.Should().NotThrow();
    }

    [Theory]
    [InlineData("SELECT * FROM Users FOR JSON AUTO", true)]
    [InlineData("SELECT * FROM Users for json path", true)]
    [InlineData("SELECT * FROM Users", false)]
    // A quoted identifier breaks the pair so two columns that happen to be called FOR and JSON
    // do not read as the SQL Server clause.
    [InlineData("SELECT \"FOR\", JSON FROM Users", false)]
    public void ReferencesIdentifierPair_MatchesConsecutiveUnquotedTokensOnly(string sql, bool expected)
    {
        DbAdminReadOnlySqlGuard.ReferencesIdentifierPair(sql, "FOR", "JSON")
            .Should().Be(expected);
    }
}

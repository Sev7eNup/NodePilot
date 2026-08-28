using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace NodePilot.Api.Tests.Architecture;

/// <summary>
/// Both production providers enable <c>EnableRetryOnFailure</c> (see
/// <c>Hosting/DbContextSetup.cs</c>). A retrying execution strategy refuses user-initiated
/// transactions unless the whole unit runs inside <c>strategy.ExecuteAsync</c>; EF throws
/// <see cref="InvalidOperationException"/> otherwise. SQLite never retries, so this scans
/// production source text for the enclosing method around each <c>BeginTransactionAsync</c>.
/// </summary>
public sealed class ExecutionStrategyTransactionTests
{
    /// <summary>
    /// Files that legitimately open a transaction outside an EF execution strategy, with the
    /// reason. Keep this list short and justified — an entry here is a claim that the retrying
    /// strategy cannot be in play at that call site.
    /// </summary>
    private static readonly Dictionary<string, string> Exempt = new(StringComparer.Ordinal)
    {
        ["DbAdminQueryExecutor.cs"] =
            "opens a transaction on a raw ADO connection (conn.BeginTransactionAsync), not through " +
            "DatabaseFacade — the EF execution strategy is not involved.",
    };

    [Fact]
    public void EveryBeginTransaction_RunsInsideAnExecutionStrategy()
    {
        var srcDir = Path.Combine(ProductionSources.RepoRoot(), "src");
        var offenders = new List<string>();

        foreach (var file in ProductionSources.CSharpFiles())
        {
            var name = Path.GetFileName(file);
            if (Exempt.ContainsKey(name)) continue;

            var source = File.ReadAllText(file);
            foreach (Match call in Regex.Matches(source, @"\.BeginTransactionAsync\("))
            {
                var body = EnclosingMethodBody(source, call.Index);
                if (body.Contains("CreateExecutionStrategy", StringComparison.Ordinal)) continue;

                var line = source.Take(call.Index).Count(c => c == '\n') + 1;
                offenders.Add($"{Path.GetRelativePath(srcDir, file)}:{line}");
            }
        }

        offenders.Should().BeEmpty(
            "jede Database.BeginTransactionAsync-Stelle muss in derselben Methode ein " +
            "CreateExecutionStrategy() haben — sonst wirft EF unter Postgres/SQL Server " +
            "InvalidOperationException, und die SQLite-Tests sehen das nie. Gefunden:\n"
            + string.Join("\n", offenders));
    }

    /// <summary>
    /// Sanity check on the scanner itself: if the regex or the exemption list silently stopped
    /// matching anything, the guard above would be permanently green for the wrong reason.
    /// </summary>
    [Fact]
    public void Scanner_ActuallyFindsTransactionCallSites()
    {
        var callSites = ProductionSources.CSharpFiles()
            .Sum(f => Regex.Matches(File.ReadAllText(f), @"\.BeginTransactionAsync\(").Count);

        callSites.Should().BeGreaterThan(10,
            "der Scanner muss die real vorhandenen Transaktionsstellen finden; " +
            "0 oder sehr wenige Treffer heißt, das Muster passt nicht mehr");
    }

    /// <summary>
    /// Walks backwards from <paramref name="index"/> to the opening brace of the enclosing
    /// method and forward to its matching close, so the "is there a strategy?" question is
    /// scoped to one method instead of the whole file.
    /// </summary>
    private static string EnclosingMethodBody(string source, int index)
    {
        var depth = 0;
        var start = 0;
        for (var i = index; i >= 0; i--)
        {
            if (source[i] == '}') depth++;
            else if (source[i] == '{')
            {
                if (depth == 0) { start = i; break; }
                depth--;
            }
        }

        depth = 0;
        var end = source.Length;
        for (var i = start; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0) { end = i; break; }
            }
        }

        // One level up as well: the call often sits in a nested lambda/using block whose own
        // braces would otherwise hide the strategy that wraps it.
        var outerStart = Math.Max(0, start - 4000);
        return source[outerStart..end];
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; depth < 10 && directory is not null; depth++, directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "NodePilot.slnx")))
                return directory.FullName;
        }
        throw new InvalidOperationException("Could not locate NodePilot.slnx from the test output directory.");
    }
}

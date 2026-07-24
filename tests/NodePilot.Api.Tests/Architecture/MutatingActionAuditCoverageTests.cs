using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace NodePilot.Api.Tests.Architecture;

public sealed class MutatingActionAuditCoverageTests
{
    private static readonly Regex MutatingActionRegex = new(
        @"(?ms)(\[(?:HttpPost|HttpPut|HttpPatch|HttpDelete)[^\]]*\](?:\s*\[[^\]]+\])*)\s*" +
        @"public\s+(?:async\s+)?(?:Task<[^>]+>|Task|ActionResult<[^>]+>|IActionResult|[^\s]+)\s+" +
        @"(?<name>\w+)\s*\([^)]*\)\s*\{",
        RegexOptions.Compiled);

    private static readonly string[] AuditSignals =
    [
        "LogAsync",
        "AuditAsync",
        "ScriptAuditAsync",
        "WriteQueryAuditAsync",
        "AuditLog.Add",
        "_stager.Build",
    ];

    private static readonly Dictionary<string, string> AllowedUnauditedActions = new(StringComparer.Ordinal)
    {
        ["BackupController.Preview"] =
            "Dry-run validation of an uploaded backup file; does not write state or trigger remote/external work.",
        ["AlertingController.PreviewFilter"] =
            "Stateless dry-run that evaluates a filter expression against sample fields; writes no state and triggers no remote/external work.",
        ["AlertingController.PreviewRule"] =
            "Stateless dry-run that evaluates a draft rule (in-memory '__preview__' draft) against sample event context; writes no state and triggers no remote/external work.",
        ["SystemAlertingController.Preview"] =
            "Stateless dry-run that samples a system-alert source and reports which current instances match; writes no state and triggers no remote/external work.",
    };

    private static readonly Dictionary<string, string> SensitiveReadAuditSignals = new(StringComparer.Ordinal)
    {
        ["AuditController.ExportStream"] = "AuditActions.AuditLogExported",
        ["DiagnosticsController.Download"] = "AuditActions.SupportLogDownloaded",
        ["DiagnosticsController.ExportEvents"] = "AuditActions.SupportEventsExported",
        ["CustomActivitiesController.Export"] = "AuditActions.CustomActivityExported",
        ["DbAdminController.GetRows"] = "WriteRowsViewedAuditAsync",
    };

    [Fact]
    public void MutatingControllerActions_AreAuditedOrExplicitlyExempt()
    {
        var controllersDir = FindRepoRoot().FullName;
        controllersDir = Path.Combine(controllersDir, "src", "NodePilot.Api", "Controllers");

        var seenExemptions = new HashSet<string>(StringComparer.Ordinal);
        var missing = new List<string>();

        foreach (var file in Directory.GetFiles(controllersDir, "*.cs"))
        {
            var source = File.ReadAllText(file);
            foreach (Match match in MutatingActionRegex.Matches(source))
            {
                var methodName = match.Groups["name"].Value;
                var key = $"{Path.GetFileNameWithoutExtension(file)}.{methodName}";
                var body = ExtractMethodBody(source, match.Index + match.Length - 1);

                if (BodyHasAuditSignal(body) || DelegatesToAuditedHelper(body))
                    continue;

                if (AllowedUnauditedActions.ContainsKey(key))
                {
                    seenExemptions.Add(key);
                    continue;
                }

                missing.Add(key);
            }
        }

        seenExemptions.Should().BeEquivalentTo(AllowedUnauditedActions.Keys,
            "stale audit exemptions should be removed when endpoints start auditing or disappear");
        missing.Should().BeEmpty("mutating HTTP actions should emit an audit event or carry an explicit rationale");
    }

    [Fact]
    public void SensitiveReadAndExportActions_EmitTheirDedicatedAuditSignals()
    {
        var controllersDir = Path.Combine(
            FindRepoRoot().FullName, "src", "NodePilot.Api", "Controllers");
        var missing = new List<string>();

        foreach (var (key, signal) in SensitiveReadAuditSignals)
        {
            var separator = key.IndexOf('.');
            var controller = key[..separator];
            var methodName = key[(separator + 1)..];
            var file = Path.Combine(controllersDir, controller + ".cs");
            var root = CSharpSyntaxTree.ParseText(File.ReadAllText(file), path: file).GetRoot();
            var method = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
                .SingleOrDefault(candidate => candidate.Identifier.ValueText == methodName);

            if (method is null
                || !method.AttributeLists.SelectMany(list => list.Attributes)
                    .Any(attribute => attribute.Name.ToString().EndsWith("HttpGet", StringComparison.Ordinal))
                || !method.ToFullString().Contains(signal, StringComparison.Ordinal))
            {
                missing.Add($"{key} -> {signal}");
            }
        }

        missing.Should().BeEmpty(
            "downloads and exports of sensitive operational data require explicit audit coverage");
    }

    private static bool BodyHasAuditSignal(string body)
        => AuditSignals.Any(body.Contains);

    private static bool DelegatesToAuditedHelper(string body)
        => body.Contains("SetEnabled(", StringComparison.Ordinal)
           || body.Contains("UpdateCore(", StringComparison.Ordinal)
           || body.Contains("DeleteCore(", StringComparison.Ordinal);

    private static string ExtractMethodBody(string source, int openingBraceIndex)
    {
        var depth = 0;
        for (var i = openingBraceIndex; i < source.Length; i++)
        {
            if (source[i] == '{')
                depth++;
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0)
                    return source[openingBraceIndex..(i + 1)];
            }
        }

        return source[openingBraceIndex..];
    }

    private static DirectoryInfo FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "NodePilot.Api", "Controllers")))
                return dir;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root from test base directory.");
    }
}

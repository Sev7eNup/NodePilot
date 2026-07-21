using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace NodePilot.Cli.Tests;

public sealed class ApiDtoParityTests
{
    public static IEnumerable<object[]> CopiedResponseContracts()
    {
        yield return new object[]
        {
            new CopiedDtoContract(
                "MachineResponse",
                "src/NodePilot.Api/Dtos/MachineDtos.cs",
                "src/NodePilot.Cli/Api/Dtos/ResourceDtos.cs",
                "src/NodePilot.Mcp/Api/Dtos/Dtos.cs"),
        };
        yield return new object[]
        {
            new CopiedDtoContract(
                "StepExecutionResponse",
                "src/NodePilot.Api/Dtos/ExecutionDtos.cs",
                "src/NodePilot.Cli/Api/Dtos/WorkflowDtos.cs",
                "src/NodePilot.Mcp/Api/Dtos/Dtos.cs"),
        };
    }

    public static IEnumerable<object[]> FrontendResponseContracts()
    {
        yield return new object[] { new FrontendDtoContract("MachineResponse", "src/NodePilot.Api/Dtos/MachineDtos.cs", "ManagedMachine") };
        yield return new object[] { new FrontendDtoContract("StepExecutionResponse", "src/NodePilot.Api/Dtos/ExecutionDtos.cs", "StepExecution") };
    }

    [Theory]
    [MemberData(nameof(CopiedResponseContracts))]
    public void CliAndMcpCopiedResponseDtos_MirrorApiRecordParameters(CopiedDtoContract contract)
    {
        var repoRoot = FindRepoRoot();
        var api = ReadRecordParameters(PathFor(repoRoot, contract.ApiPath), contract.RecordName);
        var cli = ReadRecordParameters(PathFor(repoRoot, contract.CliPath), contract.RecordName);
        var mcp = ReadRecordParameters(PathFor(repoRoot, contract.McpPath), contract.RecordName);

        cli.Should().Equal(api, $"{contract.RecordName} is copied into the CLI and must stay wire-compatible with the API DTO");
        mcp.Should().Equal(api, $"{contract.RecordName} is copied into the MCP server and must stay wire-compatible with the API DTO");
    }

    [Theory]
    [MemberData(nameof(FrontendResponseContracts))]
    public void FrontendApiTypes_ExposeApiResponseFields(FrontendDtoContract contract)
    {
        var repoRoot = FindRepoRoot();
        var api = ReadRecordParameters(PathFor(repoRoot, contract.ApiPath), contract.ApiRecordName)
            .Select(p => ToCamelCase(p.Name))
            .ToList();
        var frontend = ReadTypeScriptInterfaceFields(
            PathFor(repoRoot, "src/nodepilot-ui/src/types/api.ts"),
            contract.FrontendInterfaceName);

        var missing = api.Where(field => !frontend.Contains(field)).ToList();
        missing.Should().BeEmpty(
            $"{contract.FrontendInterfaceName} mirrors {contract.ApiRecordName} fields from the API response contract");
    }

    private static IReadOnlyList<DtoParameter> ReadRecordParameters(string path, string recordName)
    {
        File.Exists(path).Should().BeTrue($"{path} must exist");
        var content = StripComments(File.ReadAllText(path));
        var marker = $"record {recordName}";
        var markerIndex = content.IndexOf(marker, StringComparison.Ordinal);
        markerIndex.Should().BeGreaterThanOrEqualTo(0, $"{recordName} must be declared in {path}");

        var open = content.IndexOf('(', markerIndex);
        open.Should().BeGreaterThanOrEqualTo(0, $"{recordName} must use a positional record constructor");
        var body = ExtractBalanced(content, open, '(', ')');

        return SplitTopLevel(body, ',')
            .Select(ParseParameter)
            .ToList();
    }

    private static IReadOnlySet<string> ReadTypeScriptInterfaceFields(string path, string interfaceName)
    {
        File.Exists(path).Should().BeTrue($"{path} must exist");
        var content = StripComments(File.ReadAllText(path));
        var marker = $"export interface {interfaceName}";
        var markerIndex = content.IndexOf(marker, StringComparison.Ordinal);
        markerIndex.Should().BeGreaterThanOrEqualTo(0, $"{interfaceName} must be declared in {path}");

        var open = content.IndexOf('{', markerIndex);
        open.Should().BeGreaterThanOrEqualTo(0, $"{interfaceName} must have a body");
        var body = ExtractBalanced(content, open, '{', '}');

        return Regex.Matches(body, @"^\s*(?<name>[A-Za-z_][A-Za-z0-9_]*)\??\s*:", RegexOptions.Multiline)
            .Select(m => m.Groups["name"].Value)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static DtoParameter ParseParameter(string raw)
    {
        var withoutDefault = SplitTopLevel(raw, '=').First().Trim();
        var match = Regex.Match(withoutDefault, @"^(?<type>.+?)\s+(?<name>@?[A-Za-z_][A-Za-z0-9_]*)$");
        match.Success.Should().BeTrue($"'{raw}' must be a parseable DTO parameter");

        return new DtoParameter(
            Name: match.Groups["name"].Value.TrimStart('@'),
            Type: Regex.Replace(match.Groups["type"].Value.Trim(), @"\s+", " "));
    }

    private static IReadOnlyList<string> SplitTopLevel(string value, char separator)
    {
        var parts = new List<string>();
        var start = 0;
        var angleDepth = 0;
        var parenDepth = 0;
        var bracketDepth = 0;

        for (var i = 0; i < value.Length; i++)
        {
            var ch = value[i];
            switch (ch)
            {
                case '<': angleDepth++; break;
                case '>': if (angleDepth > 0) angleDepth--; break;
                case '(': parenDepth++; break;
                case ')': if (parenDepth > 0) parenDepth--; break;
                case '[': bracketDepth++; break;
                case ']': if (bracketDepth > 0) bracketDepth--; break;
            }

            if (ch == separator && angleDepth == 0 && parenDepth == 0 && bracketDepth == 0)
            {
                parts.Add(value[start..i].Trim());
                start = i + 1;
            }
        }

        parts.Add(value[start..].Trim());
        return parts.Where(p => p.Length > 0).ToList();
    }

    private static string ExtractBalanced(string content, int openIndex, char openChar, char closeChar)
    {
        var depth = 0;
        for (var i = openIndex; i < content.Length; i++)
        {
            if (content[i] == openChar) depth++;
            if (content[i] == closeChar)
            {
                depth--;
                if (depth == 0)
                    return content[(openIndex + 1)..i];
            }
        }

        throw new InvalidOperationException($"Could not find matching '{closeChar}' in DTO source.");
    }

    private static string StripComments(string content)
        => Regex.Replace(content, @"/\*[\s\S]*?\*/|//.*", "", RegexOptions.Multiline);

    private static string PathFor(string repoRoot, string relativePath)
        => Path.Combine([repoRoot, .. relativePath.Split('/')]);

    private static string ToCamelCase(string value)
        => value.Length == 0 ? value : char.ToLowerInvariant(value[0]) + value[1..];

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 10 && dir is not null; i++, dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "NodePilot.slnx")))
                return dir.FullName;
        }

        throw new InvalidOperationException($"Could not locate NodePilot.slnx walking up from {AppContext.BaseDirectory}");
    }

    public sealed record CopiedDtoContract(string RecordName, string ApiPath, string CliPath, string McpPath);

    public sealed record FrontendDtoContract(string ApiRecordName, string ApiPath, string FrontendInterfaceName);

    private sealed record DtoParameter(string Name, string Type);
}

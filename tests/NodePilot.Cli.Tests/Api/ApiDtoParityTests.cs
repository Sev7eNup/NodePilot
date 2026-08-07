using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace NodePilot.Cli.Tests.Api;

/// <summary>
/// The CLI and the MCP server are HTTP-only clients that keep their own copies of the API's
/// response/request records — <c>NodePilot.Mcp/Api/NodePilotApiClient.cs</c> says so in its own
/// header ("Copied/adapted from the CLI's client"). 97 record names are shared between the API
/// and the CLI, 62 between the API and the MCP server, and until now exactly two of them were
/// guarded by hand, so the copies could drift silently.
///
/// <para>These tests discover the shared names instead of listing them. The invariant is
/// one-directional on purpose: a client may carry <em>extra</em> fields (harmless — they stay
/// null), but it must not <em>miss</em> a field the API sends, because that silently drops data
/// on the read path and silently omits it on the write path.</para>
///
/// <para>Records are compared on positional parameters plus <c>{ get; init; }</c> body
/// properties: several API DTOs (notably <c>WorkflowResponse</c>) carry a large part of their
/// contract in the body, and a positional-only comparison reports them as false matches.</para>
/// </summary>
public sealed class ApiDtoParityTests
{
    /// <summary>
    /// Pre-existing gaps, kept explicit so they are visible and countable rather than invisible.
    /// Each entry is a client DTO that predates this guard and is missing API fields; the value
    /// documents what that costs. Entries must be removed as the gaps get closed —
    /// <see cref="KnownGaps_AreAllStillReal"/> fails on a stale entry, so the list cannot rot.
    /// </summary>
    private static readonly Dictionary<string, string> KnownCliGaps = new(StringComparer.Ordinal)
    {
        ["ArmedTriggerInfo"] = "np does not render next-fire prediction / poll interval",
        ["AuthMethodsResponse"] = "np auth cannot discover the OIDC login path",
        ["CreateUserRequest"] = "np user create cannot mark an account break-glass",
        ["CreateWorkflowRequest"] = "np workflow create cannot target a folder (RBAC folders)",
        ["DashboardStats"] = "np dashboard shows the pre-mission-control subset of the widgets",
        ["GrantSharedFolderPermissionRequest"] = "np cannot scope a grant by principal authority",
        ["SharedFolderPermissionResponse"] = "np does not render the principal authority",
        ["TopWorkflow"] = "np dashboard omits avg/p95 duration per workflow",
        ["UpdateUserRequest"] = "np user update cannot change the break-glass flag",
        ["UserResponse"] = "np user list omits provider/authority/break-glass/tombstone state",
        ["WorkflowResponse"] = "np does not render folder placement or per-row RBAC capabilities",
    };

    private static readonly Dictionary<string, string> KnownMcpGaps = new(StringComparer.Ordinal)
    {
        ["CreateWorkflowRequest"] = "create_workflow cannot target a folder (RBAC folders)",
        ["WorkflowResponse"] = "workflow tools do not surface folder placement or RBAC capabilities",
    };

    public static TheoryData<string> ClientProjects() => new("Cli", "Mcp");

    /// <summary>
    /// Every shared DTO must carry every field the API declares, except the documented gaps.
    /// A new endpoint or a new field on an existing one is exactly what this catches: the
    /// project convention is that both clients follow the API, and nothing enforced it.
    /// </summary>
    [Theory]
    [MemberData(nameof(ClientProjects))]
    public void SharedClientDtos_CarryEveryApiField(string client)
    {
        var api = DiscoverRecords("src/NodePilot.Api/Dtos");
        var clientRecords = DiscoverRecords($"src/NodePilot.{client}/Api/Dtos");
        var known = client == "Cli" ? KnownCliGaps : KnownMcpGaps;

        var offenders = new List<string>();
        foreach (var (name, apiFields) in api)
        {
            if (!clientRecords.TryGetValue(name, out var clientFields)) continue;
            if (known.ContainsKey(name)) continue;

            var missing = apiFields.Except(clientFields, StringComparer.Ordinal).ToList();
            if (missing.Count > 0)
                offenders.Add($"{name}: fehlt {string.Join(", ", missing)}");
        }

        offenders.Should().BeEmpty(
            $"das {client}-DTO ist eine Kopie des API-DTOs — ein fehlendes Feld verschluckt " +
            "auf dem Lesepfad still Daten und lässt es auf dem Schreibpfad weg. Entweder das " +
            "Feld ergänzen (samt Command/Tool, das es nutzt) oder bewusst in die Known-Gaps " +
            "aufnehmen. Gefunden:\n" + string.Join("\n", offenders));
    }

    /// <summary>
    /// Keeps the exemption list honest: once a gap is closed, its entry has to go, otherwise the
    /// list slowly turns into a blanket opt-out that hides fresh drift behind an old excuse.
    /// </summary>
    [Theory]
    [MemberData(nameof(ClientProjects))]
    public void KnownGaps_AreAllStillReal(string client)
    {
        var api = DiscoverRecords("src/NodePilot.Api/Dtos");
        var clientRecords = DiscoverRecords($"src/NodePilot.{client}/Api/Dtos");
        var known = client == "Cli" ? KnownCliGaps : KnownMcpGaps;

        var stale = known.Keys
            .Where(name => !clientRecords.TryGetValue(name, out var fields)
                           || !api.TryGetValue(name, out var apiFields)
                           || !apiFields.Except(fields, StringComparer.Ordinal).Any())
            .ToList();

        stale.Should().BeEmpty(
            "diese Known-Gap-Einträge treffen nicht mehr zu und müssen aus der Liste raus — " +
            "sonst deckt der Eintrag später echte Drift zu");
    }

    /// <summary>
    /// Sanity check on the discovery itself. If the parser or the folder layout changed, the
    /// guard above would go permanently green while checking nothing.
    /// </summary>
    [Theory]
    [MemberData(nameof(ClientProjects))]
    public void Discovery_FindsTheSharedContractSurface(string client)
    {
        var api = DiscoverRecords("src/NodePilot.Api/Dtos");
        var clientRecords = DiscoverRecords($"src/NodePilot.{client}/Api/Dtos");

        api.Should().HaveCountGreaterThan(100, "the API DTO folder holds the full contract surface");
        var shared = api.Keys.Intersect(clientRecords.Keys, StringComparer.Ordinal).Count();
        shared.Should().BeGreaterThan(50,
            $"the {client} client mirrors a large part of the API contract; " +
            "a near-empty intersection means the parser stopped matching");
    }

    public static IEnumerable<object[]> FrontendResponseContracts()
    {
        yield return new object[] { new FrontendDtoContract("MachineResponse", "src/NodePilot.Api/Dtos/MachineDtos.cs", "ManagedMachine") };
        yield return new object[] { new FrontendDtoContract("StepExecutionResponse", "src/NodePilot.Api/Dtos/ExecutionDtos.cs", "StepExecution") };
    }

    [Theory]
    [MemberData(nameof(FrontendResponseContracts))]
    public void FrontendApiTypes_ExposeApiResponseFields(FrontendDtoContract contract)
    {
        var repoRoot = FindRepoRoot();
        var api = ReadRecordFieldNames(PathFor(repoRoot, contract.ApiPath), contract.ApiRecordName)
            .Select(ToCamelCase)
            .ToList();
        var frontend = ReadTypeScriptInterfaceFields(
            PathFor(repoRoot, "src/nodepilot-ui/src/types/api.ts"),
            contract.FrontendInterfaceName);

        var missing = api.Where(field => !frontend.Contains(field)).ToList();
        missing.Should().BeEmpty(
            $"{contract.FrontendInterfaceName} mirrors {contract.ApiRecordName} fields from the API response contract");
    }

    // ------------------------------------------------------------------ discovery

    /// <summary>
    /// Parses every positional record under <paramref name="relativeFolder"/> into
    /// name → field names (positional parameters plus <c>{ get; init; }</c> body properties).
    /// First declaration wins on a duplicate name, matching how the compiler would resolve it
    /// within one namespace.
    /// </summary>
    private static Dictionary<string, IReadOnlyList<string>> DiscoverRecords(string relativeFolder)
    {
        var folder = PathFor(FindRepoRoot(), relativeFolder);
        Directory.Exists(folder).Should().BeTrue($"{folder} must exist");

        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                continue;

            var content = StripComments(File.ReadAllText(file));
            foreach (Match declaration in Regex.Matches(content, @"\brecord\s+(?<name>\w+)\s*\("))
            {
                var name = declaration.Groups["name"].Value;
                if (result.ContainsKey(name)) continue;

                var open = content.IndexOf('(', declaration.Index);
                if (open < 0) continue;
                var fields = ParseRecordFields(content, open);
                if (fields is not null) result[name] = fields;
            }
        }

        return result;
    }

    private static IReadOnlyList<string>? ParseRecordFields(string content, int openIndex)
    {
        var parameterList = TryExtractBalanced(content, openIndex, '(', ')');
        if (parameterList is null) return null;

        var fields = SplitTopLevel(parameterList, ',')
            .Select(ParseParameterName)
            .Where(n => n is not null)
            .Select(n => n!)
            .ToList();

        // Body properties, when the declaration is followed by a `{ ... }` block rather than `;`.
        var afterParameters = openIndex + parameterList.Length + 2;
        if (afterParameters < content.Length)
        {
            var rest = content[afterParameters..];
            var firstMeaningful = rest.TrimStart();
            if (firstMeaningful.StartsWith('{'))
            {
                var bodyOpen = afterParameters + (rest.Length - firstMeaningful.Length);
                var body = TryExtractBalanced(content, bodyOpen, '{', '}');
                if (body is not null)
                {
                    fields.AddRange(Regex
                        .Matches(body, @"public\s+[\w\?\<\>\[\],\.\s]+?\s+(?<name>\w+)\s*\{\s*get;\s*init;")
                        .Select(m => m.Groups["name"].Value));
                }
            }
        }

        return fields;
    }

    private static IReadOnlyList<string> ReadRecordFieldNames(string path, string recordName)
    {
        File.Exists(path).Should().BeTrue($"{path} must exist");
        var content = StripComments(File.ReadAllText(path));
        var markerIndex = content.IndexOf($"record {recordName}", StringComparison.Ordinal);
        markerIndex.Should().BeGreaterThanOrEqualTo(0, $"{recordName} must be declared in {path}");

        var open = content.IndexOf('(', markerIndex);
        open.Should().BeGreaterThanOrEqualTo(0, $"{recordName} must use a positional record constructor");

        var fields = ParseRecordFields(content, open);
        fields.Should().NotBeNull();
        return fields!;
    }

    private static IReadOnlySet<string> ReadTypeScriptInterfaceFields(string path, string interfaceName)
    {
        File.Exists(path).Should().BeTrue($"{path} must exist");
        var content = StripComments(File.ReadAllText(path));
        var markerIndex = content.IndexOf($"export interface {interfaceName}", StringComparison.Ordinal);
        markerIndex.Should().BeGreaterThanOrEqualTo(0, $"{interfaceName} must be declared in {path}");

        var open = content.IndexOf('{', markerIndex);
        open.Should().BeGreaterThanOrEqualTo(0, $"{interfaceName} must have a body");
        var body = TryExtractBalanced(content, open, '{', '}')!;

        return Regex.Matches(body, @"^\s*(?<name>[A-Za-z_][A-Za-z0-9_]*)\??\s*:", RegexOptions.Multiline)
            .Select(m => m.Groups["name"].Value)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static string? ParseParameterName(string raw)
    {
        var withoutDefault = SplitTopLevel(raw, '=').FirstOrDefault()?.Trim();
        if (string.IsNullOrEmpty(withoutDefault)) return null;
        var match = Regex.Match(withoutDefault, @"^(?<type>.+?)\s+(?<name>@?[A-Za-z_][A-Za-z0-9_]*)$", RegexOptions.Singleline);
        return match.Success ? match.Groups["name"].Value.TrimStart('@') : null;
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

    private static string? TryExtractBalanced(string content, int openIndex, char openChar, char closeChar)
    {
        if (openIndex < 0 || openIndex >= content.Length || content[openIndex] != openChar) return null;
        var depth = 0;
        for (var i = openIndex; i < content.Length; i++)
        {
            if (content[i] == openChar) depth++;
            if (content[i] == closeChar)
            {
                depth--;
                if (depth == 0) return content[(openIndex + 1)..i];
            }
        }

        return null;
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

    public sealed record FrontendDtoContract(string ApiRecordName, string ApiPath, string FrontendInterfaceName);
}

using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace NodePilot.Api.Tests.Architecture;

public sealed class MutatingActionAuditCoverageTests
{
    private static readonly string[] AuditSignals =
    [
        "LogAsync",
        "AuditAsync",
        "ScriptAuditAsync",
        "WriteQueryAuditAsync",
        "AuditLog.Add",
        "_stager.Build",
    ];

    // Service calls that stage their own audit row in the same transaction as the mutation.
    private static readonly string[] AuditingServiceCalls =
    [
        // ScimProvisioningService (ScimUsersController, ScimGroupsController)
        "provisioning.Create", "provisioning.Replace", "provisioning.Patch", "provisioning.Delete",
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
        ["AppLogController.Download"] = "AuditActions.AppLogDownloaded",
        ["DiagnosticsController.ExportEvents"] = "AuditActions.SupportEventsExported",
        ["CustomActivitiesController.Export"] = "AuditActions.CustomActivityExported",
        ["DbAdminController.GetRows"] = "WriteRowsViewedAuditAsync",
    };

    private static readonly HashSet<string> MutatingVerbAttributes =
        new(StringComparer.Ordinal) { "HttpPost", "HttpPut", "HttpPatch", "HttpDelete" };

    [Fact]
    public void MutatingControllerActions_AreAuditedOrExplicitlyExempt()
    {
        var classes = ControllerClasses();
        var seenExemptions = new HashSet<string>(StringComparer.Ordinal);
        var missing = new List<string>();

        foreach (var (controller, method) in MutatingActions(classes))
        {
            var key = $"{controller.Identifier.ValueText}.{method.Identifier.ValueText}";
            if (ReachesAuditSignal(method, VisibleMethods(controller, classes), []))
                continue;

            if (AllowedUnauditedActions.ContainsKey(key))
            {
                seenExemptions.Add(key);
                continue;
            }

            missing.Add(key);
        }

        seenExemptions.Should().BeEquivalentTo(AllowedUnauditedActions.Keys,
            "stale audit exemptions should be removed when endpoints start auditing or disappear");
        missing.Should().BeEmpty("mutating HTTP actions should emit an audit event or carry an explicit rationale");
    }

    [Fact]
    public void MutatingActionScan_SeesEveryVerbAttributedMethod()
    {
        // Cross-check the syntax scan against reflection over the compiled controllers, so an
        // action the scan cannot see fails here instead of silently skipping the audit check.
        var scanned = MutatingActions(ControllerClasses())
            .Select(action => $"{action.Controller.Identifier.ValueText}.{action.Method.Identifier.ValueText}")
            .ToHashSet(StringComparer.Ordinal);

        var compiled = typeof(NodePilot.Api.Controllers.UsersController).Assembly.GetTypes()
            .Where(type => typeof(Microsoft.AspNetCore.Mvc.ControllerBase).IsAssignableFrom(type) && !type.IsAbstract)
            .SelectMany(type => type.GetMethods(System.Reflection.BindingFlags.Public
                    | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly)
                .Where(method => method.GetCustomAttributes(inherit: true)
                    .OfType<Microsoft.AspNetCore.Mvc.Routing.HttpMethodAttribute>()
                    .Any(attribute => attribute.HttpMethods.Any(verb => verb is "POST" or "PUT" or "PATCH" or "DELETE")))
                .Select(method => $"{type.Name}.{method.Name}"))
            .ToHashSet(StringComparer.Ordinal);

        compiled.Should().NotBeEmpty();
        scanned.Should().BeEquivalentTo(compiled);
    }

    private static List<ClassDeclarationSyntax> ControllerClasses()
    {
        var controllersDir = Path.Combine(FindRepoRoot().FullName, "src", "NodePilot.Api", "Controllers");
        return Directory.GetFiles(controllersDir, "*.cs", SearchOption.AllDirectories)
            .SelectMany(file => CSharpSyntaxTree.ParseText(File.ReadAllText(file), path: file).GetRoot()
                .DescendantNodes().OfType<ClassDeclarationSyntax>())
            .ToList();
    }

    private static IEnumerable<(ClassDeclarationSyntax Controller, MethodDeclarationSyntax Method)> MutatingActions(
        IEnumerable<ClassDeclarationSyntax> classes)
        => classes
            .Where(controller => !controller.Modifiers.Any(SyntaxKind.AbstractKeyword))
            .SelectMany(controller => controller.Members.OfType<MethodDeclarationSyntax>()
                .Where(method => method.Modifiers.Any(SyntaxKind.PublicKeyword)
                    && method.AttributeLists.SelectMany(list => list.Attributes)
                        .Any(attribute => MutatingVerbAttributes.Contains(AttributeName(attribute))))
                .Select(method => (controller, method)));

    private static string AttributeName(AttributeSyntax attribute)
    {
        var name = attribute.Name.ToString();
        name = name[(name.LastIndexOf('.') + 1)..];
        return name.EndsWith("Attribute", StringComparison.Ordinal) ? name[..^"Attribute".Length] : name;
    }

    /// <summary>Methods callable without a receiver: the class itself plus its base classes in the
    /// Controllers folder.</summary>
    private static List<MethodDeclarationSyntax> VisibleMethods(
        ClassDeclarationSyntax controller, List<ClassDeclarationSyntax> classes)
    {
        var methods = new List<MethodDeclarationSyntax>();
        var current = controller;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (current is not null && seen.Add(current.Identifier.ValueText))
        {
            methods.AddRange(classes
                .Where(candidate => candidate.Identifier.ValueText == current.Identifier.ValueText)
                .SelectMany(candidate => candidate.Members.OfType<MethodDeclarationSyntax>()));
            var baseName = current.BaseList?.Types.FirstOrDefault()?.Type.ToString();
            current = classes.FirstOrDefault(candidate => candidate.Identifier.ValueText == baseName);
        }
        return methods;
    }

    /// <summary>True when the method's own code (comments excluded) holds an audit signal, or a
    /// method of the same controller it calls does.</summary>
    private static bool ReachesAuditSignal(
        MethodDeclarationSyntax method, List<MethodDeclarationSyntax> visible, HashSet<MethodDeclarationSyntax> visited)
    {
        if (!visited.Add(method)) return false;
        SyntaxNode? body = (SyntaxNode?)method.Body ?? method.ExpressionBody;
        if (body is null) return false;

        var code = string.Concat(body.DescendantTokens().Select(token => token.Text));
        if (AuditSignals.Any(code.Contains) || AuditingServiceCalls.Any(code.Contains)) return true;

        var callees = body.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Select(invocation => invocation.Expression switch
            {
                IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
                GenericNameSyntax generic => generic.Identifier.ValueText,
                MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax } member => member.Name.Identifier.ValueText,
                _ => null,
            })
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);

        return visible.Where(candidate => callees.Contains(candidate.Identifier.ValueText))
            .Any(candidate => ReachesAuditSignal(candidate, visible, visited));
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

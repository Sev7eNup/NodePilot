using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NodePilot.Core.Audit;
using Xunit;

namespace NodePilot.Api.Tests.Architecture;

/// <summary>
/// Keeps the audit-event vocabulary centralized. The guard parses C# syntax rather than matching
/// source text so formatting, named arguments, ternaries, switches and interpolated strings cannot
/// hide a raw action code. Every production C# project under <c>src</c> is included because audit
/// events are also emitted by the Data and Scheduler assemblies.
/// </summary>
public class AuditActionsCatalogTests
{
    [Fact]
    public void AuditActionSources_UseCatalogConstants()
    {
        var catalog = CatalogConstants();
        var offenders = new List<string>();

        foreach (var file in ProductionSourceFiles())
        {
            var root = CSharpSyntaxTree.ParseText(File.ReadAllText(file), path: file).GetRoot();
            offenders.AddRange(FindViolations(root, catalog.Keys));
        }

        offenders.Should().BeEmpty(
            "every emitted audit action must come from NodePilot.Core.Audit.AuditActions; " +
            "add a catalog constant and reference it at the emission source");
    }

    [Fact]
    public void EveryAuditActionConstant_IsReferencedInProductionSource()
    {
        var declared = CatalogConstants().Keys.ToHashSet(StringComparer.Ordinal);
        var referenced = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in ProductionSourceFiles().Where(p => !p.EndsWith("AuditActions.cs", StringComparison.Ordinal)))
        {
            var root = CSharpSyntaxTree.ParseText(File.ReadAllText(file), path: file).GetRoot();
            foreach (var access in root.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
            {
                if (IsAuditActionsType(access.Expression))
                    referenced.Add(access.Name.Identifier.ValueText);
            }
        }

        var stale = declared.Except(referenced).OrderBy(x => x).ToList();
        stale.Should().BeEmpty(
            "every AuditActions constant must be used by production code; remove stale entries or wire them up");
    }

    [Fact]
    public void EveryAuditActionValue_FollowsScreamingSnakeShape()
    {
        var badlyShaped = CatalogConstants().Values
            .Where(v => !Regex.IsMatch(v, "^[A-Z][A-Z0-9_]+$"))
            .ToList();

        badlyShaped.Should().BeEmpty("audit codes are SCREAMING_SNAKE strings");
    }

    [Theory]
    [InlineData("await audit.LogAsync(action: flag ? \"USER_ACTIVATED\" : AuditActions.UserDeactivated, resourceType: \"User\");")]
    [InlineData("await audit.LogAsync(flag ? $\"USER_{provider}_UPDATED\" : AuditActions.UserDeleted, \"User\");")]
    [InlineData("var action = ok ? \"LOGIN_SUCCESS\" : \"LOGIN_FAILED\"; await audit.LogAsync(action, \"User\");")]
    [InlineData("var row = stager.Build(action: \"DBADMIN_SQL_WRITE\", actor: actor, resourceType: \"DbAdmin\");")]
    [InlineData("new SettingsSectionDescriptor(\"X\", \"X\", typeof(object), typeof(object), default, true, \"SETTINGS_X_UPDATED\");")]
    [InlineData("await SaveMutationWithAuditAsync(user, $\"USER_{provider}_JIT_UPDATED\", details, ct);")]
    public void SyntaxGuard_DetectsPreviouslyBlindActionShapes(string source)
    {
        var root = CSharpSyntaxTree.ParseText(source, path: "GuardProbe.cs").GetRoot();

        FindViolations(root, CatalogConstants().Keys).Should().NotBeEmpty();
    }

    [Fact]
    public void SyntaxGuard_IgnoresUnrelatedUppercaseStrings()
    {
        const string source = "var status = \"BAD_REQUEST\"; logger.LogWarning(\"LOGIN_FAILED while probing\");";
        var root = CSharpSyntaxTree.ParseText(source, path: "GuardProbe.cs").GetRoot();

        FindViolations(root, CatalogConstants().Keys).Should().BeEmpty();
    }

    private static IReadOnlyList<string> FindViolations(SyntaxNode root, IEnumerable<string> catalogNames)
    {
        var catalog = catalogNames.ToHashSet(StringComparer.Ordinal);
        var violations = new List<string>();

        foreach (var expression in AuditActionExpressions(root))
            ValidateActionExpression(expression, root, catalog, violations, new HashSet<SyntaxNode>());

        return violations;
    }

    private static IEnumerable<ExpressionSyntax> AuditActionExpressions(SyntaxNode root)
    {
        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var methodName = InvocationName(invocation);
            ArgumentSyntax? actionArgument = null;

            if (methodName == "LogAsync")
            {
                actionArgument = NamedOrPositionalArgument(invocation.ArgumentList, "action", 0);
            }
            else if (methodName == "SaveMutationWithAuditAsync")
            {
                actionArgument = NamedOrPositionalArgument(invocation.ArgumentList, "action", 1);
            }
            else if (methodName == "Build"
                     && invocation.Expression is MemberAccessExpressionSyntax member
                     && (member.Expression.ToString().Contains("stager", StringComparison.OrdinalIgnoreCase)
                         || invocation.ArgumentList.Arguments.Any(a => a.NameColon?.Name.Identifier.ValueText == "action")))
            {
                actionArgument = NamedOrPositionalArgument(invocation.ArgumentList, "action", 0);
            }

            if (actionArgument is not null)
                yield return actionArgument.Expression;
        }

        foreach (var creation in root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
        {
            if (creation.Type.ToString().EndsWith("SettingsSectionDescriptor", StringComparison.Ordinal)
                && creation.ArgumentList is { } arguments
                && NamedOrPositionalArgument(arguments, "AuditCode", 6) is { } auditCode)
            {
                yield return auditCode.Expression;
            }

            if (!creation.Type.ToString().EndsWith("AuditLogEntry", StringComparison.Ordinal)
                || creation.Initializer is null)
                continue;

            foreach (var assignment in creation.Initializer.Expressions.OfType<AssignmentExpressionSyntax>())
                if (assignment.Left.ToString().EndsWith("Action", StringComparison.Ordinal))
                    yield return assignment.Right;
        }
    }

    private static void ValidateActionExpression(
        ExpressionSyntax expression,
        SyntaxNode root,
        IReadOnlySet<string> catalog,
        ICollection<string> violations,
        ISet<SyntaxNode> visited)
    {
        if (!visited.Add(expression))
            return;

        expression = Unwrap(expression);

        if (expression is MemberAccessExpressionSyntax member && IsAuditActionsType(member.Expression))
        {
            if (!catalog.Contains(member.Name.Identifier.ValueText))
                AddViolation(expression, violations, $"unknown AuditActions member '{member.Name.Identifier.ValueText}'");
            return;
        }

        switch (expression)
        {
            case ConditionalExpressionSyntax conditional:
                ValidateActionExpression(conditional.WhenTrue, root, catalog, violations, visited);
                ValidateActionExpression(conditional.WhenFalse, root, catalog, violations, visited);
                return;

            case SwitchExpressionSyntax switchExpression:
                foreach (var arm in switchExpression.Arms)
                    ValidateActionExpression(arm.Expression, root, catalog, violations, visited);
                return;

            case IdentifierNameSyntax identifier:
                if (TryValidateIdentifierSource(identifier, root, catalog, violations, visited))
                    return;
                AddViolation(expression, violations,
                    $"dynamic audit action '{identifier.Identifier.ValueText}' is not traceable to AuditActions");
                return;

            case MemberAccessExpressionSyntax auditCode
                when auditCode.Name.Identifier.ValueText == "AuditCode":
                // SettingsSchema constructor arguments are independently inspected above.
                return;

            case LiteralExpressionSyntax literal:
                AddViolation(literal, violations, $"raw audit action literal {literal.Token.Text}");
                return;

            case InterpolatedStringExpressionSyntax:
                AddViolation(expression, violations, "interpolated audit action");
                return;

            default:
                AddViolation(expression, violations,
                    $"audit action expression '{expression}' is not catalog-backed");
                return;
        }
    }

    private static bool TryValidateIdentifierSource(
        IdentifierNameSyntax identifier,
        SyntaxNode root,
        IReadOnlySet<string> catalog,
        ICollection<string> violations,
        ISet<SyntaxNode> visited)
    {
        var name = identifier.Identifier.ValueText;
        var containingMethod = identifier.FirstAncestorOrSelf<MethodDeclarationSyntax>();
        if (containingMethod?.ParameterList.Parameters.Any(p => p.Identifier.ValueText == name) == true
            && name == "action"
            && containingMethod.Identifier.ValueText is "Build" or "LogAsync" or "SaveMutationWithAuditAsync")
        {
            // These are the two catalog-enforcing boundary implementations and the mapper's
            // transactional wrapper. Their production call sites are inspected separately.
            return true;
        }

        var lookupScope = containingMethod as SyntaxNode ?? root;
        var declaration = lookupScope.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .Where(d => d.Identifier.ValueText == name && d.SpanStart < identifier.SpanStart)
            .OrderByDescending(d => d.SpanStart)
            .FirstOrDefault();
        if (declaration is null)
            return false;

        var sources = new List<ExpressionSyntax>();
        if (declaration.Initializer is { } initializer)
            sources.Add(initializer.Value);

        var scope = declaration.FirstAncestorOrSelf<MethodDeclarationSyntax>() as SyntaxNode ?? root;
        sources.AddRange(scope.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(a => a.IsKind(SyntaxKind.SimpleAssignmentExpression)
                        && a.Left is IdentifierNameSyntax left
                        && left.Identifier.ValueText == name
                        && a.SpanStart > declaration.SpanStart
                        && a.SpanStart < identifier.SpanStart)
            .Select(a => a.Right));

        if (sources.Count == 0)
            return false;

        foreach (var source in sources)
            ValidateActionExpression(source, root, catalog, violations, visited);
        return true;
    }

    private static ExpressionSyntax Unwrap(ExpressionSyntax expression)
    {
        while (true)
        {
            expression = expression switch
            {
                ParenthesizedExpressionSyntax parenthesized => parenthesized.Expression,
                CastExpressionSyntax cast => cast.Expression,
                CheckedExpressionSyntax checkedExpression => checkedExpression.Expression,
                _ => expression,
            };

            if (expression is not (ParenthesizedExpressionSyntax or CastExpressionSyntax or CheckedExpressionSyntax))
                return expression;
        }
    }

    private static void AddViolation(SyntaxNode node, ICollection<string> violations, string message)
    {
        var location = node.GetLocation().GetLineSpan();
        var file = Path.GetFileName(location.Path);
        violations.Add($"{file}:{location.StartLinePosition.Line + 1}: {message}");
    }

    private static string? InvocationName(InvocationExpressionSyntax invocation) => invocation.Expression switch
    {
        MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
        _ => null,
    };

    private static ArgumentSyntax? NamedOrPositionalArgument(
        BaseArgumentListSyntax arguments,
        string parameterName,
        int position)
    {
        var named = arguments.Arguments
            .FirstOrDefault(a => a.NameColon?.Name.Identifier.ValueText == parameterName);
        return named ?? (arguments.Arguments.Count > position ? arguments.Arguments[position] : null);
    }

    private static bool IsAuditActionsType(ExpressionSyntax expression) =>
        expression.ToString().Equals("AuditActions", StringComparison.Ordinal)
        || expression.ToString().EndsWith(".AuditActions", StringComparison.Ordinal);

    private static IReadOnlyDictionary<string, string> CatalogConstants() =>
        typeof(AuditActions)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f is { IsLiteral: true, IsInitOnly: false } && f.FieldType == typeof(string))
            .ToDictionary(f => f.Name, f => (string)f.GetRawConstantValue()!, StringComparer.Ordinal);

    private static IEnumerable<string> ProductionSourceFiles()
    {
        var srcDir = Path.Combine(FindRepoRoot(), "src");
        Directory.Exists(srcDir).Should().BeTrue($"production source directory must exist at {srcDir}");
        return Directory.EnumerateFiles(srcDir, "*.cs", SearchOption.AllDirectories)
            .Where(p => !HasPathSegment(p, "obj") && !HasPathSegment(p, "bin"));
    }

    private static bool HasPathSegment(string path, string segment) =>
        path.Contains($"{Path.DirectorySeparatorChar}{segment}{Path.DirectorySeparatorChar}",
            StringComparison.OrdinalIgnoreCase);

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 10 && dir is not null; i++, dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "NodePilot.slnx")))
                return dir.FullName;
        throw new InvalidOperationException($"Could not locate NodePilot.slnx walking up from {AppContext.BaseDirectory}");
    }
}

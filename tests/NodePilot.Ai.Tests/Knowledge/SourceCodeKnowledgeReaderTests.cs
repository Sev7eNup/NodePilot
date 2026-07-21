using FluentAssertions;
using NodePilot.Ai;
using NodePilot.Ai.Knowledge;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Ai.Tests.Knowledge;

/// <summary>
/// The four safety layers of the source-code reader (traversal guard, secret-file DENY, extension
/// allowlist, size caps) — exercised through the PUBLIC Search/Read API (behaviour, not internals).
/// </summary>
public sealed class SourceCodeKnowledgeReaderTests : IDisposable
{
    private readonly string _root;

    public SourceCodeKnowledgeReaderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "npk-src-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "src", "NodePilot.Api"));
        Directory.CreateDirectory(Path.Combine(_root, "secrets", "data-protection-keys"));
        Write("src/NodePilot.Api/Program.cs", "using System; class Program { /* needlemarker */ }");
        Write("src/NodePilot.Api/appsettings.json", "{ \"Jwt\": { \"Key\": \"topsecret needlemarker\" } }");
        Write("src/NodePilot.Api/appsettings.Development.json", "{ \"needlemarker\": true }");
        Write("jwt-secret.key", "SECRET needlemarker");
        Write("cert.pfx", "binary needlemarker");
        Write("service.pem", "-----BEGIN needlemarker");
        Write(".env", "PASSWORD=needlemarker");
        Write("secrets/data-protection-keys/key-1.xml", "<key>needlemarker</key>");
        Write("data.json", "{ \"needlemarker\": 1 }");
        Write("CredentialStore.cs", "class CredentialStore { /* legit needlemarker code */ }");
    }

    private void Write(string rel, string content)
    {
        var full = Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private SourceCodeKnowledgeReader Reader(int maxFileBytes = 262_144)
        => new(new StaticOptionsMonitor<AiKnowledgeOptions>(new AiKnowledgeOptions
        {
            SourceCodeEnabled = true,
            SourceCodeRootPath = _root,
            SourceCodeMaxFileBytes = maxFileBytes,
            SourceCodeMaxResults = 20,
        }));

    [Fact]
    public void IsAvailable_TrueForExistingRoot_FalseForMissing()
    {
        Reader().IsAvailable().Should().BeTrue();
        var missing = new SourceCodeKnowledgeReader(new StaticOptionsMonitor<AiKnowledgeOptions>(
            new AiKnowledgeOptions { SourceCodeRootPath = Path.Combine(_root, "nope") }));
        missing.IsAvailable().Should().BeFalse();
    }

    [Fact]
    public void Read_AllowlistedSourceFile_ReturnsContent()
    {
        var r = Reader().Read("src/NodePilot.Api/Program.cs");
        r.Ok.Should().BeTrue();
        r.Content.Should().Contain("class Program");
        r.Path.Should().Be("src/NodePilot.Api/Program.cs");
    }

    [Fact]
    public void Read_LegitCredentialStoreCs_IsAllowed()
    {
        // DENY is filename-pattern based (appsettings/jwt-secret/…), NOT a substring "credential"
        // match — CredentialStore.cs must stay readable.
        Reader().Read("CredentialStore.cs").Ok.Should().BeTrue();
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("..\\..\\Windows\\win.ini")]
    [InlineData("/etc/passwd")]
    [InlineData("C:/Windows/System32/drivers/etc/hosts")]
    public void Read_TraversalOrAbsolute_IsRejected(string path)
    {
        Reader().Read(path).Ok.Should().BeFalse();
    }

    [Theory]
    [InlineData("src/NodePilot.Api/appsettings.json")]
    [InlineData("src/NodePilot.Api/appsettings.Development.json")]
    [InlineData("jwt-secret.key")]
    [InlineData("cert.pfx")]
    [InlineData("service.pem")]
    [InlineData(".env")]
    [InlineData("secrets/data-protection-keys/key-1.xml")]
    public void Read_SecretOrConfigFile_IsDenied(string path)
    {
        var r = Reader().Read(path);
        r.Ok.Should().BeFalse();
    }

    [Fact]
    public void Read_NonAllowlistedExtension_IsRejected()
    {
        // .json is deliberately not in the allowlist (keeps appsettings out even by a fresh name).
        Reader().Read("data.json").Ok.Should().BeFalse();
    }

    [Fact]
    public void Read_OversizedFile_IsSkipped()
    {
        Write("Big.cs", new string('x', 5_000));
        Reader(maxFileBytes: 1_000).Read("Big.cs").Ok.Should().BeFalse();
    }

    [Fact]
    public void Search_FindsAllowlistedSource_ButNotDeniedOrNonAllowlisted()
    {
        var hits = Reader().Search("needlemarker");
        var paths = hits.Select(h => h.Path).ToList();

        paths.Should().Contain("src/NodePilot.Api/Program.cs");
        paths.Should().Contain("CredentialStore.cs");
        // Secrets / config / non-allowlisted must never surface, even though they contain the term.
        paths.Should().NotContain(p => p.Contains("appsettings"));
        paths.Should().NotContain(p => p.EndsWith("jwt-secret.key"));
        paths.Should().NotContain(p => p.EndsWith(".pfx") || p.EndsWith(".pem") || p.EndsWith(".env"));
        paths.Should().NotContain(p => p.EndsWith("data.json"));
        paths.Should().NotContain(p => p.Contains("data-protection-keys"));
    }

    [Fact]
    public void Search_EmptyQuery_ReturnsNothing()
    {
        Reader().Search("").Should().BeEmpty();
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort temp cleanup */ }
    }
}

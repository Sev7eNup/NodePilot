using System.IO;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Engine.Activities;
using NodePilot.Engine.PowerShell;
using NodePilot.Engine.Tests.Helpers;
using Xunit;

namespace NodePilot.Engine.Tests.Activities;

/// <summary>
/// Tests for <see cref="TextFileEditActivity"/>. Covers C#-side config validation,
/// PowerShell script generation (markers + quoting + per-op skeleton), and PostProcess
/// JSON parsing. The PowerShell helper functions themselves (BOM-sniff, atomic write,
/// line-ending preservation) are exercised manually against real files — they run inside
/// the WinRM script body and aren't unit-testable from .NET without spinning up a
/// PowerShell engine. The Engine-Tests in this file therefore focus on the boundary the
/// C# code owns: what gets emitted, how config errors surface, and how the structured
/// result JSON maps into <c>ActivityResult.OutputParameters</c>.
/// </summary>
public sealed class TextFileEditActivityTests : IDisposable
{
    private readonly Data.NodePilotDbContext _db;
    private readonly Mock<ICredentialStore> _credentialStore;
    private readonly Mock<IRemoteSessionFactory> _sessionFactory;
    private readonly Mock<IRemoteSession> _mockSession;
    private readonly PowerShellEngineFactory _engineFactory = new(NullLoggerFactory.Instance);
    private readonly Guid _machineId = Guid.NewGuid();
    private readonly Guid _credentialId = Guid.NewGuid();
    private string? _capturedScript;
    private string _scriptOutput = "OK";

    public TextFileEditActivityTests()
    {
        _db = TestDbContext.Create();
        _credentialStore = new Mock<ICredentialStore>();

        _mockSession = new Mock<IRemoteSession>();
        _mockSession
            .Setup(s => s.ExecuteScriptAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .Callback<string, int?, CancellationToken>((script, _, _) => _capturedScript = script)
            .ReturnsAsync(() => new RemoteExecutionResult { Success = true, Output = _scriptOutput });
        _mockSession.Setup(s => s.DisposeAsync()).Returns(ValueTask.CompletedTask);

        _sessionFactory = new Mock<IRemoteSessionFactory>();
        _sessionFactory
            .Setup(f => f.CreateSessionAsync(It.IsAny<ManagedMachine>(), It.IsAny<Credential>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(_mockSession.Object);

        _db.Credentials.Add(new Credential { Id = _credentialId, Name = "C", Username = "u", EncryptedPassword = new byte[] { 1 } });
        _db.ManagedMachines.Add(new ManagedMachine
        {
            Id = _machineId, Name = "S", Hostname = "host.local",
            WinRmPort = 5985, DefaultCredentialId = _credentialId, IsReachable = true
        });
        _db.SaveChanges();

        _credentialStore
            .Setup(cs => cs.GetAsync(_credentialId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Credential { Id = _credentialId, Name = "C", Username = "u", EncryptedPassword = new byte[] { 1 } });
    }

    public void Dispose() => _db.Dispose();

    private TextFileEditActivity CreateActivity(IConfiguration? cfg = null)
        => new(_sessionFactory.Object, _credentialStore.Object, _db, _engineFactory,
               cfg ?? new ConfigurationBuilder().Build());

    private StepExecutionContext Ctx()
        => new() { WorkflowExecutionId = Guid.NewGuid(), StepId = "step-1", TargetMachineId = _machineId, CredentialId = _credentialId };

    private static JsonElement Cfg(string json) => JsonDocument.Parse(json).RootElement;

    // ---- Error cases: config validation ----

    [Fact]
    public async Task MissingOperation_Throws()
    {
        Func<Task> act = () => CreateActivity().ExecuteAsync(Ctx(),
            Cfg("{\"path\":\"C:\\\\f.txt\"}"), CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*operation*");
    }

    [Fact]
    public async Task UnknownOperation_Throws()
    {
        Func<Task> act = () => CreateActivity().ExecuteAsync(Ctx(),
            Cfg("{\"operation\":\"truncate\",\"path\":\"C:\\\\f.txt\"}"), CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*unknown operation*");
    }

    [Fact]
    public async Task MissingPath_Throws()
    {
        Func<Task> act = () => CreateActivity().ExecuteAsync(Ctx(),
            Cfg("{\"operation\":\"append\",\"content\":\"x\"}"), CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*path*");
    }

    [Fact]
    public async Task AppendWithoutContent_Throws()
    {
        Func<Task> act = () => CreateActivity().ExecuteAsync(Ctx(),
            Cfg("{\"operation\":\"append\",\"path\":\"C:\\\\f.txt\"}"), CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*content*");
    }

    [Theory]
    [InlineData("insert")]
    [InlineData("replaceLine")]
    public async Task LineOpWithoutLineNumber_Throws(string op)
    {
        Func<Task> act = () => CreateActivity().ExecuteAsync(Ctx(),
            Cfg($"{{\"operation\":\"{op}\",\"path\":\"C:\\\\f.txt\",\"content\":\"x\"}}"),
            CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*lineNumber*");
    }

    [Fact]
    public async Task ReplaceWithoutMatchPattern_Throws()
    {
        Func<Task> act = () => CreateActivity().ExecuteAsync(Ctx(),
            Cfg("{\"operation\":\"replace\",\"path\":\"C:\\\\f.txt\",\"replace\":\"x\"}"),
            CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*matchPattern*");
    }

    [Fact]
    public async Task ReplaceWithoutReplaceField_Throws()
    {
        Func<Task> act = () => CreateActivity().ExecuteAsync(Ctx(),
            Cfg("{\"operation\":\"replace\",\"path\":\"C:\\\\f.txt\",\"matchPattern\":\"foo\"}"),
            CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*'replace'*");
    }

    [Fact]
    public async Task DeleteWithoutAnySelector_Throws()
    {
        Func<Task> act = () => CreateActivity().ExecuteAsync(Ctx(),
            Cfg("{\"operation\":\"delete\",\"path\":\"C:\\\\f.txt\"}"), CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*lineNumber*lineRange*matchPattern*");
    }

    [Fact]
    public async Task DeleteWithMultipleSelectors_Throws()
    {
        Func<Task> act = () => CreateActivity().ExecuteAsync(Ctx(),
            Cfg("{\"operation\":\"delete\",\"path\":\"C:\\\\f.txt\",\"lineNumber\":2,\"matchPattern\":\"x\"}"),
            CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*only one*");
    }

    [Fact]
    public async Task LineRange_RejectsNonArray()
    {
        Func<Task> act = () => CreateActivity().ExecuteAsync(Ctx(),
            Cfg("{\"operation\":\"delete\",\"path\":\"C:\\\\f.txt\",\"lineRange\":42}"),
            CancellationToken.None);
        // lineRange of 42 is not an array, so the validator counts hasLineRange=false and
        // reports the "delete without selector" message — surface the friendly path.
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task LineRange_RejectsReversedBounds()
    {
        Func<Task> act = () => CreateActivity().ExecuteAsync(Ctx(),
            Cfg("{\"operation\":\"delete\",\"path\":\"C:\\\\f.txt\",\"lineRange\":[10,3]}"),
            CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*ordered*");
    }

    [Fact]
    public async Task UnknownEncoding_Throws()
    {
        Func<Task> act = () => CreateActivity().ExecuteAsync(Ctx(),
            Cfg("{\"operation\":\"append\",\"path\":\"C:\\\\f.txt\",\"content\":\"x\",\"encoding\":\"iso-8859-1\"}"),
            CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*encoding*");
    }

    [Fact]
    public async Task UnknownLineEnding_Throws()
    {
        Func<Task> act = () => CreateActivity().ExecuteAsync(Ctx(),
            Cfg("{\"operation\":\"append\",\"path\":\"C:\\\\f.txt\",\"content\":\"x\",\"lineEnding\":\"cr\"}"),
            CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*lineEnding*");
    }

    [Fact]
    public async Task UnknownOccurrences_Throws()
    {
        Func<Task> act = () => CreateActivity().ExecuteAsync(Ctx(),
            Cfg("{\"operation\":\"replace\",\"path\":\"C:\\\\f.txt\",\"matchPattern\":\"a\",\"replace\":\"b\",\"occurrences\":\"two\"}"),
            CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*occurrences*");
    }

    [Fact]
    public async Task BackupSuffix_WithSeparator_Throws()
    {
        // The leaf-name validator must catch suffixes that would slip a separator into the
        // backup-path computation. Without this check, ".bak/../passwd" could escape.
        Func<Task> act = () => CreateActivity().ExecuteAsync(Ctx(),
            Cfg("{\"operation\":\"append\",\"path\":\"C:\\\\f.txt\",\"content\":\"x\",\"backupSuffix\":\"/etc\"}"),
            CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task WildcardPath_Rejected()
    {
        Func<Task> act = () => CreateActivity().ExecuteAsync(Ctx(),
            Cfg("{\"operation\":\"append\",\"path\":\"C:\\\\logs\\\\*.txt\",\"content\":\"x\"}"),
            CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*wildcard*");
    }

    [Fact]
    public async Task UncPath_Rejected()
    {
        Func<Task> act = () => CreateActivity().ExecuteAsync(Ctx(),
            Cfg("{\"operation\":\"append\",\"path\":\"\\\\\\\\attacker\\\\share\\\\f.txt\",\"content\":\"x\"}"),
            CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*UNC*");
    }

    // ---- Script generation: per-op skeleton ----

    [Fact]
    public async Task Append_EmitsAppendBranch()
    {
        await CreateActivity().ExecuteAsync(Ctx(),
            Cfg("{\"operation\":\"append\",\"path\":\"C:\\\\f.txt\",\"content\":\"hello\"}"),
            CancellationToken.None);
        _capturedScript.Should().NotBeNullOrEmpty();
        _capturedScript.Should().Contain("$__op = 'append'");
        _capturedScript.Should().Contain("$__path = 'C:\\f.txt'");
        _capturedScript.Should().Contain("$__content = 'hello'");
        // Append branch in the switch must be present
        _capturedScript.Should().Contain("'append'");
    }

    [Fact]
    public async Task Insert_EmitsLineNumberLiteral()
    {
        await CreateActivity().ExecuteAsync(Ctx(),
            Cfg("{\"operation\":\"insert\",\"path\":\"C:\\\\f.txt\",\"content\":\"x\",\"lineNumber\":42}"),
            CancellationToken.None);
        _capturedScript.Should().Contain("$__lineNumber = 42");
    }

    [Fact]
    public async Task DeleteRange_EmitsBothBounds()
    {
        await CreateActivity().ExecuteAsync(Ctx(),
            Cfg("{\"operation\":\"delete\",\"path\":\"C:\\\\f.txt\",\"lineRange\":[3,7]}"),
            CancellationToken.None);
        _capturedScript.Should().Contain("$__rangeFrom = 3");
        _capturedScript.Should().Contain("$__rangeTo = 7");
    }

    [Fact]
    public async Task Replace_PassesRegexAndIgnoreCaseFlags()
    {
        await CreateActivity().ExecuteAsync(Ctx(),
            Cfg("{\"operation\":\"replace\",\"path\":\"C:\\\\f.txt\",\"matchPattern\":\"foo\",\"replace\":\"bar\",\"useRegex\":true,\"ignoreCase\":true,\"occurrences\":\"first\"}"),
            CancellationToken.None);
        _capturedScript.Should().Contain("$__useRegex = $true");
        _capturedScript.Should().Contain("$__ignoreCase = $true");
        _capturedScript.Should().Contain("$__occurrencesAll = $false");
        _capturedScript.Should().Contain("$__matchPattern = 'foo'");
        _capturedScript.Should().Contain("$__replacement = 'bar'");
    }

    [Fact]
    public async Task DryRun_FlagPropagated()
    {
        await CreateActivity().ExecuteAsync(Ctx(),
            Cfg("{\"operation\":\"append\",\"path\":\"C:\\\\f.txt\",\"content\":\"x\",\"dryRun\":true}"),
            CancellationToken.None);
        _capturedScript.Should().Contain("$__dryRun = $true");
    }

    [Fact]
    public async Task MaxFileSize_DefaultsTo50Mb()
    {
        await CreateActivity().ExecuteAsync(Ctx(),
            Cfg("{\"operation\":\"append\",\"path\":\"C:\\\\f.txt\",\"content\":\"x\"}"),
            CancellationToken.None);
        _capturedScript.Should().Contain("$__maxFileSizeBytes = 50L * 1024 * 1024");
    }

    [Fact]
    public async Task MaxFileSize_OverridableViaConfig()
    {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FileSystemOperation:TextEdit:MaxFileSizeMB"] = "200",
            })
            .Build();
        await CreateActivity(cfg).ExecuteAsync(Ctx(),
            Cfg("{\"operation\":\"append\",\"path\":\"C:\\\\f.txt\",\"content\":\"x\"}"),
            CancellationToken.None);
        _capturedScript.Should().Contain("$__maxFileSizeBytes = 200L * 1024 * 1024");
    }

    [Fact]
    public async Task PerStepMaxFileSize_OverridesGlobal()
    {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FileSystemOperation:TextEdit:MaxFileSizeMB"] = "10",
            })
            .Build();
        await CreateActivity(cfg).ExecuteAsync(Ctx(),
            Cfg("{\"operation\":\"append\",\"path\":\"C:\\\\f.txt\",\"content\":\"x\",\"maxFileSizeMB\":500}"),
            CancellationToken.None);
        _capturedScript.Should().Contain("$__maxFileSizeBytes = 500L * 1024 * 1024");
    }

    // ---- Apostrophe / variable-injection escaping ----

    [Fact]
    public async Task PathWithApostrophe_IsEscapedInScript()
    {
        await CreateActivity().ExecuteAsync(Ctx(),
            Cfg("{\"operation\":\"append\",\"path\":\"C:\\\\O'Brian's file.txt\",\"content\":\"x\"}"),
            CancellationToken.None);
        _capturedScript.Should().Contain("'C:\\O''Brian''s file.txt'");
    }

    [Fact]
    public async Task ContentWithApostropheAndQuotes_IsEscapedInScript()
    {
        // The {{step.output}}-resolved value can legitimately contain apostrophes
        // (e.g. SQL strings) — those must double-escape inside the PS literal, not break
        // out. This is the same defense FileOperationActivityTests exercises for paths.
        await CreateActivity().ExecuteAsync(Ctx(),
            Cfg("{\"operation\":\"append\",\"path\":\"C:\\\\f.txt\",\"content\":\"isn't \\\"quoted\\\"\"}"),
            CancellationToken.None);
        _capturedScript.Should().Contain("'isn''t \"quoted\"'");
    }

    // ---- PostProcess ----

    private static string MarkerOutput(string json) => $$"""
        ###NODEPILOT_TEXTEDIT_RESULT_START###
        {{json}}
        ###NODEPILOT_TEXTEDIT_RESULT_END###
        """;

    [Fact]
    public async Task SuccessResult_ProjectsAllParameters()
    {
        _scriptOutput = MarkerOutput("{\"operation\":\"replace\",\"path\":\"C:\\\\f.txt\",\"ok\":true,\"linesBefore\":10,\"linesAfter\":10,\"linesChanged\":3,\"encoding\":\"utf8\",\"lineEnding\":\"crlf\",\"backupPath\":\"C:\\\\f.txt.bak\",\"dryRun\":false,\"summary\":\"replace: 3 line(s) changed in C:\\\\f.txt\"}");

        var result = await CreateActivity().ExecuteAsync(Ctx(),
            Cfg("{\"operation\":\"replace\",\"path\":\"C:\\\\f.txt\",\"matchPattern\":\"a\",\"replace\":\"b\"}"),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Output.Should().Be("replace: 3 line(s) changed in C:\\f.txt");
        result.OutputParameters["operation"].Should().Be("replace");
        result.OutputParameters["path"].Should().Be("C:\\f.txt");
        result.OutputParameters["linesBefore"].Should().Be("10");
        result.OutputParameters["linesAfter"].Should().Be("10");
        result.OutputParameters["linesChanged"].Should().Be("3");
        result.OutputParameters["encoding"].Should().Be("utf8");
        result.OutputParameters["lineEnding"].Should().Be("crlf");
        result.OutputParameters["backupPath"].Should().Be("C:\\f.txt.bak");
        result.OutputParameters["dryRun"].Should().Be("false");
    }

    [Fact]
    public async Task FailureResult_SurfacesStructuredError()
    {
        _scriptOutput = MarkerOutput("{\"operation\":\"replaceLine\",\"path\":\"C:\\\\f.txt\",\"ok\":false,\"error\":\"replaceLine: lineNumber 99 is out of range (file has 4 lines)\"}");

        var result = await CreateActivity().ExecuteAsync(Ctx(),
            Cfg("{\"operation\":\"replaceLine\",\"path\":\"C:\\\\f.txt\",\"content\":\"x\",\"lineNumber\":99}"),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorOutput.Should().Contain("out of range");
    }

    // ---- appendIfMissing semantics (regression: Finding 4) ----
    //
    // The UI surfaces the toggle as "Trim-exact match" (true) vs "Substring-match"
    // (false). The engine must honor both modes — exact-after-trim with `-eq`, or
    // case-insensitive substring with `IndexOf`. A previous revision compared with `-eq`
    // in both branches (just with/without trim), which made the toggle effectively a
    // no-op and contradicted the UI promise.

    [Fact]
    public async Task AppendIfMissingExact_EmitsTrimAndEqualityComparison()
    {
        await CreateActivity().ExecuteAsync(Ctx(),
            Cfg("{\"operation\":\"append\",\"path\":\"C:\\\\hosts\",\"content\":\"127.0.0.1 srv\",\"appendIfMissing\":true,\"appendIfMissingExact\":true}"),
            CancellationToken.None);
        _capturedScript.Should().Contain("$__appendIfMissingExact = $true");
        _capturedScript.Should().Contain("$l.Trim() -eq $__needle");
    }

    [Fact]
    public async Task AppendIfMissingFuzzy_EmitsCaseInsensitiveSubstringSearch()
    {
        await CreateActivity().ExecuteAsync(Ctx(),
            Cfg("{\"operation\":\"append\",\"path\":\"C:\\\\hosts\",\"content\":\"srv.lan\",\"appendIfMissing\":true,\"appendIfMissingExact\":false}"),
            CancellationToken.None);
        _capturedScript.Should().Contain("$__appendIfMissingExact = $false");
        _capturedScript.Should().Contain("IndexOf($__needle, [System.StringComparison]::OrdinalIgnoreCase)");
    }

    // ---- Replace: trailing-newline propagation (regression: Finding 3) ----
    //
    // Before the fix, $__hadTrailingNewline was only set during the initial file read.
    // The replace branch then operated on $__lines (which the read had stripped of its
    // implicit trailing empty element), so a replacement that *introduced* a final
    // newline got silently swallowed by the second Split-strip — and a replacement that
    // *removed* the final newline still got re-added by the materialize step. We pin the
    // emitted PowerShell here because the actual file-IO runs against the WinRM session
    // and isn't exercisable from C# tests directly.

    [Fact]
    public async Task Replace_ReAnchorsTrailingNewlineAfterReplacement()
    {
        await CreateActivity().ExecuteAsync(Ctx(),
            Cfg("{\"operation\":\"replace\",\"path\":\"C:\\\\f.txt\",\"matchPattern\":\"a\",\"replace\":\"b\"}"),
            CancellationToken.None);
        // The fix introduces a per-replace local that mirrors $__newText's trailing-state
        // and overwrites $__hadTrailingNewline so the materialize step sees the post-
        // replace shape rather than the pre-replace shape.
        _capturedScript.Should().Contain("$__newTextHasTrailingNewline");
        _capturedScript.Should().Contain("$__hadTrailingNewline = $__newTextHasTrailingNewline");
    }

    [Fact]
    public async Task UnparseableOutput_FailsClean()
    {
        _scriptOutput = "###NODEPILOT_TEXTEDIT_RESULT_START###\nnot-json-at-all\n###NODEPILOT_TEXTEDIT_RESULT_END###";

        var result = await CreateActivity().ExecuteAsync(Ctx(),
            Cfg("{\"operation\":\"append\",\"path\":\"C:\\\\f.txt\",\"content\":\"x\"}"),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorOutput.Should().Contain("could not parse result JSON");
    }

    // ---- Single-line append: [char].Trim regression ----
    //
    // [regex]::Split returns a 1-element array for single-line content, and PowerShell
    // unwraps a 1-element array assigned out of an if-expression to a scalar string — so
    // $__contentLines[0] became a [char] and .Trim() threw
    // "[System.Char] does not contain a method named 'Trim'". The [string[]] cast pins the
    // array type. First test guards the emitted script; the second runs the *real* generated
    // PowerShell (localhost bypass) against a temp file so the runtime failure can't come back.

    [Fact]
    public async Task Append_ForcesContentLinesToArray()
    {
        await CreateActivity().ExecuteAsync(Ctx(),
            Cfg("{\"operation\":\"append\",\"path\":\"C:\\\\f.txt\",\"content\":\"x\"}"),
            CancellationToken.None);
        _capturedScript.Should().Contain("[string[]]$__contentLines");
    }

    [Fact]
    public async Task Append_SingleLineContent_RunsAgainstRealFile()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"np-textedit-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(tmp, "seed line\n");
        try
        {
            var localMachineId = Guid.NewGuid();
            _db.ManagedMachines.Add(new ManagedMachine
            {
                Id = localMachineId, Name = "Local", Hostname = "localhost",
                WinRmPort = 5985, DefaultCredentialId = null, IsReachable = true
            });
            _db.SaveChanges();

            var activity = new TextFileEditActivity(
                _sessionFactory.Object, _credentialStore.Object, _db, _engineFactory,
                new ConfigurationBuilder().Build());
            var ctx = new StepExecutionContext
            {
                WorkflowExecutionId = Guid.NewGuid(), StepId = "step-1",
                TargetMachineId = localMachineId, CredentialId = null,
            };

            var result = await activity.ExecuteAsync(ctx,
                Cfg("{\"operation\":\"append\",\"path\":" + JsonSerializer.Serialize(tmp)
                    + ",\"content\":\"appended single line\"}"),
                CancellationToken.None);

            result.Success.Should().BeTrue(because: result.ErrorOutput ?? "no error");
            var content = await File.ReadAllTextAsync(tmp);
            content.Should().Contain("seed line");
            content.Should().Contain("appended single line");
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }
}

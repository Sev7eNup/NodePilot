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

public sealed class RegistryActivityTests : IDisposable
{
    private readonly Data.NodePilotDbContext _db;
    private readonly Mock<ICredentialStore> _credentialStore;
    private readonly Mock<IRemoteSessionFactory> _sessionFactory;
    private readonly PowerShellEngineFactory _engineFactory = new(NullLoggerFactory.Instance);
    private readonly Guid _machineId = Guid.NewGuid();
    private readonly Guid _credentialId = Guid.NewGuid();
    private string? _capturedScript;
    private string _mockOutput =
        "###NODEPILOT_REGISTRY_RESULT_START###\n" +
        "{\"operation\":\"read\",\"ok\":true}\n" +
        "###NODEPILOT_REGISTRY_RESULT_END###";

    public RegistryActivityTests()
    {
        _db = TestDbContext.Create();
        _credentialStore = new Mock<ICredentialStore>();

        var mockSession = new Mock<IRemoteSession>();
        mockSession
            .Setup(s => s.ExecuteScriptAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .Callback<string, int?, CancellationToken>((script, _, _) => _capturedScript = script)
            .ReturnsAsync(() => new RemoteExecutionResult { Success = true, Output = _mockOutput });
        mockSession.Setup(s => s.DisposeAsync()).Returns(ValueTask.CompletedTask);

        _sessionFactory = new Mock<IRemoteSessionFactory>();
        _sessionFactory
            .Setup(f => f.CreateSessionAsync(It.IsAny<ManagedMachine>(), It.IsAny<Credential>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockSession.Object);

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

    private RegistryActivity CreateActivity()
        => new(_sessionFactory.Object, _credentialStore.Object, _db, _engineFactory,
               new ConfigurationBuilder().Build());

    private StepExecutionContext Ctx()
        => new() { WorkflowExecutionId = Guid.NewGuid(), StepId = "step-1", TargetMachineId = _machineId, CredentialId = _credentialId };

    private static JsonElement Cfg(string json) => JsonDocument.Parse(json).RootElement;

    // ---- Error cases ----

    [Fact]
    public async Task MissingKeyPath_Throws()
    {
        var activity = CreateActivity();
        Func<Task> act = () => activity.ExecuteAsync(Ctx(),
            Cfg("{\"operation\": \"read\"}"),
            CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*keyPath*");
    }

    [Fact]
    public async Task WriteWithoutValueName_Throws()
    {
        var activity = CreateActivity();
        Func<Task> act = () => activity.ExecuteAsync(Ctx(),
            Cfg("{\"operation\": \"write\", \"keyPath\": \"HKLM:\\\\SOFTWARE\\\\Test\"}"),
            CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*valueName*");
    }

    [Fact]
    public async Task DeleteValueWithoutValueName_Throws()
    {
        var activity = CreateActivity();
        Func<Task> act = () => activity.ExecuteAsync(Ctx(),
            Cfg("{\"operation\": \"deleteValue\", \"keyPath\": \"HKLM:\\\\SOFTWARE\\\\Test\"}"),
            CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*valueName*");
    }

    [Fact]
    public async Task UnknownOperation_Throws()
    {
        var activity = CreateActivity();
        Func<Task> act = () => activity.ExecuteAsync(Ctx(),
            Cfg("{\"operation\": \"purge\", \"keyPath\": \"HKLM:\\\\SOFTWARE\\\\Test\"}"),
            CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*purge*");
    }

    [Fact]
    public async Task Write_UnknownValueType_Throws()
    {
        var activity = CreateActivity();
        Func<Task> act = () => activity.ExecuteAsync(Ctx(),
            Cfg("{\"operation\": \"write\", \"keyPath\": \"HKLM:\\\\SOFTWARE\\\\Test\", \"valueName\": \"X\", \"value\": \"1\", \"valueType\": \"Bogus\"}"),
            CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Bogus*");
    }

    // ---- Script generation (substring) ----

    [Fact]
    public async Task Read_GeneratesGetItemPropertyValueScript()
    {
        _capturedScript = null;
        await CreateActivity().ExecuteAsync(Ctx(),
            Cfg("{\"operation\": \"read\", \"keyPath\": \"HKLM:\\\\SOFTWARE\\\\Test\", \"valueName\": \"MyValue\"}"),
            CancellationToken.None);
        _capturedScript.Should().Contain("Get-ItemPropertyValue");
        _capturedScript.Should().Contain("MyValue");
    }

    [Fact]
    public async Task ReadAll_GeneratesGetItemScript()
    {
        _capturedScript = null;
        await CreateActivity().ExecuteAsync(Ctx(),
            Cfg("{\"operation\": \"read\", \"keyPath\": \"HKLM:\\\\SOFTWARE\\\\Test\"}"),
            CancellationToken.None);
        _capturedScript.Should().Contain("Get-Item -LiteralPath");
        _capturedScript.Should().Contain("$__result.values");
    }

    [Fact]
    public async Task Write_GeneratesSetItemPropertyScript()
    {
        _capturedScript = null;
        await CreateActivity().ExecuteAsync(Ctx(),
            Cfg("{\"operation\": \"write\", \"keyPath\": \"HKLM:\\\\SOFTWARE\\\\Test\", \"valueName\": \"MyVal\", \"value\": \"data\"}"),
            CancellationToken.None);
        _capturedScript.Should().Contain("Set-ItemProperty");
        _capturedScript.Should().Contain("-Type String");
    }

    [Fact]
    public async Task Write_DWord_GeneratesIntCast()
    {
        _capturedScript = null;
        await CreateActivity().ExecuteAsync(Ctx(),
            Cfg("{\"operation\": \"write\", \"keyPath\": \"HKLM:\\\\SOFTWARE\\\\Test\", \"valueName\": \"Count\", \"value\": \"42\", \"valueType\": \"DWord\"}"),
            CancellationToken.None);
        _capturedScript.Should().Contain("-Type DWord");
        _capturedScript.Should().Contain("[int]$__rawValue");
    }

    [Fact]
    public async Task Write_Binary_GeneratesByteParser()
    {
        _capturedScript = null;
        await CreateActivity().ExecuteAsync(Ctx(),
            Cfg("{\"operation\": \"write\", \"keyPath\": \"HKLM:\\\\SOFTWARE\\\\Test\", \"valueName\": \"Blob\", \"value\": \"DEADBEEF\", \"valueType\": \"Binary\"}"),
            CancellationToken.None);
        _capturedScript.Should().Contain("-Type Binary");
        _capturedScript.Should().Contain("[Convert]::ToByte");
    }

    [Fact]
    public async Task DeleteValue_GeneratesRemoveItemPropertyScript()
    {
        _capturedScript = null;
        await CreateActivity().ExecuteAsync(Ctx(),
            Cfg("{\"operation\": \"deleteValue\", \"keyPath\": \"HKLM:\\\\SOFTWARE\\\\Test\", \"valueName\": \"MyVal\"}"),
            CancellationToken.None);
        _capturedScript.Should().Contain("Remove-ItemProperty");
        _capturedScript.Should().Contain("MyVal");
    }

    [Fact]
    public async Task DeleteKey_GeneratesRemoveItemRecursiveScript()
    {
        _capturedScript = null;
        await CreateActivity().ExecuteAsync(Ctx(),
            Cfg("{\"operation\": \"deleteKey\", \"keyPath\": \"HKLM:\\\\SOFTWARE\\\\Test\"}"),
            CancellationToken.None);
        _capturedScript.Should().Contain("Remove-Item -LiteralPath $__keyPath -Recurse -Force");
    }

    [Fact]
    public async Task CreateKey_GeneratesNewItemScript()
    {
        _capturedScript = null;
        await CreateActivity().ExecuteAsync(Ctx(),
            Cfg("{\"operation\": \"createKey\", \"keyPath\": \"HKLM:\\\\SOFTWARE\\\\Test\"}"),
            CancellationToken.None);
        _capturedScript.Should().Contain("New-Item -Path $__keyPath -Force");
        _capturedScript.Should().Contain("$__result.created");
    }

    [Fact]
    public async Task Exists_GeneratesTestPathScript()
    {
        _capturedScript = null;
        await CreateActivity().ExecuteAsync(Ctx(),
            Cfg("{\"operation\": \"exists\", \"keyPath\": \"HKLM:\\\\SOFTWARE\\\\Test\"}"),
            CancellationToken.None);
        _capturedScript.Should().Contain("Test-Path");
        _capturedScript.Should().Contain("$__result.exists");
    }

    [Fact]
    public async Task ExistsValue_ChecksPropertyContains()
    {
        _capturedScript = null;
        await CreateActivity().ExecuteAsync(Ctx(),
            Cfg("{\"operation\": \"exists\", \"keyPath\": \"HKLM:\\\\SOFTWARE\\\\Test\", \"valueName\": \"V\"}"),
            CancellationToken.None);
        _capturedScript.Should().Contain("$key.Property -contains $__valueName");
    }

    [Fact]
    public async Task ListSubKeys_GeneratesGetChildItemScript()
    {
        _capturedScript = null;
        await CreateActivity().ExecuteAsync(Ctx(),
            Cfg("{\"operation\": \"listSubKeys\", \"keyPath\": \"HKLM:\\\\SOFTWARE\\\\Test\"}"),
            CancellationToken.None);
        _capturedScript.Should().Contain("Get-ChildItem -LiteralPath");
        _capturedScript.Should().Contain("$__result.subKeys");
    }

    [Fact]
    public async Task ListValues_GeneratesPropertyEnumerationScript()
    {
        _capturedScript = null;
        await CreateActivity().ExecuteAsync(Ctx(),
            Cfg("{\"operation\": \"listValues\", \"keyPath\": \"HKLM:\\\\SOFTWARE\\\\Test\"}"),
            CancellationToken.None);
        _capturedScript.Should().Contain("$__result.values");
        _capturedScript.Should().Contain("$__result.count");
    }

    [Fact]
    public async Task KeyPathWithApostrophe_IsEscapedInScript()
    {
        _capturedScript = null;
        await CreateActivity().ExecuteAsync(Ctx(),
            Cfg("{\"operation\": \"read\", \"keyPath\": \"HKLM:\\\\SOFTWARE\\\\O'Brian\"}"),
            CancellationToken.None);
        _capturedScript.Should().Contain("O''Brian");
    }

    // ---- PostProcess (Marker-Block → OutputParameters) ----

    [Fact]
    public async Task Read_SingleValue_ProjectsValueAndType()
    {
        _mockOutput =
            "###NODEPILOT_REGISTRY_RESULT_START###\n" +
            "{\"operation\":\"read\",\"ok\":true,\"value\":\"v1\",\"type\":\"String\"}\n" +
            "###NODEPILOT_REGISTRY_RESULT_END###";

        var result = await CreateActivity().ExecuteAsync(Ctx(),
            Cfg("{\"operation\": \"read\", \"keyPath\": \"HKLM:\\\\SOFTWARE\\\\Test\", \"valueName\": \"V\"}"),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.OutputParameters["value"].Should().Be("v1");
        result.OutputParameters["type"].Should().Be("String");
        result.Output.Should().Be("v1");
    }

    [Fact]
    public async Task Exists_True_ProjectsExistsParam()
    {
        _mockOutput =
            "###NODEPILOT_REGISTRY_RESULT_START###\n" +
            "{\"operation\":\"exists\",\"ok\":true,\"exists\":true}\n" +
            "###NODEPILOT_REGISTRY_RESULT_END###";

        var result = await CreateActivity().ExecuteAsync(Ctx(),
            Cfg("{\"operation\": \"exists\", \"keyPath\": \"HKLM:\\\\SOFTWARE\\\\Test\"}"),
            CancellationToken.None);

        result.OutputParameters["exists"].Should().Be("true");
        result.Output.Should().Be("True");
    }

    [Fact]
    public async Task ListSubKeys_ProjectsArrayAndCount()
    {
        _mockOutput =
            "###NODEPILOT_REGISTRY_RESULT_START###\n" +
            "{\"operation\":\"listsubkeys\",\"ok\":true,\"subKeys\":[\"a\",\"b\",\"c\"],\"count\":3}\n" +
            "###NODEPILOT_REGISTRY_RESULT_END###";

        var result = await CreateActivity().ExecuteAsync(Ctx(),
            Cfg("{\"operation\": \"listSubKeys\", \"keyPath\": \"HKLM:\\\\SOFTWARE\\\\Test\"}"),
            CancellationToken.None);

        result.OutputParameters["subKeys"].Should().Be("[\"a\",\"b\",\"c\"]");
        result.OutputParameters["count"].Should().Be("3");
    }

    [Fact]
    public async Task ListValues_ProjectsValuesAndCount()
    {
        _mockOutput =
            "###NODEPILOT_REGISTRY_RESULT_START###\n" +
            "{\"operation\":\"listvalues\",\"ok\":true,\"values\":[{\"name\":\"X\",\"type\":\"DWord\",\"value\":1}],\"count\":1}\n" +
            "###NODEPILOT_REGISTRY_RESULT_END###";

        var result = await CreateActivity().ExecuteAsync(Ctx(),
            Cfg("{\"operation\": \"listValues\", \"keyPath\": \"HKLM:\\\\SOFTWARE\\\\Test\"}"),
            CancellationToken.None);

        result.OutputParameters["count"].Should().Be("1");
        result.OutputParameters["values"].Should().Contain("\"name\":\"X\"");
    }

    [Fact]
    public async Task CreateKey_ProjectsCreatedFlag()
    {
        _mockOutput =
            "###NODEPILOT_REGISTRY_RESULT_START###\n" +
            "{\"operation\":\"createkey\",\"ok\":true,\"created\":true}\n" +
            "###NODEPILOT_REGISTRY_RESULT_END###";

        var result = await CreateActivity().ExecuteAsync(Ctx(),
            Cfg("{\"operation\": \"createKey\", \"keyPath\": \"HKLM:\\\\SOFTWARE\\\\Test\"}"),
            CancellationToken.None);

        result.OutputParameters["created"].Should().Be("true");
        result.Output.Should().Be("Created");
    }

    [Fact]
    public async Task PowerShellError_ProjectsToFailedResult()
    {
        _mockOutput =
            "###NODEPILOT_REGISTRY_RESULT_START###\n" +
            "{\"operation\":\"read\",\"ok\":false,\"error\":\"Cannot find path\"}\n" +
            "###NODEPILOT_REGISTRY_RESULT_END###";

        var result = await CreateActivity().ExecuteAsync(Ctx(),
            Cfg("{\"operation\": \"read\", \"keyPath\": \"HKLM:\\\\SOFTWARE\\\\Missing\", \"valueName\": \"V\"}"),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorOutput.Should().Contain("Cannot find path");
    }
}

using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Moq;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using NodePilot.Engine.Activities;
using NodePilot.Engine.PowerShell;
using NodePilot.Engine.Tests.Helpers;
using Xunit;

namespace NodePilot.Engine.Tests.Activities;

public sealed class BuildScriptTests : IDisposable
{
    private readonly Data.NodePilotDbContext _db;
    private readonly Mock<ICredentialStore> _credentialStore;
    private readonly Guid _machineId = Guid.NewGuid();
    private readonly Guid _credentialId = Guid.NewGuid();

    private string? _capturedScript;
    private readonly Mock<IRemoteSessionFactory> _sessionFactory;
    private readonly PowerShellEngineFactory _engineFactory = new(NullLoggerFactory.Instance);
    private readonly IConfiguration _configuration = new ConfigurationBuilder().Build();

    public BuildScriptTests()
    {
        _db = TestDbContext.Create();
        _credentialStore = new Mock<ICredentialStore>();

        var mockSession = new Mock<IRemoteSession>();
        mockSession
            .Setup(s => s.ExecuteScriptAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .Callback<string, int?, CancellationToken>((script, _, _) => _capturedScript = script)
            .ReturnsAsync(new RemoteExecutionResult { Success = true, Output = "OK" });
        mockSession
            .Setup(s => s.DisposeAsync())
            .Returns(ValueTask.CompletedTask);

        _sessionFactory = new Mock<IRemoteSessionFactory>();
        _sessionFactory
            .Setup(f => f.CreateSessionAsync(It.IsAny<ManagedMachine>(), It.IsAny<Credential>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockSession.Object);

        SeedDatabase();
    }

    public void Dispose()
    {
        _db.Dispose();
    }

    private void SeedDatabase()
    {
        _db.Credentials.Add(new Credential
        {
            Id = _credentialId,
            Name = "TestCred",
            Username = "admin",
            EncryptedPassword = new byte[] { 1, 2, 3 }
        });
        _db.SaveChanges();

        _db.ManagedMachines.Add(new ManagedMachine
        {
            Id = _machineId,
            Name = "TestServer",
            Hostname = "test-server.local",
            WinRmPort = 5985,
            UseSsl = false,
            DefaultCredentialId = _credentialId,
            IsReachable = true
        });
        _db.SaveChanges();

        _credentialStore
            .Setup(cs => cs.GetAsync(_credentialId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Credential
            {
                Id = _credentialId,
                Name = "TestCred",
                Username = "admin",
                EncryptedPassword = new byte[] { 1, 2, 3 }
            });
    }

    private StepExecutionContext CreateContext() =>
        new()
        {
            WorkflowExecutionId = Guid.NewGuid(),
            StepId = "step-1",
            TargetMachineId = _machineId,
            CredentialId = _credentialId
        };

    private async Task<string?> ExecuteAndCaptureScript(IActivityExecutor activity, JsonElement config)
    {
        _capturedScript = null;
        await activity.ExecuteAsync(CreateContext(), config, CancellationToken.None);
        return _capturedScript;
    }

    private static JsonElement ParseConfig(string json) =>
        JsonDocument.Parse(json).RootElement;

    // RunScriptActivity now uses PowerShellEngineFactory (out-of-process), not BaseRemoteActivity.
    // Its tests are in a separate test class.

    // ---- ServiceManagementActivity ----

    [Theory]
    [InlineData("start", "Start-Service -Name 'Spooler'")]
    [InlineData("stop", "Stop-Service -Name 'Spooler' -Force")]
    [InlineData("restart", "Restart-Service -Name 'Spooler' -Force")]
    [InlineData("status", "Get-Service -Name 'Spooler' | Select-Object Name, @{N='Status';E={$_.Status.ToString()}}, @{N='StartType';E={$_.StartType.ToString()}} | ConvertTo-Json -Compress")]
    public async Task ServiceManagement_GeneratesCorrectScript(string action, string expectedScript)
    {
        var activity = new ServiceManagementActivity(_sessionFactory.Object, _credentialStore.Object, _db, _engineFactory, _configuration);
        var config = ParseConfig($"{{\"serviceName\": \"Spooler\", \"action\": \"{action}\"}}");

        var script = await ExecuteAndCaptureScript(activity, config);

        script.Should().Be(expectedScript);
    }

    [Fact]
    public async Task ServiceManagement_Create_MinimalArgs_GeneratesNewService()
    {
        var activity = new ServiceManagementActivity(_sessionFactory.Object, _credentialStore.Object, _db, _engineFactory, _configuration);
        var config = ParseConfig(
            "{\"serviceName\":\"MySvc\",\"action\":\"create\",\"binaryPath\":\"C:\\\\Tools\\\\my.exe\"}");

        var script = await ExecuteAndCaptureScript(activity, config);

        script.Should().Be("New-Service -Name 'MySvc' -BinaryPathName 'C:\\Tools\\my.exe' -StartupType Automatic");
    }

    [Fact]
    public async Task ServiceManagement_Create_FullArgs_IncludesDisplayNameAndDescription()
    {
        var activity = new ServiceManagementActivity(_sessionFactory.Object, _credentialStore.Object, _db, _engineFactory, _configuration);
        var config = ParseConfig(
            "{\"serviceName\":\"MySvc\",\"action\":\"create\"," +
            "\"binaryPath\":\"C:\\\\Tools\\\\my.exe\"," +
            "\"displayName\":\"My Service\"," +
            "\"description\":\"Does things\"," +
            "\"startupType\":\"Manual\"}");

        var script = await ExecuteAndCaptureScript(activity, config);

        script.Should().Be(
            "New-Service -Name 'MySvc' -BinaryPathName 'C:\\Tools\\my.exe' " +
            "-DisplayName 'My Service' -Description 'Does things' -StartupType Manual");
    }

    [Fact]
    public async Task ServiceManagement_Create_DelayedAutoStart_FallsBackToScExe()
    {
        // PS 5.1's New-Service can't set delayed-auto directly — we create with Automatic, then
        // patch the bit via sc.exe config. Same trick setStartType uses.
        var activity = new ServiceManagementActivity(_sessionFactory.Object, _credentialStore.Object, _db, _engineFactory, _configuration);
        var config = ParseConfig(
            "{\"serviceName\":\"MySvc\",\"action\":\"create\"," +
            "\"binaryPath\":\"C:\\\\Tools\\\\my.exe\",\"startupType\":\"AutomaticDelayedStart\"}");

        var script = await ExecuteAndCaptureScript(activity, config);

        script.Should().Be(
            "New-Service -Name 'MySvc' -BinaryPathName 'C:\\Tools\\my.exe' -StartupType Automatic" +
            "; & sc.exe config 'MySvc' start= delayed-auto | Out-Null");
    }

    [Fact]
    public async Task ServiceManagement_Create_MissingBinaryPath_Throws()
    {
        var activity = new ServiceManagementActivity(_sessionFactory.Object, _credentialStore.Object, _db, _engineFactory, _configuration);
        var config = ParseConfig("{\"serviceName\":\"MySvc\",\"action\":\"create\"}");

        var act = async () => await activity.ExecuteAsync(CreateContext(), config, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*binaryPath*");
    }

    [Fact]
    public async Task ServiceManagement_Create_UnknownStartupType_Throws()
    {
        var activity = new ServiceManagementActivity(_sessionFactory.Object, _credentialStore.Object, _db, _engineFactory, _configuration);
        var config = ParseConfig(
            "{\"serviceName\":\"MySvc\",\"action\":\"create\"," +
            "\"binaryPath\":\"C:\\\\Tools\\\\my.exe\",\"startupType\":\"Boot\"}");

        var act = async () => await activity.ExecuteAsync(CreateContext(), config, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Boot*");
    }

    [Fact]
    public async Task ServiceManagement_Delete_StopsThenScExeDelete()
    {
        var activity = new ServiceManagementActivity(_sessionFactory.Object, _credentialStore.Object, _db, _engineFactory, _configuration);
        var config = ParseConfig("{\"serviceName\":\"MySvc\",\"action\":\"delete\"}");

        var script = await ExecuteAndCaptureScript(activity, config);

        script.Should().Be(
            "if (Get-Service -Name 'MySvc' -ErrorAction SilentlyContinue) " +
            "{ Stop-Service -Name 'MySvc' -Force -ErrorAction SilentlyContinue }; " +
            "& sc.exe delete 'MySvc'");
    }

    [Theory]
    [InlineData("Automatic", "Set-Service -Name 'MySvc' -StartupType Automatic")]
    [InlineData("Manual",    "Set-Service -Name 'MySvc' -StartupType Manual")]
    [InlineData("Disabled",  "Set-Service -Name 'MySvc' -StartupType Disabled")]
    public async Task ServiceManagement_SetStartType_UsesSetService(string startupType, string expected)
    {
        var activity = new ServiceManagementActivity(_sessionFactory.Object, _credentialStore.Object, _db, _engineFactory, _configuration);
        var config = ParseConfig(
            $"{{\"serviceName\":\"MySvc\",\"action\":\"setStartType\",\"startupType\":\"{startupType}\"}}");

        var script = await ExecuteAndCaptureScript(activity, config);

        script.Should().Be(expected);
    }

    [Fact]
    public async Task ServiceManagement_SetStartType_DelayedAuto_UsesScExe()
    {
        var activity = new ServiceManagementActivity(_sessionFactory.Object, _credentialStore.Object, _db, _engineFactory, _configuration);
        var config = ParseConfig(
            "{\"serviceName\":\"MySvc\",\"action\":\"setStartType\",\"startupType\":\"AutomaticDelayedStart\"}");

        var script = await ExecuteAndCaptureScript(activity, config);

        script.Should().Be(
            "& sc.exe config 'MySvc' start= delayed-auto | Out-Null; " +
            "& sc.exe qc 'MySvc' | Select-String 'START_TYPE'");
    }

    [Fact]
    public async Task ServiceManagement_SetStartType_MissingStartupType_Throws()
    {
        var activity = new ServiceManagementActivity(_sessionFactory.Object, _credentialStore.Object, _db, _engineFactory, _configuration);
        var config = ParseConfig("{\"serviceName\":\"MySvc\",\"action\":\"setStartType\"}");

        var act = async () => await activity.ExecuteAsync(CreateContext(), config, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*startupType*");
    }

    // ---- FileOperationActivity ----
    //
    // Since the marker refactor, file-op scripts are wrapped in a ###NODEPILOT_FILEOP_RESULT_*###
    // block plus try/catch — substring checks are enough here, since an exact string comparison
    // against the JSON envelope would be brittle.

    [Theory]
    [InlineData("copy", "{\"operation\": \"copy\", \"path\": \"C:\\\\temp\\\\file.txt\", \"destination\": \"D:\\\\backup\\\\file.txt\"}",
        new[] { "$__path = 'C:\\temp\\file.txt'", "$__destination = 'D:\\backup\\file.txt'",
                "Copy-Item -LiteralPath $__path -Destination $__destination -Force",
                "$__result.destination = $__destination", "Not a file:" })]
    [InlineData("move", "{\"operation\": \"move\", \"path\": \"C:\\\\temp\\\\file.txt\", \"destination\": \"D:\\\\backup\\\\file.txt\"}",
        new[] { "$__path = 'C:\\temp\\file.txt'", "$__destination = 'D:\\backup\\file.txt'",
                "Move-Item -LiteralPath $__path -Destination $__destination -Force",
                "$__result.destination = $__destination", "Not a file:" })]
    [InlineData("delete", "{\"operation\": \"delete\", \"path\": \"C:\\\\temp\\\\file.txt\"}",
        new[] { "$__path = 'C:\\temp\\file.txt'", "Remove-Item -LiteralPath $__path -Force", "Not a file:" })]
    [InlineData("exists", "{\"operation\": \"exists\", \"path\": \"C:\\\\temp\\\\file.txt\"}",
        new[] { "$__path = 'C:\\temp\\file.txt'", "$__result.exists = [bool](Test-Path -LiteralPath $__path -PathType Leaf)" })]
    [InlineData("create", "{\"operation\": \"create\", \"path\": \"C:\\\\temp\\\\new.txt\"}",
        new[] { "$__path = 'C:\\temp\\new.txt'", "New-Item -Path $__path -ItemType File -Force",
                "Cannot create file: path exists as directory:", "$__result.fullName" })]
    [InlineData("rename", "{\"operation\": \"rename\", \"path\": \"C:\\\\temp\\\\old.txt\", \"newName\": \"new.txt\"}",
        new[] { "$__path = 'C:\\temp\\old.txt'", "$__newName = 'new.txt'",
                "Rename-Item -LiteralPath $__path -NewName $__newName -Force",
                "$__result.newPath = $__target", "Not a file:" })]
    public async Task FileOperation_GeneratesCorrectScript(string _, string configJson, string[] expectedFragments)
    {
        var activity = new FileOperationActivity(_sessionFactory.Object, _credentialStore.Object, _db, _engineFactory, _configuration);
        var config = ParseConfig(configJson);

        var script = await ExecuteAndCaptureScript(activity, config);

        script.Should().NotBeNull();
        script.Should().Contain("###NODEPILOT_FILEOP_RESULT_START###");
        script.Should().Contain("###NODEPILOT_FILEOP_RESULT_END###");
        foreach (var fragment in expectedFragments)
            script.Should().Contain(fragment);
    }

    // ---- FolderOperationActivity ----

    [Theory]
    [InlineData("copy", "{\"operation\": \"copy\", \"path\": \"C:\\\\temp\\\\src\", \"destination\": \"D:\\\\backup\"}",
        new[] { "$__path = 'C:\\temp\\src'", "$__destination = 'D:\\backup'",
                "Copy-Item -LiteralPath $__path -Destination $__destination -Force -Recurse",
                "Not a directory:" })]
    [InlineData("move", "{\"operation\": \"move\", \"path\": \"C:\\\\temp\\\\src\", \"destination\": \"D:\\\\backup\"}",
        new[] { "$__path = 'C:\\temp\\src'", "$__destination = 'D:\\backup'",
                "Move-Item -LiteralPath $__path -Destination $__destination -Force",
                "Not a directory:" })]
    [InlineData("delete", "{\"operation\": \"delete\", \"path\": \"C:\\\\temp\\\\old\"}",
        new[] { "$__path = 'C:\\temp\\old'", "Remove-Item -LiteralPath $__path -Force -Recurse",
                "Not a directory:" })]
    [InlineData("exists", "{\"operation\": \"exists\", \"path\": \"C:\\\\temp\\\\dir\"}",
        new[] { "$__path = 'C:\\temp\\dir'", "$__result.exists = [bool](Test-Path -LiteralPath $__path -PathType Container)" })]
    [InlineData("list", "{\"operation\": \"list\", \"path\": \"C:\\\\temp\\\\dir\"}",
        new[] { "$__path = 'C:\\temp\\dir'", "Get-ChildItem -LiteralPath $__path",
                "$__result.items = @($__items)", "$__result.count = $__total",
                "$__result.truncated = [bool]($__total -gt $__cap)", "Not a directory:" })]
    [InlineData("create", "{\"operation\": \"create\", \"path\": \"C:\\\\temp\\\\new\"}",
        new[] { "$__path = 'C:\\temp\\new'", "New-Item -Path $__path -ItemType Directory -Force",
                "$__result.fullName" })]
    [InlineData("rename", "{\"operation\": \"rename\", \"path\": \"C:\\\\temp\\\\old\", \"newName\": \"new\"}",
        new[] { "$__path = 'C:\\temp\\old'", "$__newName = 'new'",
                "Rename-Item -LiteralPath $__path -NewName $__newName -Force",
                "$__result.newPath = $__target", "Not a directory:" })]
    public async Task FolderOperation_GeneratesCorrectScript(string _, string configJson, string[] expectedFragments)
    {
        var activity = new FolderOperationActivity(_sessionFactory.Object, _credentialStore.Object, _db, _engineFactory, _configuration);
        var config = ParseConfig(configJson);

        var script = await ExecuteAndCaptureScript(activity, config);

        script.Should().NotBeNull();
        script.Should().Contain("###NODEPILOT_FOLDEROP_RESULT_START###");
        script.Should().Contain("###NODEPILOT_FOLDEROP_RESULT_END###");
        foreach (var fragment in expectedFragments)
            script.Should().Contain(fragment);
    }

    // ---- RegistryActivity ----
    //
    // Since the key/value extension, scripts are wrapped in a marker block plus try/catch —
    // substring checks are enough here, since exact matches against the result JSON output
    // would be brittle.

    [Fact]
    public async Task Registry_ReadWithValueName_GeneratesCorrectScript()
    {
        var activity = new RegistryActivity(_sessionFactory.Object, _credentialStore.Object, _db, _engineFactory, _configuration);
        var config = ParseConfig("{\"operation\": \"read\", \"keyPath\": \"HKLM:\\\\SOFTWARE\\\\Test\", \"valueName\": \"MyValue\"}");

        var script = await ExecuteAndCaptureScript(activity, config);

        script.Should().Contain("Get-ItemPropertyValue -LiteralPath $__keyPath -Name $__valueName");
        script.Should().Contain("'HKLM:\\SOFTWARE\\Test'");
        script.Should().Contain("'MyValue'");
    }

    [Fact]
    public async Task Registry_ReadWithoutValueName_GeneratesCorrectScript()
    {
        var activity = new RegistryActivity(_sessionFactory.Object, _credentialStore.Object, _db, _engineFactory, _configuration);
        var config = ParseConfig("{\"operation\": \"read\", \"keyPath\": \"HKLM:\\\\SOFTWARE\\\\Test\"}");

        var script = await ExecuteAndCaptureScript(activity, config);

        script.Should().Contain("Get-Item -LiteralPath $__keyPath");
        script.Should().Contain("$__result.values");
        script.Should().Contain("'HKLM:\\SOFTWARE\\Test'");
    }

    [Fact]
    public async Task Registry_Write_GeneratesCorrectScript()
    {
        var activity = new RegistryActivity(_sessionFactory.Object, _credentialStore.Object, _db, _engineFactory, _configuration);
        var config = ParseConfig("{\"operation\": \"write\", \"keyPath\": \"HKLM:\\\\SOFTWARE\\\\Test\", \"valueName\": \"MyValue\", \"value\": \"123\"}");

        var script = await ExecuteAndCaptureScript(activity, config);

        script.Should().Contain("Set-ItemProperty -LiteralPath $__keyPath -Name $__valueName -Type String -Value $__typed");
        script.Should().Contain("New-Item -Path $__keyPath -Force");
        script.Should().Contain("'MyValue'");
        script.Should().Contain("'123'");
    }

    [Fact]
    public async Task Registry_DeleteValue_GeneratesCorrectScript()
    {
        var activity = new RegistryActivity(_sessionFactory.Object, _credentialStore.Object, _db, _engineFactory, _configuration);
        var config = ParseConfig("{\"operation\": \"deleteValue\", \"keyPath\": \"HKLM:\\\\SOFTWARE\\\\Test\", \"valueName\": \"MyValue\"}");

        var script = await ExecuteAndCaptureScript(activity, config);

        script.Should().Contain("Remove-ItemProperty -LiteralPath $__keyPath -Name $__valueName -Force");
        script.Should().Contain("'MyValue'");
    }

    [Fact]
    public async Task Registry_DeleteKey_GeneratesCorrectScript()
    {
        var activity = new RegistryActivity(_sessionFactory.Object, _credentialStore.Object, _db, _engineFactory, _configuration);
        var config = ParseConfig("{\"operation\": \"deleteKey\", \"keyPath\": \"HKLM:\\\\SOFTWARE\\\\Test\"}");

        var script = await ExecuteAndCaptureScript(activity, config);

        script.Should().Contain("Remove-Item -LiteralPath $__keyPath -Recurse -Force");
    }

    [Fact]
    public async Task Registry_CreateKey_GeneratesCorrectScript()
    {
        var activity = new RegistryActivity(_sessionFactory.Object, _credentialStore.Object, _db, _engineFactory, _configuration);
        var config = ParseConfig("{\"operation\": \"createKey\", \"keyPath\": \"HKLM:\\\\SOFTWARE\\\\Test\"}");

        var script = await ExecuteAndCaptureScript(activity, config);

        script.Should().Contain("New-Item -Path $__keyPath -Force");
        script.Should().Contain("$__result.created");
    }

    [Fact]
    public async Task Registry_Exists_GeneratesCorrectScript()
    {
        var activity = new RegistryActivity(_sessionFactory.Object, _credentialStore.Object, _db, _engineFactory, _configuration);
        var config = ParseConfig("{\"operation\": \"exists\", \"keyPath\": \"HKLM:\\\\SOFTWARE\\\\Test\"}");

        var script = await ExecuteAndCaptureScript(activity, config);

        script.Should().Contain("Test-Path -LiteralPath $__keyPath");
        script.Should().Contain("$__result.exists");
    }

    [Fact]
    public async Task Registry_ListSubKeys_GeneratesCorrectScript()
    {
        var activity = new RegistryActivity(_sessionFactory.Object, _credentialStore.Object, _db, _engineFactory, _configuration);
        var config = ParseConfig("{\"operation\": \"listSubKeys\", \"keyPath\": \"HKLM:\\\\SOFTWARE\\\\Test\"}");

        var script = await ExecuteAndCaptureScript(activity, config);

        script.Should().Contain("Get-ChildItem -LiteralPath $__keyPath");
        script.Should().Contain("$__result.subKeys");
    }

    [Fact]
    public async Task Registry_ListValues_GeneratesCorrectScript()
    {
        var activity = new RegistryActivity(_sessionFactory.Object, _credentialStore.Object, _db, _engineFactory, _configuration);
        var config = ParseConfig("{\"operation\": \"listValues\", \"keyPath\": \"HKLM:\\\\SOFTWARE\\\\Test\"}");

        var script = await ExecuteAndCaptureScript(activity, config);

        script.Should().Contain("Get-Item -LiteralPath $__keyPath");
        script.Should().Contain("$__result.values");
        script.Should().Contain("$__result.count");
    }

    [Fact]
    public async Task Registry_Write_DWord_EmitsTypeToken()
    {
        var activity = new RegistryActivity(_sessionFactory.Object, _credentialStore.Object, _db, _engineFactory, _configuration);
        var config = ParseConfig("{\"operation\": \"write\", \"keyPath\": \"HKLM:\\\\SOFTWARE\\\\Test\", \"valueName\": \"Count\", \"value\": \"42\", \"valueType\": \"DWord\"}");

        var script = await ExecuteAndCaptureScript(activity, config);

        script.Should().Contain("-Type DWord");
        script.Should().Contain("[int]$__rawValue");
    }

    // ---- WmiQueryActivity ----

    [Fact]
    public async Task WmiQuery_BasicQuery_GeneratesCorrectScript()
    {
        var activity = new WmiQueryActivity(_sessionFactory.Object, _credentialStore.Object, _db, _engineFactory, _configuration);
        var config = ParseConfig("{\"className\": \"Win32_Process\", \"namespace\": \"root\\\\cimv2\"}");

        var script = await ExecuteAndCaptureScript(activity, config);

        script.Should().Be("Get-CimInstance -ClassName 'Win32_Process' -Namespace 'root\\cimv2'");
    }

    [Fact]
    public async Task WmiQuery_WithFilter_GeneratesCorrectScript()
    {
        var activity = new WmiQueryActivity(_sessionFactory.Object, _credentialStore.Object, _db, _engineFactory, _configuration);
        var config = ParseConfig("{\"className\": \"Win32_Process\", \"namespace\": \"root\\\\cimv2\", \"filter\": \"Name='explorer.exe'\"}");

        var script = await ExecuteAndCaptureScript(activity, config);

        script.Should().Be("$__npFilter = 'Name=''explorer.exe'''; Get-CimInstance -ClassName 'Win32_Process' -Namespace 'root\\cimv2' -Filter $__npFilter");
    }

    [Fact]
    public async Task WmiQuery_DefaultNamespace_UsesRootCimv2()
    {
        var activity = new WmiQueryActivity(_sessionFactory.Object, _credentialStore.Object, _db, _engineFactory, _configuration);
        var config = ParseConfig("{\"className\": \"Win32_OperatingSystem\"}");

        var script = await ExecuteAndCaptureScript(activity, config);

        script.Should().Contain("-Namespace 'root\\cimv2'");
    }

    // ---- PowerManagementActivity ----

    private PowerManagementActivity CreatePowerMgmt() =>
        new(_sessionFactory.Object, _credentialStore.Object, _db, _engineFactory, _configuration);

    [Theory]
    [InlineData("shutdown", "& shutdown.exe /s /f /t 0")]
    [InlineData("restart",  "& shutdown.exe /r /f /t 0")]
    public async Task PowerManagement_DefaultForceAndZeroDelay(string action, string expected)
    {
        var script = await ExecuteAndCaptureScript(CreatePowerMgmt(), ParseConfig($"{{\"action\":\"{action}\"}}"));
        script.Should().Be(expected);
    }

    [Fact]
    public async Task PowerManagement_DelayAndMessage_EmittedAsArgs()
    {
        var config = ParseConfig("{\"action\":\"restart\",\"delaySeconds\":60,\"message\":\"Patching now\"}");
        var script = await ExecuteAndCaptureScript(CreatePowerMgmt(), config);
        script.Should().Be("& shutdown.exe /r /f /t 60 /c 'Patching now'");
    }

    [Fact]
    public async Task PowerManagement_ForceFalse_OmitsForceFlag()
    {
        var config = ParseConfig("{\"action\":\"shutdown\",\"force\":false,\"delaySeconds\":30}");
        var script = await ExecuteAndCaptureScript(CreatePowerMgmt(), config);
        script.Should().Be("& shutdown.exe /s /t 30");
    }

    [Fact]
    public async Task PowerManagement_MessageWithApostrophe_IsEscaped()
    {
        // PowerShellQuoter doubles apostrophes so the literal stays well-formed even when the
        // message came from an upstream step's output we don't trust to be quote-free.
        var config = ParseConfig("{\"action\":\"shutdown\",\"message\":\"Kev's patch\"}");
        var script = await ExecuteAndCaptureScript(CreatePowerMgmt(), config);
        script.Should().Be("& shutdown.exe /s /f /t 0 /c 'Kev''s patch'");
    }

    [Theory]
    [InlineData("logoff",    "& shutdown.exe /l")]
    [InlineData("hibernate", "& shutdown.exe /h")]
    public async Task PowerManagement_SimpleActions_HaveNoDelayOrForce(string action, string expected)
    {
        // These shutdown.exe modes don't take /t or /f — passing them would be a Windows error.
        // Verify we emit a clean one-shot invocation.
        var script = await ExecuteAndCaptureScript(CreatePowerMgmt(),
            ParseConfig($"{{\"action\":\"{action}\",\"delaySeconds\":30,\"force\":true,\"message\":\"ignored\"}}"));
        script.Should().Be(expected);
    }

    [Fact]
    public async Task PowerManagement_Abort_TreatsNoPendingShutdownAsSuccess()
    {
        var script = await ExecuteAndCaptureScript(CreatePowerMgmt(),
            ParseConfig("{\"action\":\"abort\",\"delaySeconds\":30,\"force\":true,\"message\":\"ignored\"}"));

        script.Should().Contain("shutdown.exe /a");
        script.Should().Contain("$LASTEXITCODE -eq 1116");
        script.Should().Contain("Write-Output");
        script.Should().Contain("Write-Error");
        script.Should().NotContain("/t 30");
        script.Should().NotContain("/f");
    }

    [Fact]
    public async Task PowerManagement_MissingAction_Throws()
    {
        var activity = CreatePowerMgmt();
        var act = async () => await activity.ExecuteAsync(CreateContext(), ParseConfig("{}"), CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*'action' is required*");
    }

    [Fact]
    public async Task PowerManagement_UnknownAction_Throws()
    {
        var activity = CreatePowerMgmt();
        var act = async () => await activity.ExecuteAsync(CreateContext(), ParseConfig("{\"action\":\"selfDestruct\"}"), CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*unknown action 'selfdestruct'*");
    }

    [Fact]
    public async Task PowerManagement_NegativeDelay_ClampsToZero()
    {
        var config = ParseConfig("{\"action\":\"shutdown\",\"delaySeconds\":-5}");
        var script = await ExecuteAndCaptureScript(CreatePowerMgmt(), config);
        script.Should().Be("& shutdown.exe /s /f /t 0");
    }
}

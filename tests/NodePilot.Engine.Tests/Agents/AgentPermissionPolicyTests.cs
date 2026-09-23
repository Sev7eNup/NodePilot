using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NodePilot.Core.Agents;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Engine.Agents;
using NodePilot.Engine.PowerShell;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Engine.Tests.Agents;

public sealed class AgentPermissionPolicyTests
{
    [Fact]
    public void EventParameterSetErrorExplainsThePermittedCorrection()
    {
        var error = Assert.Throws<ArgumentException>(() => AgentPermissionPolicy.PrepareShell("powershell",
            "Get-WinEvent -LogName System -FilterHashtable @{Id=7040} -MaxEvents 20"));
        Assert.Contains("inside the filter", error.Message);
        AgentPermissionPolicy.PrepareShell("powershell", "Get-WinEvent -FilterHashtable @{LogName='System';Id=7040} -MaxEvents 20");
        Assert.Throws<UnauthorizedAccessException>(() => AgentPermissionPolicy.PrepareShell("powershell", "Clear-EventLog -LogName System"));
    }
    [Theory]
    [InlineData("Get-CimInstance -Namespace root/ccm/Policy/Machine/ActualConfig -ClassName CCM_UpdateCIAssignment -Property 'Name FROM OtherClass'")]
    [InlineData("Get-CimInstance -Namespace root/SMS/site_CHQ -ClassName SMS_UpdatesAssignment -Property 'Name,Deadline FROM OtherClass'")]
    [InlineData("Get-CimInstance -ClassName Win32_Service -Property 'Name,,State'")]
    [InlineData("Get-CimInstance -ClassName Win32_Product -Property 'Name,Version'")]
    public void CimProjectionCannotBecomeQuerySyntax(string command)
        => Assert.Throws<UnauthorizedAccessException>(() => AgentPermissionPolicy.PrepareShell("powershell", command));

    [Theory]
    [InlineData("Get-CimInstance -ClassName Win32_Service -Property 'Name, State'", "Get-CimInstance -ClassName Win32_Service -Property Name,State")]
    [InlineData("Get-CimInstance -Namespace root/SMS/site_CHQ -ClassName SMS_UpdatesAssignment -Property 'AssignmentID,EnforcementDeadline'", "Get-CimInstance -Namespace root/SMS/site_CHQ -ClassName SMS_UpdatesAssignment -Property AssignmentID,EnforcementDeadline")]
    public void LiteralCommaProjectionProducesTheSameCheckedRead(string accidentalList, string explicitList)
        => Assert.Equal(AgentPermissionPolicy.PrepareShell("powershell", explicitList), AgentPermissionPolicy.PrepareShell("powershell", accidentalList));

    [Theory]
    [InlineData("Get-CimInstance -Namespace root/ccm/Policy/Machine/ActualConfig -ClassName CCM_UpdateCIAssignment -Property AssignmentID,EnforcementDeadline,UseGMTTimes")]
    [InlineData("Get-CimInstance -Namespace root/SMS/site_CHQ -Query 'SELECT AssignmentID,EnforcementDeadline,UseGMTTimes FROM SMS_UpdatesAssignment WHERE AssignmentID=1'")]
    [InlineData("Get-CimInstance -Namespace root/ccm/ClientSDK -ClassName CCM_SoftwareUpdate | Select-Object UpdateID,Deadline")]
    [InlineData("Get-CimInstance -Namespace root/ccm/ClientSDK -ClassName CCM_ServiceWindow | Select-Object Type,Duration,StartTime,EndTime")]
    public void SchedulingQueriesPreserveRawDmtfInsteadOfCimDateTimeConversion(string command)
    {
        var prepared = AgentPermissionPolicy.PrepareShell("powershell", command);
        Assert.Contains("Microsoft.PowerShell.Management\\Get-WmiObject", prepared);
        Assert.DoesNotContain("Get-CimInstance", prepared);
        Assert.DoesNotContain(" -ClassName ", prepared);
        Assert.Contains(" -Class ", prepared);
    }

    [Theory]
    [InlineData("Get-CimClass -Namespace root/ccm/ClientSDK -ClassName CCM_SoftwareUpdate | Select-Object -ExpandProperty CimClassProperties | Select-Object Name,CimType")]
    [InlineData("Get-CimInstance -Namespace root/SMS/site_CHQ -ClassName SMS_CIDeploymentUnknownAssetDetails -Filter 'MachineID=16777220' | Select-Object CI_ID,AssignmentID")]
    public void SupportsKnownSchemaAndUnknownDeploymentReads(string command)
        => Assert.NotEmpty(AgentPermissionPolicy.PrepareShell("powershell", command));

    [Theory]
    [InlineData("Get-CimClass -ClassName Win32_Product")]
    [InlineData("Get-CimClass -ClassName Win32_Service -ComputerName other")]
    [InlineData("Get-CimClass -Namespace root/ccm/ClientSDK -ClassName '*'")]
    [InlineData("Get-CimClass -ClassName Win32_Service | Invoke-CimMethod -MethodName StartService")]
    public void SchemaReadsCannotBroadenTargetOrExecuteMethods(string command)
        => Assert.Throws<UnauthorizedAccessException>(() => AgentPermissionPolicy.PrepareShell("powershell", command));

    [Theory]
    [InlineData("Get-CimInstance -Namespace root/SMS -ClassName SMS_ProviderLocation | Select-Object Machine,SiteCode,NamespacePath")]
    [InlineData("Get-CimInstance -Namespace root/SMS/site_CHQ -ClassName SMS_BoundaryGroupMembers -Filter 'GroupID=16777218'")]
    public void SupportsConfigMgrProviderDiscoveryAndBoundaryMembership(string command)
        => Assert.NotEmpty(AgentPermissionPolicy.PrepareShell("powershell", command));

    [Theory]
    [InlineData("$kind='Win32_Service'; Get-CimInstance -ClassName $kind | Where-Object State -EQ Running | Select-Object Name")]
    [InlineData("Get-CimInstance -Query \"SELECT Name,State FROM Win32_Service WHERE State='Running'\"")]
    [InlineData("Get-Service -ErrorAction SilentlyContinue | Where-Object -Property Status -EQ -Value Running")]
    [InlineData("Get-WinEvent -LogName System -FilterXPath '*[System[(EventID=7040)]]' -Oldest -MaxEvents 10")]
    [InlineData("Write-Output '{\"name\":\"sample\"}' | ConvertFrom-Json | Select-Object name")]
    public void SupportsReadComposition(string command)
        => Assert.NotEmpty(AgentPermissionPolicy.PrepareShell("powershell", command));

    [Theory]
    [InlineData("$kind='Win32_Product'; Get-CimInstance -ClassName $kind")]
    [InlineData("Get-CimInstance -Query 'SELECT * FROM Win32_Product'")]
    [InlineData("Get-CimInstance -Query 'ASSOCIATORS OF {Win32_Service.Name=abc}'")]
    [InlineData("Get-Service | Where-Object { Stop-Service $_ }")]
    [InlineData("Get-Service -ErrorAction Inquire")]
    [InlineData("$x=Get-Service; $x.Stop()")]
    public void CompositionCannotEnableWriters(string command)
        => Assert.Throws<UnauthorizedAccessException>(() => AgentPermissionPolicy.PrepareShell("powershell", command));

    [Fact]
    public void SkillCannotUseBindingsToAlterPowerShellDefaults()
        => Assert.Throws<UnauthorizedAccessException>(() => AgentPermissionPolicy.ValidateSkillScript("powershell",
            "$PSDefaultParameterValues=@{'Get-Service:ComputerName'='elsewhere'}; Get-Service", []));

    [Theory]
    [InlineData("powershell")]
    [InlineData("bash")]
    public async Task RealReadPipelineFiltersWithoutChangingItsSource(string shell)
    {
        if (shell == "bash" && !File.Exists(@"C:\Program Files\Git\bin\bash.exe")) Assert.Skip("Git Bash missing");
        var path = Path.Combine(Path.GetTempPath(), "agent-composition-" + Guid.NewGuid().ToString("N") + ".txt");
        const string content = "keep\ndiscard\nkeep\n";
        try
        {
            await File.WriteAllTextAsync(path, content, TestContext.Current.CancellationToken);
            var command = shell == "powershell"
                ? "$source=" + PowerShellOperation.Literal(path) + "; Get-Content -LiteralPath $source | Where-Object -Property Length -EQ -Value 4 | Measure-Object | Select-Object -ExpandProperty Count"
                : "cat '/" + char.ToLowerInvariant(path[0]) + path[2..].Replace('\\', '/') + "' | grep keep | wc -l";
            using var result = JsonDocument.Parse(await AgentShellTests.Execute(AgentProcessScript.Build(shell, AgentPermissionPolicy.PrepareShell(shell, command), null, null)));
            Assert.Equal(0, result.RootElement.GetProperty("exitCode").GetInt32());
            Assert.Equal("2", result.RootElement.GetProperty("stdout").GetString()!.Trim());
            Assert.Equal(content, await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("ipconfig /all")]
    [InlineData("ipconfig /displaydns")]
    [InlineData("netstat -ano")]
    [InlineData("sc.exe query CcmExec")]
    [InlineData("sc qc CcmExec")]
    [InlineData("tasklist /svc")]
    [InlineData("systeminfo")]
    public void CmdSupportsSystemAndNetworkReads(string command)
        => Assert.Contains("%SystemRoot%", AgentPermissionPolicy.PrepareShell("cmd", command));

    [Theory]
    [InlineData("ipconfig /release")]
    [InlineData("ipconfig /flushdns")]
    [InlineData("sc.exe stop CcmExec")]
    [InlineData("sc.exe query \\\\other")]
    [InlineData("tasklist /S other")]
    [InlineData("systeminfo /S other")]
    [InlineData("netstat -ano > C:\\out.txt")]
    public void CmdReadVariantsDoNotEnableMutationsOrRemoteOverrides(string command)
        => Assert.Throws<UnauthorizedAccessException>(() => AgentPermissionPolicy.PrepareShell("cmd", command));

    [Theory]
    [InlineData("Get-NetIPConfiguration -Detailed")]
    [InlineData("Get-NetTCPConnection -State Established | Select-Object LocalAddress,RemoteAddress,OwningProcess")]
    [InlineData("Get-NetRoute -AddressFamily IPv4 | Sort-Object -Property RouteMetric")]
    [InlineData("Find-NetRoute -RemoteIPAddress 10.0.0.7 | Select-Object InterfaceAlias,DestinationPrefix,NextHop")]
    [InlineData("Get-DnsClientCache | Select-Object Entry,Data")]
    [InlineData("Get-ScheduledTask -TaskPath '\\Microsoft\\Windows\\*' | Select-Object TaskName,State")]
    [InlineData("Get-MpComputerStatus | Select-Object AMProductVersion")]
    [InlineData("Get-Volume | Select-Object DriveLetter,SizeRemaining")]
    [InlineData("Get-CimInstance -Namespace root/ccm/ClientSDK -ClassName CCM_SoftwareUpdate -Filter 'ComplianceState=0' | Select-Object Name,Deadline")]
    [InlineData("Get-CimInstance -Namespace root/SMS/site_CHQ -ClassName SMS_DistributionDPStatus -Filter \"PackageID='CHQ00009'\" | Select-Object Name,State")]
    public void GeneralDiagnosticsDoNotDependOnTheCurrentFault(string command)
        => Assert.False(string.IsNullOrWhiteSpace(AgentPermissionPolicy.PrepareShell("powershell", command)));

    [Theory]
    [InlineData("Get-NetIPConfiguration -CimSession other")]
    [InlineData("Get-ScheduledTask | Start-ScheduledTask")]
    [InlineData("Get-CimInstance -Namespace root/SMS/site_CHQ -ClassName SMS_ClientOperation")]
    [InlineData("Get-CimInstance -Namespace root/ccm/ClientSDK -ClassName CCM_SoftwareUpdate | Invoke-CimMethod -MethodName InstallUpdates")]
    [InlineData("Get-WindowsUpdateLog")]
    public void BroaderReadsStillRejectRemediationAndTargetOverrides(string command)
        => Assert.Throws<UnauthorizedAccessException>(() => AgentPermissionPolicy.PrepareShell("powershell", command));

    [Fact]
    public void DnsDiagnosisUsesTheTargetsResolverWithoutProtocolFallback()
    {
        var prepared = AgentPermissionPolicy.PrepareShell("powershell", "Resolve-DnsName -Name 'CM1.corp.contoso.com' -Type A | Select-Object Name,IPAddress");
        Assert.Contains("DnsClient\\Resolve-DnsName", prepared);
        Assert.Contains("-DnsOnly", prepared);
    }

    [Theory]
    [InlineData("Resolve-DnsName -Name host.example -Server elsewhere")]
    [InlineData("Resolve-DnsName -Name host.example -Type TXT")]
    [InlineData("Resolve-DnsName -Name 'https://host.example/path'")]
    [InlineData("Resolve-DnsName -Name host.example -DnsOnly:$false")]
    public void DnsDiagnosisRejectsAlternateServersAndUnsupportedQueries(string command)
        => Assert.Throws<UnauthorizedAccessException>(() => AgentPermissionPolicy.PrepareShell("powershell", command));

    [Theory]
    [InlineData("Get-NetFirewallRule -Enabled True -Direction Outbound -Action Block | Get-NetFirewallPortFilter | Select-Object Protocol,RemotePort")]
    [InlineData("Get-NetFirewallRule -Name 'example-rule' | Get-NetFirewallAddressFilter | Select-Object RemoteAddress")]
    [InlineData("Get-NetFirewallRule -Name 'example-rule' | Get-NetFirewallApplicationFilter | Select-Object Program")]
    [InlineData("Get-NetFirewallRule -Name 'example-rule' | Get-NetFirewallServiceFilter | Select-Object Service")]
    [InlineData("Get-NetFirewallProfile | Select-Object Name,Enabled,DefaultOutboundAction")]
    [InlineData("Get-NetConnectionProfile | Select-Object InterfaceAlias,NetworkCategory")]
    public void FirewallDiagnosisReadsEffectiveLocalConfiguration(string command)
    {
        var prepared = AgentPermissionPolicy.PrepareShell("powershell", command);
        Assert.Contains("\\Get-Net", prepared);
        if (!command.StartsWith("Get-NetConnectionProfile")) Assert.Contains("ActiveStore", prepared);
    }

    [Theory]
    [InlineData("Get-NetFirewallRule -PolicyStore 'other.example.com'")]
    [InlineData("Get-NetFirewallRule -CimSession elsewhere")]
    [InlineData("Get-NetFirewallRule -GPOSession elsewhere")]
    [InlineData("Get-NetFirewallRule | Disable-NetFirewallRule")]
    [InlineData("Get-NetFirewallRule | Set-NetFirewallRule -Enabled False")]
    [InlineData("Write-Output @{CimSession='elsewhere'} | Get-NetFirewallPortFilter")]
    [InlineData("Get-NetFirewallRule | Select-Object Name | Get-NetFirewallAddressFilter")]
    public void FirewallReadsCannotChangeRulesOrQueryAnotherTarget(string command)
        => Assert.Throws<UnauthorizedAccessException>(() => AgentPermissionPolicy.PrepareShell("powershell", command));

    [Theory]
    [InlineData("powershell", "Get-CimInstance Win32_Product")]
    [InlineData("powershell", "Get-CimInstance -ClassName Win32_Product | Select-Object Name")]
    [InlineData("powershell", "Get-CimInstance -Query 'SELECT * FROM Win32_Product'")]
    [InlineData("powershell", "$c='Win32_Product'; Get-CimInstance $c")]
    [InlineData("powershell", "Get-Service | ForEach-Object { Stop-Service $_ }")]
    [InlineData("powershell", "Get-Service > C:\\changed.txt")]
    [InlineData("powershell", "Get-Service; Start-Process cmd.exe")]
    [InlineData("powershell", "Get-Content $(Set-Content C:\\changed.txt x)")]
    [InlineData("powershell", "[IO.File]::WriteAllText('C:\\changed.txt','x')")]
    [InlineData("powershell", "Get-CimInstance -ClassName Win32_Service -ComputerName elsewhere")]
    [InlineData("powershell", "Get-Date -Date '2026-01-01'; Set-Date '2026-01-01'")]
    [InlineData("powershell", "Get-Service -OutVariable stolen")]
    [InlineData("powershell", "Get-Content -Path 'Function:\\script'")]
    [InlineData("powershell", "Get-Item C:\\file.txt:stream")]
    [InlineData("powershell", "Get-Service | Select-Object @{Name='x';Expression={Start-Process cmd}}")]
    [InlineData("powershell", "Get-CimInstance -Namespace root/evil -ClassName Win32_Service")]
    [InlineData("powershell", "Get-CimInstance -ClassName Win32_Prod*")]
    [InlineData("powershell", "Get-CimInstance -ClassName ([string]'Win32_Product')")]
    [InlineData("powershell", "Get-Content \"C:\\$(Start-Process cmd).txt\"")]
    [InlineData("powershell", "#requires -Modules Evil\nGet-Service")]
    [InlineData("powershell", "function Get-Service { Set-Content C:\\x y }; Get-Service")]
    [InlineData("powershell", "Get-Service &")]
    [InlineData("powershell", ". C:\\evil.ps1")]
    [InlineData("powershell", "& 'Get-Service'")]
    [InlineData("powershell", "Write-Output @{ComputerName='other'} | Get-Service")]
    [InlineData("cmd", "type C:\\logs.txt & del C:\\logs.txt")]
    [InlineData("cmd", "echo changed > C:\\changed.txt")]
    [InlineData("cmd", "%COMSPEC% /c del C:\\logs.txt")]
    [InlineData("cmd", "powershell Get-CimInstance Win32_Product")]
    [InlineData("bash", "cat /c/logs.txt; rm /c/logs.txt")]
    [InlineData("bash", "cat $(touch /c/changed.txt)")]
    [InlineData("bash", "cat /c/logs.txt > /c/changed.txt")]
    [InlineData("bash", "find /c -exec rm {} +")]
    [InlineData("bash", "sort -o /c/changed.txt /c/logs.txt")]
    [InlineData("bash", "hostname changed")]
    [InlineData("bash", "df --sync")]
    [InlineData("bash", "du /c/Windows > /c/changed.txt")]
    public void RejectsSideEffectsAndDynamicExecutionBeforeAProcessCanStart(string shell, string command)
        => Assert.Throws<UnauthorizedAccessException>(() => AgentPermissionPolicy.PrepareShell(shell, command));

    [Theory]
    [InlineData("powershell", "Get-CimInstance -ClassName Win32_Service | Select-Object Name,State,StartMode")]
    [InlineData("powershell", "Get-CimInstance -Namespace root/ccm -ClassName SMS_Client")]
    [InlineData("powershell", "Get-ItemProperty -LiteralPath 'HKLM:\\SOFTWARE\\Microsoft\\SMS\\Mobile Client' | Select-Object ProductVersion")]
    [InlineData("powershell", "Get-WinEvent -FilterHashtable @{LogName='System';Id=7040} -MaxEvents 20 | Select-Object TimeCreated,Id,Message")]
    [InlineData("powershell", "Get-Content -LiteralPath 'C:\\Windows\\CCM\\Logs\\CcmExec.log' -Tail 20")]
    [InlineData("cmd", "type \"C:\\logs with spaces.txt\"")]
    [InlineData("cmd", "dir C:\\Windows")]
    [InlineData("bash", "cat '/c/logs with spaces.txt'")]
    [InlineData("bash", "grep -n -m 20 ERROR /c/logs.txt")]
    [InlineData("bash", "du -sh /c/Windows/Logs")]
    [InlineData("bash", "df -h")]
    [InlineData("bash", "ps -ef")]
    [InlineData("bash", "id")]
    [InlineData("bash", "uname -a")]
    public void SupportsExistingShellsForExplicitReadOperations(string shell, string command)
        => Assert.False(string.IsNullOrWhiteSpace(AgentPermissionPolicy.PrepareShell(shell, command)));

    [Theory]
    [InlineData("powershell")]
    [InlineData("cmd")]
    [InlineData("bash")]
    public async Task CheckedCommandsStillReadARealFileUsingEachShell(string shell)
    {
        if (shell == "bash" && !File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe")))
            Assert.Skip("Git Bash is not installed.");
        var path = Path.Combine(Path.GetTempPath(), "agent-read-permission-" + Guid.NewGuid().ToString("N") + ".txt");
        var marker = Guid.NewGuid().ToString();
        try
        {
            await File.WriteAllTextAsync(path, marker, TestContext.Current.CancellationToken);
            var posix = "/" + char.ToLowerInvariant(path[0]) + path[2..].Replace('\\', '/');
            var command = shell switch { "powershell" => "Get-Content -LiteralPath " + PowerShellOperation.Literal(path), "cmd" => "type \"" + path + "\"", _ => "cat '" + posix + "'" };
            using var result = JsonDocument.Parse(await AgentShellTests.Execute(AgentProcessScript.Build(shell, AgentPermissionPolicy.PrepareShell(shell, command), null, null)));
            Assert.Equal(0, result.RootElement.GetProperty("exitCode").GetInt32());
            Assert.Contains(marker, result.RootElement.GetProperty("stdout").GetString());
            Assert.Equal(marker, await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void SkillParametersAreCheckedAgainstTheInvokedCimClass()
    {
        const string script = "param([string]$Class)\nGet-CimInstance -ClassName $Class";
        AgentPermissionPolicy.ValidateSkillScript("powershell", script, ["Win32_Service"]);
        Assert.Throws<UnauthorizedAccessException>(() => AgentPermissionPolicy.ValidateSkillScript("powershell", script, ["Win32_Product"]));
        Assert.Throws<UnauthorizedAccessException>(() => AgentPermissionPolicy.ValidateSkillScript("powershell", script, ["-Class:Win32_Product"]));
        Assert.Throws<UnauthorizedAccessException>(() => AgentPermissionPolicy.ValidateSkillScript("powershell", "param([ValidateScript({Set-Content C:\\x y})][string]$p)\nWrite-Output $p", ["x"]));
    }

    [Theory]
    [InlineData("files_write", "{\"path\":\"C:\\\\blocked.txt\",\"content\":\"x\"}")]
    [InlineData("http_request", "{\"url\":\"http://localhost/delete\",\"method\":\"POST\"}")]
    [InlineData("workflow_run", "{\"workflowId\":\"11111111-1111-1111-1111-111111111111\"}")]
    [InlineData("powershell", "{\"command\":\"Get-CimInstance Win32_Product\",\"allowWrites\":true}")]
    public async Task RealToolBoundaryBlocksBeforeOpeningARemoteSession(string name, string input)
    {
        await using var db = TestDbFactory.Create();
        var sessions = new Mock<IRemoteSessionFactory>(MockBehavior.Strict);
        var engine = new Mock<IPowerShellExecutionEngine>(MockBehavior.Strict);
        var monitor = new Mock<IOptionsMonitor<AgentOptions>>(); monitor.SetupGet(x => x.CurrentValue).Returns(new AgentOptions { AllowServiceIdentity = true });
        var factory = new AgentTargetFactory(db, Mock.Of<ICredentialStore>(), sessions.Object,
            new PowerShellEngineFactory(engine.Object, engine.Object, engine.Object), monitor.Object, NullLogger<AgentTargetFactory>.Instance);
        var host = new AgentToolHost(factory, null!, db, null!, null!, new AgentExternalReadPolicy(monitor.Object));
        await using var session = await host.OpenAsync(new AgentDefinition { UseServiceIdentity = true, Tools = [new() { Name = name, AllowedPaths = ["C:\\"] }] },
            new StepExecutionContext { ResolvedMachine = new ManagedMachine { Hostname = "test-target" } }, Guid.NewGuid(), null!, null!, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => session.Tools.Single().InvokeAsync(JsonDocument.Parse(input).RootElement, TestContext.Current.CancellationToken));
        sessions.VerifyNoOtherCalls(); engine.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task McpWithoutAReadContractIsRejectedBeforeConnecting()
    {
        await using var db = TestDbFactory.Create();
        var host = new AgentToolHost(null!, null!, db, null!, null!, null!);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => host.OpenAsync(new AgentDefinition { Tools = [new() { Name = "mcp", McpServerId = Guid.NewGuid(), McpToolName = "claimsReadOnly" }] },
            new StepExecutionContext(), Guid.NewGuid(), null!, null!, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UnsafePackagedScriptIsRejectedBeforeStagingOrConnection()
    {
        await using var db = TestDbFactory.Create();
        var bytes = AgentSkillTests.Package(("SKILL.md", "---\nname: probe\ndescription: Test\n---\nInspect the host."),
            ("scripts/check.ps1", "Get-CimInstance Win32_Product"));
        var parsed = AgentSkillArchive.Read(bytes); var id = Guid.NewGuid();
        db.AgentSkillPackages.Add(new AgentSkillPackage { Id = id, Name = parsed.Name, Version = "1", Description = parsed.Description, Package = bytes, Sha256 = parsed.Sha256 });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var sessions = new Mock<IRemoteSessionFactory>(MockBehavior.Strict);
        var monitor = new Mock<IOptionsMonitor<AgentOptions>>(); monitor.SetupGet(x => x.CurrentValue).Returns(new AgentOptions());
        await using var target = new AgentTarget(new ManagedMachine { Hostname = "test-target" }, new Credential(), false, sessions.Object, null!, monitor.Object, "test", NullLogger.Instance);
        var tools = await AgentSkillTools.CreateAsync(db, new AgentDefinition { SkillIds = [id], Tools = [new() { Name = "powershell" }] }, target, Guid.NewGuid(), TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => tools.Single(x => x.Name == "run_skill_script").InvokeAsync(JsonSerializer.SerializeToElement(new { skillId = id, path = "scripts/check.ps1" }), TestContext.Current.CancellationToken));
        Assert.Equal("", target.WorkingRoot);
        sessions.VerifyNoOtherCalls();
    }
}

using NodePilot.Engine.Agents;
using NodePilot.Engine.PowerShell;
using Xunit;

namespace NodePilot.Engine.Tests.Agents;

public sealed class AgentShareReadTests
{
    [Theory]
    [InlineData("Get-SmbShare -Name 'Packages$' | Select-Object Name,Path,ShareState")]
    [InlineData("Get-SmbShareAccess -Name 'Packages$' | Select-Object AccountName,AccessRight")]
    [InlineData("Get-CimInstance -ClassName Win32_Share -Filter \"Name='Packages$'\"")]
    [InlineData("Get-CimInstance -Namespace root/SMS/site_ABC -ClassName SMS_Advertisement -Filter \"AdvertisementID='ABC20001'\"")]
    [InlineData("Get-CimClass -Namespace root/SMS/site_ABC -ClassName SMS_TaskSequencePackage")]
    [InlineData("Get-CimInstance -Namespace root/SMS/site_ABC -ClassName SMS_DeploymentSummary")]
    [InlineData("Get-CimInstance -Namespace root/ccm/SoftMgmtAgent -ClassName CacheConfig")]
    [InlineData("Get-CimInstance -Namespace root/ccm/Policy/Machine/ActualConfig -ClassName CCM_Scheduler_ScheduledMessage")]
    public void SupportsShareAndDeploymentObservation(string command)
        => Assert.NotEmpty(AgentPermissionPolicy.PrepareShell("powershell", command));

    [Theory]
    [InlineData("Get-SmbShare -CimSession other")]
    [InlineData("Get-SmbShare -ScopeName other")]
    [InlineData("Get-SmbShare | Remove-SmbShare -Force")]
    [InlineData("Grant-SmbShareAccess -Name data -AccountName Everyone -AccessRight Full")]
    [InlineData("Get-CimInstance -Namespace root/SMS/site_ABC -ClassName SMS_Advertisement | Invoke-CimMethod -MethodName RefreshPkgSource")]
    [InlineData("Get-CimInstance -ClassName Win32_Product")]
    [InlineData("Get-Content -LiteralPath '\\\\?\\C:\\Windows\\win.ini'")]
    [InlineData("Get-Content -LiteralPath '\\\\.\\pipe\\test'")]
    [InlineData("Get-Content -LiteralPath '\\\\localhost\\C$\\Windows\\win.ini:stream'")]
    [InlineData("Get-Content -LiteralPath '\\\\localhost\\data\\..\\secret'")]
    [InlineData("Get-Content -LiteralPath '\\\\localhost@80\\DavWWWRoot\\file'")]
    public void BroaderReadsDoNotGrantWritesOrAlternateTargets(string command)
        => Assert.Throws<UnauthorizedAccessException>(() => AgentPermissionPolicy.PrepareShell("powershell", command));

    [Fact]
    public async Task LocalShareReadUsesLocalFileAndPreservesContents()
    {
        var path = Path.Combine(Path.GetTempPath(), "agent-share-" + Guid.NewGuid().ToString("N") + ".txt");
        const string content = "read through the bound machine's share";
        try
        {
            await File.WriteAllTextAsync(path, content, TestContext.Current.CancellationToken);
            var unc = @"\\localhost\" + path[0] + "$" + path[2..];
            var prepared = AgentPermissionPolicy.PrepareShell("powershell", "Get-Content -Raw -LiteralPath " + PowerShellOperation.Literal(unc));
            Assert.Equal(content, (await AgentShellTests.Execute("$ErrorActionPreference='Stop'; " + prepared)).Trim());
            Assert.Equal(content, await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task OtherUncHostIsRejectedBeforeAnyNetworkRead()
    {
        var prepared = AgentPermissionPolicy.PrepareShell("powershell", @"Test-Path -LiteralPath '\\unconfigured.invalid\data\file'");
        var error = await AgentShellTests.Execute("try { " + prepared + " } catch { $_.Exception.Message }");
        Assert.Contains("bound machine", error);
    }

    [Fact]
    public void OriginalSkillBytesCannotBypassTheLocalShareResolver()
        => Assert.Throws<UnauthorizedAccessException>(() => AgentPermissionPolicy.ValidateSkillScript(
            "powershell", "param([string]$path) Get-Content -LiteralPath $path", [@"\\localhost\C$\Windows\win.ini"]));
}

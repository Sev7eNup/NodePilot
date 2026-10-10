using NodePilot.Engine.Agents;
using Xunit;

namespace NodePilot.Engine.Tests.Agents;

public sealed class AgentAdvancedReadTests
{
    [Theory]
    [InlineData("Set-WebConfigurationProperty -Filter 'system.webServer/security/access' -Name sslFlags -Value None")]
    [InlineData("Get-WebConfigurationProperty -PSPath '\\\\other\\config' -Filter 'system.webServer/security/access' -Name sslFlags")]
    [InlineData("Get-WebConfigurationProperty -PSPath 'MACHINE/WEBROOT/APPHOST' -Filter 'system.applicationHost/sites/../config' -Name value")]
    [InlineData("Get-WindowsCapability -Online:$false")]
    [InlineData("get-windowscapability")]
    [InlineData("Get-WindowsCapability -Online -LogPath C:\\output.log")]
    [InlineData("Add-WindowsCapability -Online -Name feature")]
    [InlineData("Get-WinEvent -ListProvider '*' -ComputerName other")]
    [InlineData("Get-WinEvent -ListProvider '*' -LogName System")]
    [InlineData("Get-Content Cert:\\LocalMachine\\My")]
    [InlineData("Get-Item Cert:\\LocalMachine\\..\\other")]
    [InlineData("Get-CimInstance -Namespace root/cimv2 -ClassName Win32_Product")]
    [InlineData("Get-CimInstance -Namespace root/SMS/site_LAB -ClassName SMS_Content")]
    [InlineData("Get-CimClass -Namespace root/unknown -ClassName Something")]
    [InlineData("Get-CimClass -Namespace root/SMS/site_LAB -ClassName '*' ")]
    [InlineData("Select-String -LiteralPath C:\\log.txt -Pattern x -Context 100")]
    public void InspectionCannotExpandIntoMutationOrAnotherTarget(string command)
        => Assert.Throws<UnauthorizedAccessException>(() => AgentPermissionPolicy.PrepareShell("powershell", command));

    [Theory]
    [InlineData("Get-CimInstance -Namespace root/SMS/site_LAB -ClassName SMS_Application -Filter 'CI_ID=42'")]
    [InlineData("Get-CimInstance -Namespace root/SMS/site_LAB -ClassName SMS_DeploymentType -Filter 'CI_ID=42'")]
    [InlineData("Get-CimInstance -Namespace root/SMS/site_LAB -ClassName SMS_CollectionSettings -Filter \"CollectionID='LAB00001'\"")]
    [InlineData("Get-WebConfigurationProperty -PSPath 'MACHINE/WEBROOT/APPHOST' -Location 'site/service' -Filter 'system.webServer/security/access' -Name sslFlags")]
    [InlineData("Get-WebConfigurationProperty -PSPath 'IIS:\\Sites\\site' -Filter 'system.webServer/security/authentication/anonymousAuthentication' -Name enabled")]
    [InlineData("Get-WebConfigurationProperty -PSPath 'IIS:\\' -Filter 'system.webServer/security/authorization' -Name rules")]
    [InlineData("Get-CimInstance -Namespace root/ccm/ClientSDK -ClassName CCM_ServiceWindow")]
    [InlineData("Get-CimClass -Namespace root/SMS/site_LAB -ClassName SMS_Content | Select-Object -ExpandProperty CimClassProperties | Select-Object Name")]
    [InlineData("Get-CimClass -Namespace root/ccm/ClientSDK -ClassName CCM_DeploymentType")]
    [InlineData("Get-Item IIS:\\AppPools\\pool | Select-Object Name,recycling,failure")]
    [InlineData("Get-ChildItem Cert:\\LocalMachine\\TrustedPublisher | Select-Object Subject,Thumbprint")]
    [InlineData("Get-AuthenticodeSignature -LiteralPath C:\\Temp\\payload.cab")]
    [InlineData("Get-WindowsCapability -Online -Name 'Tools.*'")]
    [InlineData("Get-WindowsOptionalFeature -Online -FeatureName 'feature'")]
    [InlineData("Get-WinEvent -ListProvider '*WAS*' | Select-Object Name,LogLinks")]
    public void DiagnosticInspectionIsAllowed(string command)
        => Assert.NotEmpty(AgentPermissionPolicy.PrepareShell("powershell", command));

    [Fact]
    public void RegexCannotSilentlyBecomeLiteralSearch()
    {
        var exception = Assert.Throws<UnauthorizedAccessException>(() => AgentPermissionPolicy.PrepareShell("powershell",
            "Select-String -LiteralPath C:\\log.txt -Pattern 'first|second'"));
        Assert.Contains("SimpleMatch", exception.Message);
        var literal = AgentPermissionPolicy.PrepareShell("powershell",
            "Select-String -LiteralPath C:\\log.txt -Pattern 'first|second' -SimpleMatch -Context 2,2");
        Assert.Contains("-Context", literal);
    }

    [Theory]
    [InlineData("Get-CimInstance -Namespace root/SMS/site_LAB -ClassName SMS_DeploymentType -Property CI_ID,SDMPackageXML")]
    [InlineData("Get-CimInstance -Namespace root/SMS/site_LAB -Query 'SELECT SDMPackageXML FROM SMS_Application WHERE CI_ID=42'")]
    public void LazyXmlIsFetchedByInstanceGetInsteadOfInvalidEnumerationProjection(string command)
    {
        var prepared = AgentPermissionPolicy.PrepareShell("powershell", command);
        Assert.DoesNotContain("SDMPackageXML", prepared);
        Assert.Contains("CI_ID", prepared);
        Assert.Contains("| CimCmdlets\\Get-CimInstance", prepared);
    }

    [Fact]
    public async Task TimestampSearchPreservesWholeNormalLine()
    {
        var path = Path.GetTempFileName();
        var line = "UPDATE_INSTALLED " + new string('x', 220) + " 10:59:14";
        try
        {
            await File.WriteAllTextAsync(path, line, TestContext.Current.CancellationToken);
            var result = await AgentShellTests.Execute(AgentFileTools.BuildSearchScript(path, "10:59:14"));
            Assert.Contains(line, result);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task LiteralAlternativesAndSurroundingContextExecuteAsRequested()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "before\nfirst\nnearby\nsecond\nafter", TestContext.Current.CancellationToken);
            var command = AgentPermissionPolicy.PrepareShell("powershell", $"Select-String -LiteralPath '{path}' -Pattern first,second -SimpleMatch -Context 1,1");
            var output = await AgentShellTests.Execute(command);
            Assert.Contains("first", output);
            Assert.Contains("second", output);
            Assert.Contains("before", output);
            Assert.Contains("after", output);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task LongLineOmissionsAreExplicit()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, new string('x', 17000) + "NEEDLE" + new string('y', 2000), TestContext.Current.CancellationToken);
            var output = await AgentShellTests.Execute(AgentFileTools.BuildSearchScript(path, "NEEDLE"));
            Assert.Contains("earlier characters omitted", output);
            Assert.Contains("later characters omitted", output);
            Assert.Contains("NEEDLE", output);
            Assert.True(output.Length < 2200);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task MaximumLengthQueryRemainsCompleteInsideBoundedExcerpt()
    {
        var path = Path.GetTempFileName();
        var query = new string('q', 1024);
        try
        {
            await File.WriteAllTextAsync(path, new string('x', 2000) + query + new string('y', 2000), TestContext.Current.CancellationToken);
            var output = await AgentShellTests.Execute(AgentFileTools.BuildSearchScript(path, query));
            Assert.Contains(query, output);
            Assert.True(output.Length < 1600);
        }
        finally { File.Delete(path); }
    }
}

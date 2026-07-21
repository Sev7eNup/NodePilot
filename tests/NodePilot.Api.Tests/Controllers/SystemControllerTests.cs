using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using NodePilot.Api.Controllers;
using NodePilot.Api.Dtos;
using NodePilot.Api.Services;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using Xunit;

namespace NodePilot.Api.Tests.Controllers;

/// <summary>
/// Covers the host-info endpoint (mapping from <see cref="IHostIdentityProvider"/> to the DTO)
/// and the pure FQDN-assembly logic in <see cref="HostIdentityProvider.BuildIdentity"/>.
/// </summary>
public sealed class SystemControllerTests
{
    private sealed class FakeHostIdentity : IHostIdentityProvider
    {
        public FakeHostIdentity(HostIdentity identity) => Current = identity;
        public HostIdentity Current { get; }
    }

    [Fact]
    public void GetHostInfo_MapsProviderIdentity()
    {
        var controller = new SystemController(
            new FakeHostIdentity(new HostIdentity("NPSRV01", "npsrv01.corp.example.local", "corp.example.local")));

        var ok = controller.GetHostInfo().Result as OkObjectResult;
        ok.Should().NotBeNull();
        var body = ok!.Value as HostInfoResponse;
        body.Should().NotBeNull();
        body!.MachineName.Should().Be("NPSRV01");
        body.Fqdn.Should().Be("npsrv01.corp.example.local");
        body.Domain.Should().Be("corp.example.local");
    }

    [Fact]
    public void GetHostInfo_WorkgroupHostHasNullDomain()
    {
        var controller = new SystemController(
            new FakeHostIdentity(new HostIdentity("NPSRV01", "NPSRV01", null)));

        var body = ((OkObjectResult)controller.GetHostInfo().Result!).Value as HostInfoResponse;
        body!.Domain.Should().BeNull();
        body.Fqdn.Should().Be("NPSRV01");
    }

    [Fact]
    public void BuildIdentity_DomainJoined_AppendsDomain()
    {
        var id = HostIdentityProvider.BuildIdentity("NPSRV01", "npsrv01", "corp.example.local");
        id.MachineName.Should().Be("NPSRV01");
        id.Domain.Should().Be("corp.example.local");
        id.Fqdn.Should().Be("npsrv01.corp.example.local");
    }

    [Fact]
    public void BuildIdentity_Workgroup_NoDomain_FqdnIsHost()
    {
        var id = HostIdentityProvider.BuildIdentity("NPSRV01", "npsrv01", "");
        id.Domain.Should().BeNull();
        id.Fqdn.Should().Be("npsrv01");
    }

    [Fact]
    public void BuildIdentity_AlreadyQualifiedHost_DoesNotDoubleAppend()
    {
        var id = HostIdentityProvider.BuildIdentity("NPSRV01", "npsrv01.corp.example.local", "corp.example.local");
        id.Fqdn.Should().Be("npsrv01.corp.example.local");
    }

    [Fact]
    public void BuildIdentity_EmptyHostName_FallsBackToMachineName()
    {
        var id = HostIdentityProvider.BuildIdentity("NPSRV01", "", "corp.example.local");
        id.Fqdn.Should().Be("NPSRV01.corp.example.local");
    }
}

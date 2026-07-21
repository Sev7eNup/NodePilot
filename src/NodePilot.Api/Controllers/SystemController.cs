using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NodePilot.Api.Dtos;
using NodePilot.Core.Interfaces;

namespace NodePilot.Api.Controllers;

/// <summary>
/// Read-only metadata about the running API host. Open to every authenticated role
/// (Admin/Operator/Viewer) — unlike the Admin-only <c>/api/admin/settings/system-info</c>,
/// this surfaces only non-sensitive host identity so the SPA header can show which server
/// the current session is connected to. Intentionally not anonymous: internal host names
/// should not be readable without authentication.
/// </summary>
[ApiController]
[Route("api/system")]
[Authorize]
public sealed class SystemController : ControllerBase
{
    private readonly IHostIdentityProvider _hostIdentity;

    public SystemController(IHostIdentityProvider hostIdentity)
    {
        _hostIdentity = hostIdentity;
    }

    [HttpGet("host-info")]
    public ActionResult<HostInfoResponse> GetHostInfo()
    {
        var id = _hostIdentity.Current;
        return Ok(new HostInfoResponse(id.MachineName, id.Fqdn, id.Domain));
    }
}

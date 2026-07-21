using Microsoft.AspNetCore.Mvc;
using NodePilot.Api.Security.Scim;

namespace NodePilot.Api.Controllers;

[ApiController]
[ScimAuthorize]
[Route("api/scim/v2/Groups")]
public sealed class ScimGroupsController(
    ScimProvisioningService provisioning) : ScimControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] string? filter = null,
        [FromQuery] int startIndex = 1,
        [FromQuery] int count = 100,
        CancellationToken ct = default)
        => FromService(await provisioning.ListGroupsAsync(
            filter, startIndex, count, ScimBaseUrl(), ct));

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
        => FromService(await provisioning.GetGroupAsync(id, ScimBaseUrl(), ct));

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] ScimGroupWriteRequest request, CancellationToken ct)
    {
        // AuditLog.Add is staged atomically with the mutation by ScimProvisioningService.
        var result = await provisioning.CreateGroupAsync(request, ScimBaseUrl(), ct);
        return FromService(result, result.Value?.Meta.Location);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Replace(Guid id, [FromBody] ScimGroupWriteRequest request, CancellationToken ct)
    {
        // AuditLog.Add is staged atomically with the mutation by ScimProvisioningService.
        var result = await provisioning.ReplaceGroupAsync(id, request, ScimBaseUrl(), ct);
        return FromService(result);
    }

    [HttpPatch("{id:guid}")]
    public async Task<IActionResult> Patch(Guid id, [FromBody] ScimPatchRequest request, CancellationToken ct)
    {
        // AuditLog.Add is staged atomically with the mutation by ScimProvisioningService.
        var result = await provisioning.PatchGroupAsync(id, request, ScimBaseUrl(), ct);
        return FromService(result);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        // AuditLog.Add is staged atomically with the mutation by ScimProvisioningService.
        var result = await provisioning.DeleteGroupAsync(id, ct);
        return result.Succeeded ? NoContent() : FromService(result);
    }
}

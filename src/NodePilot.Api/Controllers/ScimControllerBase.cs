using Microsoft.AspNetCore.Mvc;
using NodePilot.Api.Security.Scim;

namespace NodePilot.Api.Controllers;

public abstract class ScimControllerBase : ControllerBase
{
    protected IActionResult FromService<T>(ScimServiceResult<T> result, string? location = null)
    {
        if (!result.Succeeded)
        {
            return new ObjectResult(new ScimError
            {
                Status = result.StatusCode.ToString(),
                Detail = result.Detail ?? "SCIM request failed.",
                ScimType = result.ScimType,
            })
            {
                StatusCode = result.StatusCode,
                ContentTypes = { "application/scim+json" },
            };
        }

        if (result.Created && location is not null) Response.Headers.Location = location;
        return new ObjectResult(result.Value)
        {
            StatusCode = result.StatusCode,
            ContentTypes = { "application/scim+json" },
        };
    }

    protected string ScimBaseUrl()
        => $"{Request.Scheme}://{Request.Host}{Request.PathBase}/api/scim/v2";
}

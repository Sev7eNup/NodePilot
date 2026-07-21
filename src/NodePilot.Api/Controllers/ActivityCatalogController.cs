using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NodePilot.Api.Dtos;
using NodePilot.Core.Activities;

namespace NodePilot.Api.Controllers;

[ApiController]
[Route("api/activity-catalog")]
[Authorize]
public sealed class ActivityCatalogController : ControllerBase
{
    [HttpGet]
    public ActionResult<IReadOnlyList<ActivityCatalogEntryResponse>> Get() =>
        Ok(ActivityCatalog.All.Select(ActivityCatalogEntryResponse.From).ToArray());
}

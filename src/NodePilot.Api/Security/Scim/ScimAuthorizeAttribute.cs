using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace NodePilot.Api.Security.Scim;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class ScimAuthorizeAttribute : TypeFilterAttribute
{
    public ScimAuthorizeAttribute() : base(typeof(ScimAuthorizationFilter)) { }
}

internal sealed class ScimAuthorizationFilter(ScimAuthentication authentication)
    : IAuthorizationFilter
{
    public void OnAuthorization(AuthorizationFilterContext context)
    {
        context.HttpContext.Response.Headers.CacheControl = "no-store";
        context.HttpContext.Response.Headers.Pragma = "no-cache";
        if (authentication.IsAuthorized(context.HttpContext.Request)) return;

        context.HttpContext.Response.Headers.WWWAuthenticate = "Bearer";
        context.Result = new ObjectResult(new ScimError
        {
            Status = StatusCodes.Status401Unauthorized.ToString(),
            Detail = "A valid SCIM bearer token is required.",
        })
        {
            StatusCode = StatusCodes.Status401Unauthorized,
            ContentTypes = { "application/scim+json" },
        };
    }
}

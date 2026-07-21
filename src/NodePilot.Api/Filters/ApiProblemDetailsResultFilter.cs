using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using NodePilot.Api.Controllers;

namespace NodePilot.Api.Filters;

/// <summary>
/// Normalizes legacy controller error payloads like <c>{ message }</c>, <c>{ error }</c>
/// or <c>{ code, message }</c> into ProblemDetails at the HTTP boundary.
/// </summary>
internal sealed class ApiProblemDetailsResultFilter : IAlwaysRunResultFilter
{
    public void OnResultExecuting(ResultExecutingContext context)
    {
        if (context.Result is not ObjectResult objectResult)
            return;

        var status = objectResult.StatusCode ?? StatusFromResultType(objectResult);
        if (status is null || status.Value < StatusCodes.Status400BadRequest)
            return;

        if (!ApiProblems.TryCreateFromLegacyPayload(
                context.HttpContext,
                status.Value,
                objectResult.Value,
                out var problem))
            return;

        objectResult.Value = problem;
        objectResult.DeclaredType = typeof(ProblemDetails);
        objectResult.ContentTypes.Clear();
        objectResult.ContentTypes.Add("application/problem+json");
    }

    public void OnResultExecuted(ResultExecutedContext context)
    {
    }

    private static int? StatusFromResultType(ObjectResult result) => result switch
    {
        BadRequestObjectResult => StatusCodes.Status400BadRequest,
        UnauthorizedObjectResult => StatusCodes.Status401Unauthorized,
        NotFoundObjectResult => StatusCodes.Status404NotFound,
        ConflictObjectResult => StatusCodes.Status409Conflict,
        _ => null,
    };
}

using Microsoft.AspNetCore.Mvc;
using NodePilot.Api.Security.Scim;

namespace NodePilot.Api.Controllers;

[ApiController]
[ScimAuthorize]
[Route("api/scim/v2")]
public sealed class ScimDiscoveryController : ScimControllerBase
{
    [HttpGet("ServiceProviderConfig")]
    public IActionResult ServiceProviderConfig()
        => Scim(new
        {
            schemas = new[] { "urn:ietf:params:scim:schemas:core:2.0:ServiceProviderConfig" },
            patch = new { supported = true },
            bulk = new { supported = false, maxOperations = 0, maxPayloadSize = 0 },
            filter = new { supported = true, maxResults = 100 },
            changePassword = new { supported = false },
            sort = new { supported = false },
            etag = new { supported = false },
            authenticationSchemes = new[]
            {
                new
                {
                    type = "oauthbearertoken",
                    name = "Bearer Token",
                    description = "Static high-entropy SCIM provisioning token",
                    specUri = "https://www.rfc-editor.org/rfc/rfc6750",
                    primary = true,
                },
            },
        });

    [HttpGet("ResourceTypes")]
    public IActionResult ResourceTypes()
        => Scim(new
        {
            schemas = new[] { ScimSchemas.ListResponse },
            totalResults = 2,
            Resources = new object[]
            {
                new
                {
                    schemas = new[] { "urn:ietf:params:scim:schemas:core:2.0:ResourceType" },
                    id = "User", name = "User", endpoint = "/Users", schema = ScimSchemas.User,
                },
                new
                {
                    schemas = new[] { "urn:ietf:params:scim:schemas:core:2.0:ResourceType" },
                    id = "Group", name = "Group", endpoint = "/Groups", schema = ScimSchemas.Group,
                },
            },
        });

    [HttpGet("Schemas")]
    public IActionResult Schemas()
    {
        var resources = new[] { UserSchema(), GroupSchema() };
        return Scim(new
        {
            schemas = new[] { ScimSchemas.ListResponse },
            totalResults = resources.Length,
            startIndex = 1,
            itemsPerPage = resources.Length,
            Resources = resources,
        });
    }

    [HttpGet("Schemas/{*id}")]
    public IActionResult Schema(string id)
    {
        if (string.Equals(id, ScimSchemas.User, StringComparison.Ordinal)) return Scim(UserSchema());
        if (string.Equals(id, ScimSchemas.Group, StringComparison.Ordinal)) return Scim(GroupSchema());

        return Scim(new ScimError
        {
            Status = StatusCodes.Status404NotFound.ToString(),
            Detail = "The requested SCIM schema is not supported.",
        }, StatusCodes.Status404NotFound);
    }

    private static object UserSchema() => new
    {
        schemas = new[] { ScimSchemas.Schema },
        id = ScimSchemas.User,
        name = "User",
        description = "NodePilot SCIM user attributes",
        attributes = new object[]
        {
            StringAttribute("externalId", "Stable identity-provider subject identifier.", required: true, caseExact: true, uniqueness: "server"),
            StringAttribute("userName", "Human-readable sign-in name.", required: true, caseExact: false, uniqueness: "server"),
            new
            {
                name = "active", type = "boolean", multiValued = false,
                description = "Whether the user is permitted to access NodePilot.",
                required = false, mutability = "readWrite", returned = "default", uniqueness = "none",
            },
        },
    };

    private static object GroupSchema() => new
    {
        schemas = new[] { ScimSchemas.Schema },
        id = ScimSchemas.Group,
        name = "Group",
        description = "NodePilot SCIM group attributes",
        attributes = new object[]
        {
            StringAttribute("externalId", "Stable identity-provider group identifier.", required: true, caseExact: true, uniqueness: "server"),
            StringAttribute("displayName", "Human-readable group name.", required: true, caseExact: false, uniqueness: "server"),
            new
            {
                name = "members", type = "complex", multiValued = true,
                description = "Users assigned to the group.", required = false,
                mutability = "readWrite", returned = "default", uniqueness = "none",
                subAttributes = new object[]
                {
                    StringAttribute("value", "NodePilot user resource id.", required: true, caseExact: true, uniqueness: "none"),
                    new
                    {
                        name = "$ref", type = "reference", multiValued = false,
                        description = "URI of the referenced user.", required = false,
                        caseExact = false, mutability = "readOnly", returned = "default",
                        uniqueness = "none", referenceTypes = new[] { "User" },
                    },
                    StringAttribute("display", "Display value of the referenced user.", required: false, caseExact: false, uniqueness: "none", mutability: "readOnly"),
                },
            },
        },
    };

    private static object StringAttribute(
        string name,
        string description,
        bool required,
        bool caseExact,
        string uniqueness,
        string mutability = "readWrite") => new
    {
        name,
        type = "string",
        multiValued = false,
        description,
        required,
        caseExact,
        mutability,
        returned = "default",
        uniqueness,
    };

    private ObjectResult Scim(object value, int statusCode = StatusCodes.Status200OK) => new(value)
    {
        StatusCode = statusCode,
        ContentTypes = { "application/scim+json" },
    };
}

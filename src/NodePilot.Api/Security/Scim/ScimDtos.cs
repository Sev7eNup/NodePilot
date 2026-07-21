using System.Text.Json;
using System.Text.Json.Serialization;

namespace NodePilot.Api.Security.Scim;

internal static class ScimSchemas
{
    public const string User = "urn:ietf:params:scim:schemas:core:2.0:User";
    public const string Group = "urn:ietf:params:scim:schemas:core:2.0:Group";
    public const string ListResponse = "urn:ietf:params:scim:api:messages:2.0:ListResponse";
    public const string Error = "urn:ietf:params:scim:api:messages:2.0:Error";
    public const string Patch = "urn:ietf:params:scim:api:messages:2.0:PatchOp";
    public const string Schema = "urn:ietf:params:scim:schemas:core:2.0:Schema";
}

public sealed class ScimUserWriteRequest
{
    [JsonPropertyName("schemas")]
    public string[]? Schemas { get; init; }

    [JsonPropertyName("externalId")]
    public string? ExternalId { get; init; }

    [JsonPropertyName("userName")]
    public string? UserName { get; init; }

    [JsonPropertyName("active")]
    public bool? Active { get; init; }
}

public sealed class ScimGroupWriteRequest
{
    [JsonPropertyName("schemas")]
    public string[]? Schemas { get; init; }

    [JsonPropertyName("externalId")]
    public string? ExternalId { get; init; }

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; init; }

    [JsonPropertyName("members")]
    public List<ScimMember>? Members { get; init; }
}

public sealed class ScimPatchRequest
{
    [JsonPropertyName("schemas")]
    public string[]? Schemas { get; init; }

    [JsonPropertyName("Operations")]
    public List<ScimPatchOperation>? Operations { get; init; }
}

public sealed class ScimPatchOperation
{
    [JsonPropertyName("op")]
    public string? Op { get; init; }

    [JsonPropertyName("path")]
    public string? Path { get; init; }

    [JsonPropertyName("value")]
    public JsonElement Value { get; init; }
}

public sealed class ScimMember
{
    [JsonPropertyName("value")]
    public string? Value { get; init; }

    [JsonPropertyName("display")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Display { get; init; }

    [JsonPropertyName("$ref")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Reference { get; init; }
}

public sealed class ScimUserResource
{
    [JsonPropertyName("schemas")]
    public string[] Schemas { get; init; } = [ScimSchemas.User];

    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("externalId")]
    public required string ExternalId { get; init; }

    [JsonPropertyName("userName")]
    public required string UserName { get; init; }

    [JsonPropertyName("active")]
    public bool Active { get; init; }

    [JsonPropertyName("meta")]
    public required ScimMeta Meta { get; init; }
}

public sealed class ScimGroupResource
{
    [JsonPropertyName("schemas")]
    public string[] Schemas { get; init; } = [ScimSchemas.Group];

    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("externalId")]
    public required string ExternalId { get; init; }

    [JsonPropertyName("displayName")]
    public required string DisplayName { get; init; }

    [JsonPropertyName("members")]
    public IReadOnlyList<ScimMember> Members { get; init; } = [];

    [JsonPropertyName("meta")]
    public required ScimMeta Meta { get; init; }
}

public sealed class ScimMeta
{
    [JsonPropertyName("resourceType")]
    public required string ResourceType { get; init; }

    [JsonPropertyName("created")]
    public DateTime Created { get; init; }

    [JsonPropertyName("lastModified")]
    public DateTime LastModified { get; init; }

    [JsonPropertyName("location")]
    public required string Location { get; init; }
}

public sealed class ScimListResponse<T>
{
    [JsonPropertyName("schemas")]
    public string[] Schemas { get; init; } = [ScimSchemas.ListResponse];

    [JsonPropertyName("totalResults")]
    public int TotalResults { get; init; }

    [JsonPropertyName("startIndex")]
    public int StartIndex { get; init; }

    [JsonPropertyName("itemsPerPage")]
    public int ItemsPerPage { get; init; }

    [JsonPropertyName("Resources")]
    public IReadOnlyList<T> Resources { get; init; } = [];
}

public sealed class ScimError
{
    [JsonPropertyName("schemas")]
    public string[] Schemas { get; init; } = [ScimSchemas.Error];

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("scimType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ScimType { get; init; }

    [JsonPropertyName("detail")]
    public required string Detail { get; init; }
}

public sealed record ScimServiceResult<T>(
    T? Value,
    int StatusCode,
    string? Detail = null,
    string? ScimType = null,
    bool Created = false)
{
    public bool Succeeded => Value is not null && StatusCode is >= 200 and < 300;

    public static ScimServiceResult<T> Ok(T value, bool created = false)
        => new(value, created ? StatusCodes.Status201Created : StatusCodes.Status200OK, Created: created);

    public static ScimServiceResult<T> Fail(int status, string detail, string? type = null)
        => new(default, status, detail, type);
}

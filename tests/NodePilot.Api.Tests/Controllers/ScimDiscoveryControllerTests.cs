using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NodePilot.Api.Controllers;
using NodePilot.Api.Security.Scim;
using Xunit;

namespace NodePilot.Api.Tests.Controllers;

public sealed class ScimDiscoveryControllerTests
{
    [Fact]
    public void Schemas_AdvertisesOnlySupportedUserAndGroupSchemas()
    {
        var result = new ScimDiscoveryController().Schemas().Should().BeOfType<ObjectResult>().Subject;

        result.StatusCode.Should().Be(StatusCodes.Status200OK);
        result.ContentTypes.Should().Contain("application/scim+json");
        using var json = JsonSerializer.SerializeToDocument(result.Value, JsonOptions());
        var root = json.RootElement;
        root.GetProperty("totalResults").GetInt32().Should().Be(2);
        root.GetProperty("itemsPerPage").GetInt32().Should().Be(2);
        root.GetProperty("resources").EnumerateArray()
            .Select(resource => resource.GetProperty("id").GetString())
            .Should().BeEquivalentTo(ScimSchemas.User, ScimSchemas.Group);
    }

    [Fact]
    public void UserSchema_DescribesTheAcceptedIdentityAttributes()
    {
        var result = new ScimDiscoveryController().Schema(ScimSchemas.User)
            .Should().BeOfType<ObjectResult>().Subject;

        using var json = JsonSerializer.SerializeToDocument(result.Value, JsonOptions());
        var root = json.RootElement;
        root.GetProperty("schemas")[0].GetString().Should().Be(ScimSchemas.Schema);
        root.GetProperty("id").GetString().Should().Be(ScimSchemas.User);
        root.GetProperty("attributes").EnumerateArray()
            .Select(attribute => attribute.GetProperty("name").GetString())
            .Should().BeEquivalentTo("externalId", "userName", "active");
    }

    [Fact]
    public void UnknownSchema_ReturnsScim404()
    {
        var result = new ScimDiscoveryController().Schema("unsupported")
            .Should().BeOfType<ObjectResult>().Subject;

        result.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        result.ContentTypes.Should().Contain("application/scim+json");
        var error = result.Value.Should().BeOfType<ScimError>().Subject;
        error.Status.Should().Be("404");
        error.Schemas.Should().ContainSingle(ScimSchemas.Error);
    }

    private static JsonSerializerOptions JsonOptions() => new(JsonSerializerDefaults.Web);
}

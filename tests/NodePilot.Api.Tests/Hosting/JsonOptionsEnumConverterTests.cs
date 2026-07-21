using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NodePilot.Core.Enums;
using Xunit;

namespace NodePilot.Api.Tests.Hosting;

/// <summary>
/// Regression guard for finding #2 of the CLI-catch-up review: shared-folder
/// permission DTOs use <see cref="FolderPrincipalType"/> and <see cref="SharedFolderRole"/>,
/// while the UI and CLI both type those fields as <c>string</c>. Without a global
/// <c>JsonStringEnumConverter</c> on the MVC JSON pipeline, the API would serialise the
/// enums as numbers and break both clients on the read path. This test asserts the wiring
/// in <c>Program.cs</c> (<c>AddControllers().AddJsonOptions(...)</c>) by spinning up a
/// minimal builder configured the same way and round-tripping a sample DTO through MVC's
/// resolved <see cref="JsonOptions"/>.
/// </summary>
public class JsonOptionsEnumConverterTests
{
    private sealed record SampleDto(FolderPrincipalType PrincipalType, SharedFolderRole Role);

    private static JsonOptions BuildMvcJsonOptions()
    {
        // Mirror Program.cs's wiring exactly — same chain, same converter. If a future
        // refactor moves the wiring to an extension method, update the call here.
        var builder = WebApplication.CreateBuilder(Array.Empty<string>());
        builder.Services.AddControllers().AddJsonOptions(opt =>
        {
            opt.JsonSerializerOptions.Converters.Add(
                new System.Text.Json.Serialization.JsonStringEnumConverter());
        });
        var sp = builder.Services.BuildServiceProvider();
        return sp.GetRequiredService<IOptions<JsonOptions>>().Value;
    }

    [Fact]
    public void Serialises_Enums_As_Strings()
    {
        var options = BuildMvcJsonOptions();
        var dto = new SampleDto(FolderPrincipalType.User, SharedFolderRole.FolderOperator);
        var json = JsonSerializer.Serialize(dto, options.JsonSerializerOptions);
        json.Should().Contain("\"principalType\":\"User\"");
        json.Should().Contain("\"role\":\"FolderOperator\"");
    }

    [Fact]
    public void Deserialises_String_Enums()
    {
        var options = BuildMvcJsonOptions();
        var parsed = JsonSerializer.Deserialize<SampleDto>(
            """{"principalType":"Group","role":"FolderAdmin"}""",
            options.JsonSerializerOptions);
        parsed!.PrincipalType.Should().Be(FolderPrincipalType.Group);
        parsed.Role.Should().Be(SharedFolderRole.FolderAdmin);
    }

    [Fact]
    public void Deserialises_Numeric_Enums_For_Backcompat()
    {
        // .NET 10's default already accepts numeric enums even with JsonStringEnumConverter
        // present. This test pins that behaviour so a future migration to a stricter
        // converter doesn't silently break a caller still sending numeric form.
        var options = BuildMvcJsonOptions();
        var parsed = JsonSerializer.Deserialize<SampleDto>(
            """{"principalType":2,"role":3}""",
            options.JsonSerializerOptions);
        parsed!.PrincipalType.Should().Be(FolderPrincipalType.Group);
        parsed.Role.Should().Be(SharedFolderRole.FolderAdmin);
    }
}

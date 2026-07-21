using System.Text.Json;
using FluentAssertions;
using NodePilot.Api.Services.DbAdmin;
using NodePilot.Core.Enums;
using Xunit;

namespace NodePilot.Api.Tests.Services;

/// <summary>
/// <see cref="DbAdminQueryBuilder.CoerceJsonValue"/> converts a JSON cell from the DB-admin
/// editor into the target column's CLR type. It is the type-safety boundary for row edits —
/// a wrong coercion (or a swallowed conversion error) would write malformed data or 500 the
/// request. Pin every supported type plus the rejection paths (null into a value type,
/// unparseable value, unsupported type).
/// </summary>
public class DbAdminQueryBuilderCoerceTests
{
    private static JsonElement E(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    [Fact]
    public void Null_IntoNullableType_ReturnsNull()
    {
        DbAdminQueryBuilder.CoerceJsonValue(E("null"), typeof(int?)).Should().BeNull();
    }

    [Fact]
    public void Null_IntoNonNullableValueType_Throws()
    {
        var act = () => DbAdminQueryBuilder.CoerceJsonValue(E("null"), typeof(int));
        act.Should().Throw<ArgumentException>().WithMessage("*non-nullable*");
    }

    [Fact]
    public void String_IntoString_ReturnsValue()
    {
        DbAdminQueryBuilder.CoerceJsonValue(E("\"hello\""), typeof(string)).Should().Be("hello");
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    public void Bool_Roundtrips(string json, bool expected)
    {
        DbAdminQueryBuilder.CoerceJsonValue(E(json), typeof(bool)).Should().Be(expected);
    }

    [Fact]
    public void Numbers_CoerceToTheirClrTypes()
    {
        DbAdminQueryBuilder.CoerceJsonValue(E("42"), typeof(int)).Should().Be(42);
        DbAdminQueryBuilder.CoerceJsonValue(E("42"), typeof(long)).Should().Be(42L);
        DbAdminQueryBuilder.CoerceJsonValue(E("1.5"), typeof(double)).Should().Be(1.5d);
        DbAdminQueryBuilder.CoerceJsonValue(E("1.5"), typeof(decimal)).Should().Be(1.5m);
        DbAdminQueryBuilder.CoerceJsonValue(E("1.5"), typeof(float)).Should().Be(1.5f);
        DbAdminQueryBuilder.CoerceJsonValue(E("7"), typeof(short)).Should().Be((short)7);
    }

    [Fact]
    public void Guid_FromString_Parses()
    {
        var g = Guid.NewGuid();
        DbAdminQueryBuilder.CoerceJsonValue(E($"\"{g}\""), typeof(Guid)).Should().Be(g);
    }

    [Fact]
    public void DateTime_FromIsoString_ReturnsUtcKind()
    {
        var value = DbAdminQueryBuilder.CoerceJsonValue(E("\"2026-07-08T10:30:00Z\""), typeof(DateTime));
        value.Should().BeOfType<DateTime>();
        var dt = (DateTime)value!;
        dt.Kind.Should().Be(DateTimeKind.Utc);
        dt.Hour.Should().Be(10);
    }

    [Fact]
    public void Enum_ValidName_Parses()
    {
        DbAdminQueryBuilder.CoerceJsonValue(E("\"Succeeded\""), typeof(ExecutionStatus))
            .Should().Be(ExecutionStatus.Succeeded);
    }

    [Fact]
    public void Enum_InvalidName_Throws()
    {
        var act = () => DbAdminQueryBuilder.CoerceJsonValue(E("\"NotAStatus\""), typeof(ExecutionStatus));
        act.Should().Throw<ArgumentException>().WithMessage("*not a valid*");
    }

    [Fact]
    public void TypeMismatch_WrappedAsArgumentException()
    {
        // GetInt32 on a string element throws InvalidOperationException internally; the builder
        // must translate that into an ArgumentException so the caller returns 400, not 500.
        var act = () => DbAdminQueryBuilder.CoerceJsonValue(E("\"not-a-number\""), typeof(int));
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void UnsupportedColumnType_Throws()
    {
        var act = () => DbAdminQueryBuilder.CoerceJsonValue(E("\"00:05:00\""), typeof(TimeSpan));
        act.Should().Throw<ArgumentException>().WithMessage("*Unsupported column type*");
    }
}

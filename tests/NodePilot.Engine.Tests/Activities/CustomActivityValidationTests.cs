using FluentAssertions;
using NodePilot.Core.Activities;
using Xunit;

namespace NodePilot.Engine.Tests.Activities;

/// <summary>
/// Exercises every rejection + acceptance branch of <see cref="CustomActivityValidation.Validate"/>
/// (key/name/icon/engine + per-parameter name/type/label/reserved/duplicate rules).
/// </summary>
public class CustomActivityValidationTests
{
    private static CustomActivityInputParameter Input(string name, string type = "string", string label = "L")
        => new(name, label, type);

    private static CustomActivityOutputParameter Output(string name, string type = "string")
        => new(name, type);

    // ---------- key ----------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("bad key")]   // space
    [InlineData("bad!key")]   // illegal char
    public void Validate_RequireKey_InvalidKey_ReturnsError(string? key)
    {
        var error = CustomActivityValidation.Validate(key, "Name", "extension", "auto", [], [], requireKey: true);
        error.Should().Be("Key must match [A-Za-z0-9_-]{1,64}.");
    }

    [Fact]
    public void Validate_RequireKey_ValidKey_Accepted()
    {
        var error = CustomActivityValidation.Validate("disk-check_1", "Name", "extension", "auto", [], [], requireKey: true);
        error.Should().BeNull();
    }

    [Fact]
    public void Validate_NoRequireKey_NullKeyIsAccepted()
    {
        // When requireKey is false the key block is skipped entirely.
        var error = CustomActivityValidation.Validate(null, "Name", "extension", "auto", [], [], requireKey: false);
        error.Should().BeNull();
    }

    // ---------- name ----------

    [Fact]
    public void Validate_BlankName_ReturnsError()
    {
        var error = CustomActivityValidation.Validate("k", "   ", "extension", "auto", [], [], requireKey: true);
        error.Should().Be("Name is required and must be at most 200 characters.");
    }

    [Fact]
    public void Validate_TooLongName_ReturnsError()
    {
        var error = CustomActivityValidation.Validate("k", new string('x', 201), "extension", "auto", [], [], requireKey: true);
        error.Should().Be("Name is required and must be at most 200 characters.");
    }

    // ---------- icon ----------

    [Theory]
    [InlineData("")]           // empty
    [InlineData("Extension")]  // uppercase not allowed
    [InlineData("bad-icon")]   // hyphen not allowed
    public void Validate_InvalidIcon_ReturnsError(string icon)
    {
        var error = CustomActivityValidation.Validate("k", "Name", icon, "auto", [], [], requireKey: true);
        error.Should().Be("Icon must be a Material Symbol name ([a-z0-9_], max 60).");
    }

    // ---------- engine ----------

    [Fact]
    public void Validate_UnknownEngine_ReturnsError()
    {
        var error = CustomActivityValidation.Validate("k", "Name", "extension", "bash", [], [], requireKey: true);
        error.Should().Be("Engine must be one of: auto, pwsh, powershell.");
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("pwsh")]
    [InlineData("powershell")]
    public void Validate_AllowedEngines_Accepted(string engine)
    {
        var error = CustomActivityValidation.Validate("k", "Name", "extension", engine, [], [], requireKey: true);
        error.Should().BeNull();
    }

    // ---------- input parameters ----------

    [Fact]
    public void Validate_InputBadName_ReturnsError()
    {
        var error = CustomActivityValidation.Validate("k", "Name", "extension", "auto",
            [Input("bad-name")], [], requireKey: true);
        error.Should().Be("Input parameter name 'bad-name' must match [A-Za-z0-9_]+.");
    }

    [Fact]
    public void Validate_InputReservedName_ReturnsError()
    {
        var error = CustomActivityValidation.Validate("k", "Name", "extension", "auto",
            [Input("args")], [], requireKey: true);
        error.Should().Be("Input parameter name 'args' is reserved.");
    }

    [Fact]
    public void Validate_InputUnsupportedType_ReturnsError()
    {
        // "object" is an output-only type — not valid for inputs.
        var error = CustomActivityValidation.Validate("k", "Name", "extension", "auto",
            [Input("myInput", type: "object")], [], requireKey: true);
        error.Should().Be("Input parameter 'myInput' has unsupported type 'object'.");
    }

    [Fact]
    public void Validate_InputMissingLabel_ReturnsError()
    {
        var error = CustomActivityValidation.Validate("k", "Name", "extension", "auto",
            [new CustomActivityInputParameter("myInput", "   ", "string")], [], requireKey: true);
        error.Should().Be("Input parameter 'myInput' needs a label.");
    }

    [Fact]
    public void Validate_DuplicateInputName_ReturnsError()
    {
        // Duplicate detection is case-insensitive; the second occurrence trips it.
        var error = CustomActivityValidation.Validate("k", "Name", "extension", "auto",
            [Input("dup"), Input("DUP")], [], requireKey: true);
        error.Should().Be("Duplicate input parameter name 'DUP'.");
    }

    // ---------- output parameters ----------

    [Fact]
    public void Validate_OutputBadName_ReturnsError()
    {
        var error = CustomActivityValidation.Validate("k", "Name", "extension", "auto",
            [], [Output("bad-name")], requireKey: true);
        error.Should().Be("Output parameter name 'bad-name' must match [A-Za-z0-9_]+.");
    }

    [Fact]
    public void Validate_OutputReservedName_ReturnsError()
    {
        var error = CustomActivityValidation.Validate("k", "Name", "extension", "auto",
            [], [Output("exitCode")], requireKey: true);
        error.Should().Be("Output parameter name 'exitCode' is reserved (exitCode is always provided automatically).");
    }

    [Fact]
    public void Validate_OutputUnsupportedType_ReturnsError()
    {
        // "select" is an input-only type — not valid for outputs.
        var error = CustomActivityValidation.Validate("k", "Name", "extension", "auto",
            [], [Output("myOut", "select")], requireKey: true);
        error.Should().Be("Output parameter 'myOut' has unsupported type 'select'.");
    }

    [Fact]
    public void Validate_DuplicateOutputName_ReturnsError()
    {
        var error = CustomActivityValidation.Validate("k", "Name", "extension", "auto",
            [], [Output("dup"), Output("dup")], requireKey: true);
        error.Should().Be("Duplicate output parameter name 'dup'.");
    }

    // ---------- full happy path ----------

    [Fact]
    public void Validate_FullValidDefinition_ReturnsNull()
    {
        var error = CustomActivityValidation.Validate("disk_check", "Disk Check", "extension", "auto",
            [Input("path", "string", "Path"), Input("depth", "number", "Depth")],
            [Output("status", "string"), Output("count", "number")], requireKey: true);
        error.Should().BeNull();
    }
}

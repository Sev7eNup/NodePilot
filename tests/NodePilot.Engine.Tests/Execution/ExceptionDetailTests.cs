using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NodePilot.Engine.Execution;
using Xunit;

namespace NodePilot.Engine.Tests.Execution;

/// <summary>
/// <see cref="ExceptionDetail.Describe"/> exists because persisting only
/// <c>ex.Message</c> hid the diagnosis behind wrapper exceptions. The canonical case is
/// <see cref="DbUpdateException"/>, whose message is the constant
/// "An error occurred while saving the entity changes. See the inner exception for details."
/// </summary>
public class ExceptionDetailTests
{
    [Fact]
    public void Describe_Null_ReturnsEmpty()
        => ExceptionDetail.Describe(null).Should().BeEmpty();

    [Fact]
    public void Describe_SingleException_ReturnsItsMessage()
        => ExceptionDetail.Describe(new InvalidOperationException("boom")).Should().Be("boom");

    [Fact]
    public void Describe_DbUpdateException_SurfacesTheInnerCause()
    {
        // The exact shape that produced an unusable 87-character step error in the field.
        var ex = new DbUpdateException(
            "An error occurred while saving the entity changes. See the inner exception for details.",
            new Exception("Violation of PRIMARY KEY constraint 'PK_StepExecutions'."));

        var described = ExceptionDetail.Describe(ex);

        described.Should().Contain("PK_StepExecutions",
            "the primary-key violation is the only actionable part of the message");
        described.Should().StartWith("An error occurred while saving the entity changes");
    }

    [Fact]
    public void Describe_NestedChain_JoinsLinksInOrder()
    {
        var ex = new Exception("outer", new Exception("middle", new Exception("root")));

        ExceptionDetail.Describe(ex).Should().Be("outer -> middle -> root");
    }

    [Fact]
    public void Describe_WrapperRepeatingItsCause_DoesNotDuplicateTheText()
    {
        // Several BCL wrappers embed the inner message verbatim; repeating it doubles the
        // line length without adding information.
        var inner = new Exception("connection refused");
        var ex = new Exception("send failed: connection refused", inner);

        ExceptionDetail.Describe(ex).Should().Be("send failed: connection refused");
    }

    [Fact]
    public void Describe_AggregateWithSingleInner_UnwrapsIt()
    {
        var ex = new AggregateException(new InvalidOperationException("the real cause"));

        ExceptionDetail.Describe(ex).Should().Be("the real cause");
    }

    [Fact]
    public void Describe_AggregateWithMultipleInners_KeepsTheAggregate()
    {
        // Collapsing a genuine multi-error aggregate would silently drop failures.
        var ex = new AggregateException(new Exception("first"), new Exception("second"));

        var described = ExceptionDetail.Describe(ex);

        described.Should().Contain("first");
    }

    [Fact]
    public void Describe_LongChain_IsBounded()
    {
        Exception ex = new("level5");
        foreach (var level in new[] { "level4", "level3", "level2", "level1", "level0" })
            ex = new Exception(level, ex);

        var described = ExceptionDetail.Describe(ex);

        // Bounded so a deep chain cannot turn a step error into a stack dump.
        described.Split(" -> ").Should().HaveCount(4);
        described.Should().StartWith("level0");
    }

    [Fact]
    public void Describe_EmptyMessage_FallsBackToTypeName()
    {
        var described = ExceptionDetail.Describe(new EmptyMessageException());

        described.Should().Be(nameof(EmptyMessageException));
    }

    private sealed class EmptyMessageException : Exception
    {
        public override string Message => string.Empty;
    }
}

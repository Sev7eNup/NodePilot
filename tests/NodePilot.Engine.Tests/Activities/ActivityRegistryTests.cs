using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NodePilot.Core.Interfaces;
using NodePilot.Engine;
using Xunit;

namespace NodePilot.Engine.Tests.Activities;

public class ActivityRegistryTests
{
    private static Mock<IActivityExecutor> CreateMockExecutor(string activityType)
    {
        var mock = new Mock<IActivityExecutor>();
        mock.Setup(e => e.ActivityType).Returns(activityType);
        return mock;
    }

    [Fact]
    public void GetExecutor_RegisteredType_ReturnsCorrectExecutor()
    {
        var executor = CreateMockExecutor("runScript");
        var registry = new ActivityRegistry(new[] { executor.Object });

        var result = registry.GetExecutor("runScript");

        result.Should().BeSameAs(executor.Object);
    }

    [Fact]
    public void GetExecutor_UnknownType_ThrowsInvalidOperationException()
    {
        var executor = CreateMockExecutor("runScript");
        var registry = new ActivityRegistry(new[] { executor.Object });

        var act = () => registry.GetExecutor("nonExistent");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*nonExistent*");
    }

    [Fact]
    public void GetRegisteredTypes_ReturnsAllRegisteredTypes()
    {
        var exec1 = CreateMockExecutor("runScript");
        var exec2 = CreateMockExecutor("restApi");
        var exec3 = CreateMockExecutor("email");
        var registry = new ActivityRegistry(new[] { exec1.Object, exec2.Object, exec3.Object });

        var types = registry.GetRegisteredTypes();

        types.Should().HaveCount(3);
        types.Should().Contain("runScript");
        types.Should().Contain("restApi");
        types.Should().Contain("email");
    }

    [Fact]
    public void Constructor_CaseInsensitive_FindsExecutor()
    {
        var executor = CreateMockExecutor("RunScript");
        var registry = new ActivityRegistry(new[] { executor.Object });

        var result = registry.GetExecutor("runscript");

        result.Should().BeSameAs(executor.Object);
    }

    /// <summary>
    /// Production ctor: scans the DI container's IActivityExecutor registrations once,
    /// caches a type-only map, and resolves fresh instances from the per-step scope on
    /// every GetExecutor call. Verifies that two GetExecutor calls with two different
    /// scopes return two different executor instances (i.e. scoping is honoured).
    /// </summary>
    [Fact]
    public void Production_GetExecutor_WithScopedProvider_ResolvesFreshPerScope()
    {
        var services = new ServiceCollection();
        services.AddScoped<TestExecutor>();
        services.AddScoped<IActivityExecutor>(sp => sp.GetRequiredService<TestExecutor>());
        using var rootProvider = services.BuildServiceProvider();

        var registry = new ActivityRegistry(rootProvider);

        using var scope1 = rootProvider.CreateScope();
        using var scope2 = rootProvider.CreateScope();

        var exec1 = registry.GetExecutor("test", scope1.ServiceProvider);
        var exec1Again = registry.GetExecutor("test", scope1.ServiceProvider);
        var exec2 = registry.GetExecutor("test", scope2.ServiceProvider);

        exec1.Should().BeOfType<TestExecutor>();
        exec1Again.Should().BeSameAs(exec1, "same scope should return the same scoped instance");
        exec2.Should().NotBeSameAs(exec1, "a different scope should produce a fresh executor instance");
    }

    [Fact]
    public void Production_GetRegisteredTypes_ListsScannedActivityTypes()
    {
        var services = new ServiceCollection();
        services.AddScoped<TestExecutor>();
        services.AddScoped<IActivityExecutor>(sp => sp.GetRequiredService<TestExecutor>());
        using var rootProvider = services.BuildServiceProvider();

        var registry = new ActivityRegistry(rootProvider);

        registry.GetRegisteredTypes().Should().Contain("test");
    }

    private sealed class TestExecutor : IActivityExecutor
    {
        public string ActivityType => "test";
        public Task<ActivityResult> ExecuteAsync(StepExecutionContext context, System.Text.Json.JsonElement config, CancellationToken ct) =>
            Task.FromResult(new ActivityResult { Success = true });
    }
}

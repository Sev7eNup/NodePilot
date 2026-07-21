using Moq;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;

namespace NodePilot.Engine.Tests.Helpers;

public static class MockRemoteSession
{
    public static Mock<IRemoteSessionFactory> CreateFactory(RemoteExecutionResult? defaultResult = null)
    {
        var result = defaultResult ?? new RemoteExecutionResult
        {
            Success = true,
            Output = "OK",
            ErrorOutput = "",
            Duration = TimeSpan.FromMilliseconds(100)
        };

        var mockSession = new Mock<IRemoteSession>();
        mockSession
            .Setup(s => s.ExecuteScriptAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);
        mockSession
            .Setup(s => s.DisposeAsync())
            .Returns(ValueTask.CompletedTask);

        var mockFactory = new Mock<IRemoteSessionFactory>();
        mockFactory
            .Setup(f => f.CreateSessionAsync(It.IsAny<ManagedMachine>(), It.IsAny<Credential>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockSession.Object);

        return mockFactory;
    }

    public static Mock<IRemoteSessionFactory> CreateFailingFactory(string errorMessage = "Connection failed")
    {
        var result = new RemoteExecutionResult
        {
            Success = false,
            Output = "",
            ErrorOutput = errorMessage,
            Duration = TimeSpan.FromMilliseconds(50)
        };

        return CreateFactory(result);
    }
}

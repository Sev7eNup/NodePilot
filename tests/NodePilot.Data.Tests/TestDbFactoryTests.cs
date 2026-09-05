using System.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Data.Tests;

/// <summary>
/// Connection ownership of the shared test factory. <c>Create()</c> hands out only the context,
/// so the context must close the connection on dispose; <c>CreateWithConnection()</c> hands the
/// connection to the caller, who keeps it across contexts.
/// </summary>
public class TestDbFactoryTests
{
    [Fact]
    public void Create_DisposingTheContext_ClosesTheConnection()
    {
        var context = TestDbFactory.Create();
        var connection = context.Database.GetDbConnection();
        connection.State.Should().Be(ConnectionState.Open);

        context.Dispose();

        connection.State.Should().Be(ConnectionState.Closed);
    }

    [Fact]
    public void CreateWithConnection_DisposingTheContext_KeepsTheCallersConnectionOpen()
    {
        var (connection, context) = TestDbFactory.CreateWithConnection();

        context.Dispose();

        connection.State.Should().Be(ConnectionState.Open);
        connection.Dispose();
    }
}

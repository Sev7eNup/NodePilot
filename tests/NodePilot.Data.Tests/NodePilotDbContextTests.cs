using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using NodePilot.Core.Enums;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Data.Tests;

public sealed class NodePilotDbContextTests : IDisposable
{
    private readonly NodePilotDbContext _context;

    public NodePilotDbContextTests()
    {
        _context = TestDbFactory.Create();
    }

    public void Dispose()
    {
        _context.Dispose();
    }

    private static NodePilotDbContext CreateContext() => TestDbFactory.Create();

    [Fact]
    public void CanCreateDatabase_InMemory()
    {
        using var context = CreateContext();
        // If we get here without exception, EnsureCreated succeeded
        context.Workflows.Should().NotBeNull();
        context.WorkflowExecutions.Should().NotBeNull();
        context.StepExecutions.Should().NotBeNull();
        context.ManagedMachines.Should().NotBeNull();
        context.Credentials.Should().NotBeNull();
        context.Users.Should().NotBeNull();
        context.AuditLog.Should().NotBeNull();
    }

    [Fact]
    public async Task Workflow_CRUD_Operations()
    {
        // Create
        var workflow = new Workflow
        {
            Id = Guid.NewGuid(),
            Name = "Test Workflow",
            Description = "A test workflow",
            DefinitionJson = "{\"steps\":[]}",
            Version = 1,
            IsEnabled = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            CreatedBy = "tester"
        };

        _context.Workflows.Add(workflow);
        await _context.SaveChangesAsync();

        // Read
        var loaded = await _context.Workflows.FindAsync(workflow.Id);
        loaded.Should().NotBeNull();
        loaded!.Name.Should().Be("Test Workflow");
        loaded.Description.Should().Be("A test workflow");
        loaded.DefinitionJson.Should().Be("{\"steps\":[]}");
        loaded.CreatedBy.Should().Be("tester");

        // Update
        loaded.Name = "Updated Workflow";
        loaded.Version = 2;
        loaded.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        var updated = await _context.Workflows.FindAsync(workflow.Id);
        updated!.Name.Should().Be("Updated Workflow");
        updated.Version.Should().Be(2);

        // Delete
        _context.Workflows.Remove(updated);
        await _context.SaveChangesAsync();

        var deleted = await _context.Workflows.FindAsync(workflow.Id);
        deleted.Should().BeNull();
    }

    [Fact]
    public async Task WorkflowExecution_CascadeDelete_WhenWorkflowDeleted()
    {
        var workflow = new Workflow
        {
            Id = Guid.NewGuid(),
            Name = "Cascade Test",
            DefinitionJson = "{}"
        };

        var execution = new WorkflowExecution
        {
            Id = Guid.NewGuid(),
            WorkflowId = workflow.Id,
            Status = ExecutionStatus.Running,
            StartedAt = DateTime.UtcNow
        };

        _context.Workflows.Add(workflow);
        _context.WorkflowExecutions.Add(execution);
        await _context.SaveChangesAsync();

        // Verify the execution exists
        (await _context.WorkflowExecutions.FindAsync(execution.Id)).Should().NotBeNull();

        // Delete the workflow
        _context.Workflows.Remove(workflow);
        await _context.SaveChangesAsync();

        // Execution should be cascade-deleted
        (await _context.WorkflowExecutions.FindAsync(execution.Id)).Should().BeNull();
    }

    [Fact]
    public async Task StepExecution_CascadeDelete_WhenExecutionDeleted()
    {
        var workflow = new Workflow
        {
            Id = Guid.NewGuid(),
            Name = "Step Cascade Test",
            DefinitionJson = "{}"
        };

        var execution = new WorkflowExecution
        {
            Id = Guid.NewGuid(),
            WorkflowId = workflow.Id,
            Status = ExecutionStatus.Running,
            StartedAt = DateTime.UtcNow
        };

        var step = new StepExecution
        {
            Id = Guid.NewGuid(),
            WorkflowExecutionId = execution.Id,
            StepId = "step-1",
            StepType = "RunScript",
            Status = ExecutionStatus.Pending
        };

        _context.Workflows.Add(workflow);
        _context.WorkflowExecutions.Add(execution);
        _context.StepExecutions.Add(step);
        await _context.SaveChangesAsync();

        // Verify the step exists
        (await _context.StepExecutions.FindAsync(step.Id)).Should().NotBeNull();

        // Delete the execution
        _context.WorkflowExecutions.Remove(execution);
        await _context.SaveChangesAsync();

        // Step should be cascade-deleted
        (await _context.StepExecutions.FindAsync(step.Id)).Should().BeNull();
    }

    [Fact]
    public async Task User_UniqueUsername_Constraint()
    {
        var user1 = new User
        {
            Id = Guid.NewGuid(),
            Username = "duplicate_user",
            PasswordHash = "hash1",
            Role = UserRole.Viewer
        };

        var user2 = new User
        {
            Id = Guid.NewGuid(),
            Username = "duplicate_user",
            PasswordHash = "hash2",
            Role = UserRole.Admin
        };

        _context.Users.Add(user1);
        await _context.SaveChangesAsync();

        _context.Users.Add(user2);

        var act = () => _context.SaveChangesAsync();
        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task ManagedMachine_DefaultCredential_SetNull_OnDelete()
    {
        var credential = new Credential
        {
            Id = Guid.NewGuid(),
            Name = "Test Cred",
            Username = "admin",
            EncryptedPassword = new byte[] { 1, 2, 3 }
        };

        var machine = new ManagedMachine
        {
            Id = Guid.NewGuid(),
            Name = "Server-01",
            Hostname = "server01.local",
            DefaultCredentialId = credential.Id
        };

        _context.Credentials.Add(credential);
        _context.ManagedMachines.Add(machine);
        await _context.SaveChangesAsync();

        // Verify the FK is set
        var loadedMachine = await _context.ManagedMachines.FindAsync(machine.Id);
        loadedMachine!.DefaultCredentialId.Should().Be(credential.Id);

        // Delete the credential
        _context.Credentials.Remove(credential);
        await _context.SaveChangesAsync();

        // Reload the machine — FK should be null
        await _context.Entry(loadedMachine).ReloadAsync();
        loadedMachine.DefaultCredentialId.Should().BeNull();
    }

    [Fact]
    public async Task EnumColumns_StoredAsString()
    {
        var workflow = new Workflow
        {
            Id = Guid.NewGuid(),
            Name = "Enum Test",
            DefinitionJson = "{}"
        };

        var execution = new WorkflowExecution
        {
            Id = Guid.NewGuid(),
            WorkflowId = workflow.Id,
            Status = ExecutionStatus.Succeeded,
            StartedAt = DateTime.UtcNow
        };

        _context.Workflows.Add(workflow);
        _context.WorkflowExecutions.Add(execution);
        await _context.SaveChangesAsync();

        // Use the underlying ADO.NET connection to read the raw stored value
        var dbConnection = _context.Database.GetDbConnection();
        using var command = dbConnection.CreateCommand();
        command.CommandText = "SELECT Status FROM WorkflowExecutions LIMIT 1";
        var statusValue = (string?)await command.ExecuteScalarAsync();

        statusValue.Should().Be("Succeeded");
    }
}

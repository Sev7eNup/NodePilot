using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using NodePilot.Api.Controllers;
using NodePilot.Api.Dtos;
using NodePilot.Core.Enums;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Data;
using Xunit;

namespace NodePilot.Api.Tests.Controllers;

public class MachinesControllerTests
{
    private static NodePilotDbContext CreateContext() => NodePilot.TestCommons.TestDbFactory.Create();

    [Fact]
    public async Task GetAll_ReturnsOrderedList()
    {
        // Arrange
        var db = CreateContext();
        var machineB = new ManagedMachine
        {
            Id = Guid.NewGuid(),
            Name = "Bravo",
            Hostname = "bravo.local"
        };
        var machineA = new ManagedMachine
        {
            Id = Guid.NewGuid(),
            Name = "Alpha",
            Hostname = "alpha.local"
        };
        db.ManagedMachines.AddRange(machineB, machineA);
        await db.SaveChangesAsync();

        var mockSessionFactory = new Mock<IRemoteSessionFactory>();
        var mockCredentialStore = new Mock<ICredentialStore>();
        var controller = new MachinesController(db, mockSessionFactory.Object, mockCredentialStore.Object, NoopAuditWriter.Instance);

        // Act
        var result = await controller.GetAll(CancellationToken.None);

        // Assert
        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var machines = ok.Value.Should().BeAssignableTo<List<MachineResponse>>().Subject;
        machines.Should().HaveCount(2);
        machines[0].Name.Should().Be("Alpha");
        machines[1].Name.Should().Be("Bravo");
    }

    [Fact]
    public async Task Create_ValidRequest_Returns201()
    {
        // Arrange
        var db = CreateContext();
        var mockSessionFactory = new Mock<IRemoteSessionFactory>();
        var mockCredentialStore = new Mock<ICredentialStore>();
        var controller = new MachinesController(db, mockSessionFactory.Object, mockCredentialStore.Object, NoopAuditWriter.Instance);
        var request = new CreateMachineRequest("Server1", "server1.local", 5985, false, null, "web,prod");

        // Act
        var result = await controller.Create(request, CancellationToken.None);

        // Assert
        var created = result.Result.Should().BeOfType<CreatedAtActionResult>().Subject;
        created.StatusCode.Should().Be(201);
        var response = created.Value.Should().BeOfType<MachineResponse>().Subject;
        response.Name.Should().Be("Server1");
        response.Hostname.Should().Be("server1.local");
        response.Tags.Should().Be("web,prod");

        // Verify persisted
        var saved = await db.ManagedMachines.FindAsync(response.Id);
        saved.Should().NotBeNull();
    }

    [Theory]
    [InlineData("", "server.local", 5985)]
    [InlineData("Server", "", 5985)]
    [InlineData("Server", "http://server.local", 5985)]
    [InlineData("Server", "server.local", 0)]
    [InlineData("Server", "server.local", 65536)]
    public async Task Create_InvalidRequest_Returns400(string name, string hostname, int port)
    {
        var db = CreateContext();
        var controller = new MachinesController(
            db,
            new Mock<IRemoteSessionFactory>().Object,
            new Mock<ICredentialStore>().Object,
            NoopAuditWriter.Instance);

        var result = await controller.Create(
            new CreateMachineRequest(name, hostname, port, false, null, null),
            CancellationToken.None);

        var badRequest = result.Result.Should().BeOfType<BadRequestObjectResult>().Subject;
        AssertProblemDetails(badRequest);
        (await db.ManagedMachines.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Create_UnknownDefaultCredential_Returns400()
    {
        var db = CreateContext();
        var controller = new MachinesController(
            db,
            new Mock<IRemoteSessionFactory>().Object,
            new Mock<ICredentialStore>().Object,
            NoopAuditWriter.Instance);

        var result = await controller.Create(
            new CreateMachineRequest("Server", "server.local", 5985, false, Guid.NewGuid(), null),
            CancellationToken.None);

        var badRequest = result.Result.Should().BeOfType<BadRequestObjectResult>().Subject;
        AssertProblemDetails(badRequest, "MACHINE_DEFAULT_CREDENTIAL_UNKNOWN");
    }

    [Fact]
    public async Task Update_Exists_Returns204()
    {
        // Arrange
        var db = CreateContext();
        var machine = new ManagedMachine
        {
            Id = Guid.NewGuid(),
            Name = "Original",
            Hostname = "original.local"
        };
        db.ManagedMachines.Add(machine);
        await db.SaveChangesAsync();

        var mockSessionFactory = new Mock<IRemoteSessionFactory>();
        var mockCredentialStore = new Mock<ICredentialStore>();
        var controller = new MachinesController(db, mockSessionFactory.Object, mockCredentialStore.Object, NoopAuditWriter.Instance);
        var request = new UpdateMachineRequest("Updated", "updated.local", 5986, true, null, "updated");

        // Act
        var result = await controller.Update(machine.Id, request, CancellationToken.None);

        // Assert
        result.Should().BeOfType<NoContentResult>();
        var updated = await db.ManagedMachines.FindAsync(machine.Id);
        updated!.Name.Should().Be("Updated");
        updated.Hostname.Should().Be("updated.local");
        updated.WinRmPort.Should().Be(5986);
        updated.UseSsl.Should().BeTrue();
    }

    [Fact]
    public async Task Delete_Exists_Returns204()
    {
        // Arrange
        var db = CreateContext();
        var machine = new ManagedMachine
        {
            Id = Guid.NewGuid(),
            Name = "ToDelete",
            Hostname = "delete.local"
        };
        db.ManagedMachines.Add(machine);
        await db.SaveChangesAsync();

        var mockSessionFactory = new Mock<IRemoteSessionFactory>();
        var mockCredentialStore = new Mock<ICredentialStore>();
        var controller = new MachinesController(db, mockSessionFactory.Object, mockCredentialStore.Object, NoopAuditWriter.Instance);

        // Act
        var result = await controller.Delete(machine.Id, CancellationToken.None);

        // Assert
        result.Should().BeOfType<NoContentResult>();
        var deleted = await db.ManagedMachines.FindAsync(machine.Id);
        deleted.Should().BeNull();
    }

    [Fact]
    public async Task TestConnection_NoCredential_Returns400()
    {
        // Arrange
        var db = CreateContext();
        var machine = new ManagedMachine
        {
            Id = Guid.NewGuid(),
            Name = "NoCred",
            Hostname = "nocred.local",
            DefaultCredentialId = null
        };
        db.ManagedMachines.Add(machine);
        await db.SaveChangesAsync();

        var mockSessionFactory = new Mock<IRemoteSessionFactory>();
        var mockCredentialStore = new Mock<ICredentialStore>();
        var controller = new MachinesController(db, mockSessionFactory.Object, mockCredentialStore.Object, NoopAuditWriter.Instance);

        // Act
        var result = await controller.TestConnection(machine.Id, null, CancellationToken.None);

        // Assert
        var badRequest = result.Result.Should().BeOfType<BadRequestObjectResult>().Subject;
        AssertProblemDetails(badRequest, "MACHINE_CREDENTIAL_REQUIRED");
    }

    [Fact]
    public async Task TestConnection_Success_EmitsAuditWithSuccessAction()
    {
        var db = CreateContext();
        var credential = new Credential
        {
            Id = Guid.NewGuid(),
            Name = "TestCred",
            Username = "admin",
            EncryptedPassword = new byte[] { 1, 2, 3 }
        };
        db.Credentials.Add(credential);
        var machine = new ManagedMachine
        {
            Id = Guid.NewGuid(),
            Name = "Probe",
            Hostname = "probe.local",
            DefaultCredentialId = credential.Id,
        };
        db.ManagedMachines.Add(machine);
        await db.SaveChangesAsync();

        var credStore = new Mock<ICredentialStore>();
        credStore.Setup(c => c.GetAsync(credential.Id, It.IsAny<CancellationToken>())).ReturnsAsync(credential);

        var session = new Mock<IRemoteSession>();
        session.Setup(s => s.ExecuteScriptAsync("$env:COMPUTERNAME", 15, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RemoteExecutionResult { Success = true, Output = "PROBE", ErrorOutput = "" });
        session.Setup(s => s.DisposeAsync()).Returns(ValueTask.CompletedTask);

        var factory = new Mock<IRemoteSessionFactory>();
        factory.Setup(f => f.CreateSessionAsync(It.IsAny<ManagedMachine>(), credential, It.IsAny<CancellationToken>()))
            .ReturnsAsync(session.Object);

        var audit = new CapturingAuditWriter();
        var controller = new MachinesController(db, factory.Object, credStore.Object, audit);

        await controller.TestConnection(machine.Id, null, CancellationToken.None);

        var call = audit.Calls.Should().ContainSingle().Subject;
        call.Action.Should().Be("MACHINE_CONNECTION_TESTED");
        call.ResourceType.Should().Be("Machine");
        call.ResourceId.Should().Be(machine.Id);
        call.Details.Should().Contain("Probe").And.Contain("\"success\":\"True\"");
    }

    [Fact]
    public async Task TestConnection_RemoteFails_EmitsTestFailedAudit()
    {
        var db = CreateContext();
        var credential = new Credential
        {
            Id = Guid.NewGuid(), Name = "TC", Username = "x",
            EncryptedPassword = new byte[] { 1 }
        };
        db.Credentials.Add(credential);
        var machine = new ManagedMachine
        {
            Id = Guid.NewGuid(), Name = "Sad", Hostname = "sad.local",
            DefaultCredentialId = credential.Id,
        };
        db.ManagedMachines.Add(machine);
        await db.SaveChangesAsync();

        var credStore = new Mock<ICredentialStore>();
        credStore.Setup(c => c.GetAsync(credential.Id, It.IsAny<CancellationToken>())).ReturnsAsync(credential);

        var session = new Mock<IRemoteSession>();
        session.Setup(s => s.ExecuteScriptAsync("$env:COMPUTERNAME", 15, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RemoteExecutionResult { Success = false, Output = "", ErrorOutput = "WinRM 500" });
        session.Setup(s => s.DisposeAsync()).Returns(ValueTask.CompletedTask);

        var factory = new Mock<IRemoteSessionFactory>();
        factory.Setup(f => f.CreateSessionAsync(It.IsAny<ManagedMachine>(), credential, It.IsAny<CancellationToken>()))
            .ReturnsAsync(session.Object);

        var audit = new CapturingAuditWriter();
        var controller = new MachinesController(db, factory.Object, credStore.Object, audit);

        await controller.TestConnection(machine.Id, null, CancellationToken.None);

        // Suffix `_FAILED` means the AuditWriter maps event.outcome=failure for SIEM —
        // critical for "all failed connection probes from IP X in the last hour" alerts.
        var call = audit.Calls.Should().ContainSingle().Subject;
        call.Action.Should().Be("MACHINE_CONNECTION_TEST_FAILED");
    }

    [Fact]
    public async Task TestConnection_SessionFactoryThrows_EmitsTestFailedAudit()
    {
        var db = CreateContext();
        var credential = new Credential
        {
            Id = Guid.NewGuid(), Name = "TC", Username = "x",
            EncryptedPassword = new byte[] { 1 }
        };
        db.Credentials.Add(credential);
        var machine = new ManagedMachine
        {
            Id = Guid.NewGuid(), Name = "Broken", Hostname = "broken.local",
            DefaultCredentialId = credential.Id,
        };
        db.ManagedMachines.Add(machine);
        await db.SaveChangesAsync();

        var credStore = new Mock<ICredentialStore>();
        credStore.Setup(c => c.GetAsync(credential.Id, It.IsAny<CancellationToken>())).ReturnsAsync(credential);

        var factory = new Mock<IRemoteSessionFactory>();
        factory.Setup(f => f.CreateSessionAsync(It.IsAny<ManagedMachine>(), credential, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("DNS unreachable"));

        var audit = new CapturingAuditWriter();
        var controller = new MachinesController(db, factory.Object, credStore.Object, audit);

        await controller.TestConnection(machine.Id, null, CancellationToken.None);

        var call = audit.Calls.Should().ContainSingle().Subject;
        call.Action.Should().Be("MACHINE_CONNECTION_TEST_FAILED");
        call.Details.Should().Contain("DNS unreachable");
    }

    [Fact]
    public async Task GetAll_PopulatesUsedByWorkflowCount_DistinctPerWorkflow()
    {
        // Arrange — two machines, three workflows.
        //   - WF-A targets machineX twice (two nodes) + machineY once  → X counts once, Y counts once
        //   - WF-B targets machineX only                                → X counts once more
        //   - WF-C has no machine targets                               → no contribution
        // Expected: X usedBy=2, Y usedBy=1.
        var db = CreateContext();
        var machineX = new ManagedMachine { Id = Guid.NewGuid(), Name = "X", Hostname = "x.local" };
        var machineY = new ManagedMachine { Id = Guid.NewGuid(), Name = "Y", Hostname = "y.local" };
        db.ManagedMachines.AddRange(machineX, machineY);

        // Inlined JSON via concat — raw-string interpolation gets tangled by the
        // nested JSON braces here. Concatenation reads more clearly anyway.
        static string MakeWfDef(params Guid[] targets)
        {
            var nodes = string.Join(",", targets.Select((g, i) =>
                $"{{\"id\":\"n{i}\",\"type\":\"activity\",\"data\":{{\"activityType\":\"runScript\",\"targetMachineId\":\"{g}\"}}}}"));
            return $"{{\"nodes\":[{nodes}],\"edges\":[]}}";
        }
        db.Workflows.AddRange(
            new Workflow { Id = Guid.NewGuid(), Name = "WF-A", DefinitionJson = MakeWfDef(machineX.Id, machineX.Id, machineY.Id) },
            new Workflow { Id = Guid.NewGuid(), Name = "WF-B", DefinitionJson = MakeWfDef(machineX.Id) },
            new Workflow { Id = Guid.NewGuid(), Name = "WF-C", DefinitionJson = "{\"nodes\":[],\"edges\":[]}" });
        await db.SaveChangesAsync();

        var controller = new MachinesController(
            db, new Mock<IRemoteSessionFactory>().Object,
            new Mock<ICredentialStore>().Object, NoopAuditWriter.Instance);

        // Act
        var result = await controller.GetAll(CancellationToken.None);

        // Assert
        var machines = (result.Result as OkObjectResult)!.Value as List<MachineResponse>;
        var x = machines!.Single(m => m.Id == machineX.Id);
        var y = machines!.Single(m => m.Id == machineY.Id);
        x.UsedByWorkflowCount.Should().Be(2, "WF-A references X twice but should count as 1 workflow, plus WF-B = 2");
        y.UsedByWorkflowCount.Should().Be(1);
    }

    [Fact]
    public async Task GetAll_PopulatesRecentStepStats_AndActiveRuns()
    {
        var db = CreateContext();
        var machine = new ManagedMachine { Id = Guid.NewGuid(), Name = "M", Hostname = "m.local" };
        db.ManagedMachines.Add(machine);

        // One workflow execution to hang step rows off — the controller queries
        // StepExecutions directly so a single parent run is enough.
        var workflow = new Workflow { Id = Guid.NewGuid(), Name = "wf", DefinitionJson = "{\"nodes\":[],\"edges\":[]}" };
        db.Workflows.Add(workflow);
        var exec = new WorkflowExecution
        {
            Id = Guid.NewGuid(), WorkflowId = workflow.Id,
            Status = ExecutionStatus.Running, StartedAt = DateTime.UtcNow,
        };
        db.WorkflowExecutions.Add(exec);

        var machineId = machine.Id.ToString();
        var now = DateTime.UtcNow;
        db.StepExecutions.AddRange(
            // Within window: 4 steps targeting our machine — 1 failed, 1 currently running.
            new StepExecution { Id = Guid.NewGuid(), WorkflowExecutionId = exec.Id, StepId = "s1", StepType = "runScript", TargetMachine = machineId, Status = ExecutionStatus.Succeeded, StartedAt = now.AddDays(-1) },
            new StepExecution { Id = Guid.NewGuid(), WorkflowExecutionId = exec.Id, StepId = "s2", StepType = "runScript", TargetMachine = machineId, Status = ExecutionStatus.Succeeded, StartedAt = now.AddDays(-2) },
            new StepExecution { Id = Guid.NewGuid(), WorkflowExecutionId = exec.Id, StepId = "s3", StepType = "runScript", TargetMachine = machineId, Status = ExecutionStatus.Failed,    StartedAt = now.AddDays(-3) },
            new StepExecution { Id = Guid.NewGuid(), WorkflowExecutionId = exec.Id, StepId = "s4", StepType = "runScript", TargetMachine = machineId, Status = ExecutionStatus.Running,   StartedAt = now.AddMinutes(-2) },
            // Outside the 7-day window — must not be counted.
            new StepExecution { Id = Guid.NewGuid(), WorkflowExecutionId = exec.Id, StepId = "s5", StepType = "runScript", TargetMachine = machineId, Status = ExecutionStatus.Succeeded, StartedAt = now.AddDays(-30) },
            // Different (unknown) target id — must not be attributed to our machine.
            new StepExecution { Id = Guid.NewGuid(), WorkflowExecutionId = exec.Id, StepId = "s6", StepType = "runScript", TargetMachine = Guid.NewGuid().ToString(), Status = ExecutionStatus.Succeeded, StartedAt = now.AddDays(-1) },
            // Null target — engine-local activities, must not affect any machine.
            new StepExecution { Id = Guid.NewGuid(), WorkflowExecutionId = exec.Id, StepId = "s7", StepType = "log", TargetMachine = null, Status = ExecutionStatus.Succeeded, StartedAt = now.AddDays(-1) });
        await db.SaveChangesAsync();

        var controller = new MachinesController(
            db, new Mock<IRemoteSessionFactory>().Object,
            new Mock<ICredentialStore>().Object, NoopAuditWriter.Instance);

        var result = await controller.GetAll(CancellationToken.None);
        var m = ((result.Result as OkObjectResult)!.Value as List<MachineResponse>)!.Single();

        m.RecentStepCount.Should().Be(4, "only the 4 in-window steps should be counted (the 30-day-old + foreign + null-target rows are excluded)");
        m.RecentFailedStepCount.Should().Be(1);
        m.ActiveRunCount.Should().Be(1, "only the one Running step targeting our machine should appear here");
    }

    [Fact]
    public async Task GetAll_MalformedWorkflowJson_IsSkippedNotThrown()
    {
        var db = CreateContext();
        var machine = new ManagedMachine { Id = Guid.NewGuid(), Name = "M", Hostname = "m.local" };
        db.ManagedMachines.Add(machine);
        // Garbage JSON must NOT take the whole endpoint down — log-and-skip semantics.
        db.Workflows.Add(new Workflow { Id = Guid.NewGuid(), Name = "broken", DefinitionJson = "{not-json" });
        await db.SaveChangesAsync();

        var controller = new MachinesController(
            db, new Mock<IRemoteSessionFactory>().Object,
            new Mock<ICredentialStore>().Object, NoopAuditWriter.Instance);

        var act = async () => await controller.GetAll(CancellationToken.None);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task TestConnection_Success_UpdatesReachability()
    {
        // Arrange
        var db = CreateContext();
        var credential = new Credential
        {
            Id = Guid.NewGuid(),
            Name = "TestCred",
            Username = "admin",
            EncryptedPassword = new byte[] { 1, 2, 3 }
        };
        db.Credentials.Add(credential);

        var machine = new ManagedMachine
        {
            Id = Guid.NewGuid(),
            Name = "TestMachine",
            Hostname = "test.local",
            DefaultCredentialId = credential.Id,
            IsReachable = false
        };
        db.ManagedMachines.Add(machine);
        await db.SaveChangesAsync();

        var mockCredentialStore = new Mock<ICredentialStore>();
        mockCredentialStore.Setup(c => c.GetAsync(credential.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(credential);

        var mockSession = new Mock<IRemoteSession>();
        mockSession.Setup(s => s.ExecuteScriptAsync("$env:COMPUTERNAME", 15, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RemoteExecutionResult
            {
                Success = true,
                Output = "TESTMACHINE",
                ErrorOutput = ""
            });
        mockSession.Setup(s => s.DisposeAsync()).Returns(ValueTask.CompletedTask);

        var mockSessionFactory = new Mock<IRemoteSessionFactory>();
        mockSessionFactory.Setup(f => f.CreateSessionAsync(It.IsAny<ManagedMachine>(), credential, It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockSession.Object);

        var controller = new MachinesController(db, mockSessionFactory.Object, mockCredentialStore.Object, NoopAuditWriter.Instance);

        // Act
        var result = await controller.TestConnection(machine.Id, null, CancellationToken.None);

        // Assert
        result.Result.Should().BeOfType<OkObjectResult>();
        var updated = await db.ManagedMachines.FindAsync(machine.Id);
        updated!.IsReachable.Should().BeTrue();
        updated.LastConnectivityCheck.Should().NotBeNull();
    }

    [Fact]
    public async Task TestConnection_SessionThrows_DoesNotLeakExceptionMessageToCaller()
    {
        // M-6 (security audit 2026-05-15): a WinRM/PSRemoting failure must not echo the raw
        // exception text back over the HTTP response — it can leak internal hostnames, SPN /
        // Kerberos realm details and provider internals. The detail stays in the audit trail
        // (admin-only) and server log; the caller gets a generic message + a correlation id.
        const string secretLeak = "Kerberos realm CORP.INTERNAL ticket for dc01.corp.internal failed";

        var db = CreateContext();
        var credential = new Credential
        {
            Id = Guid.NewGuid(), Name = "TestCred", Username = "admin",
            EncryptedPassword = new byte[] { 1, 2, 3 },
        };
        db.Credentials.Add(credential);
        var machine = new ManagedMachine
        {
            Id = Guid.NewGuid(), Name = "TestMachine", Hostname = "test.local",
            DefaultCredentialId = credential.Id, IsReachable = true,
        };
        db.ManagedMachines.Add(machine);
        await db.SaveChangesAsync();

        var credStore = new Mock<ICredentialStore>();
        credStore.Setup(c => c.GetAsync(credential.Id, It.IsAny<CancellationToken>())).ReturnsAsync(credential);

        var factory = new Mock<IRemoteSessionFactory>();
        factory.Setup(f => f.CreateSessionAsync(It.IsAny<ManagedMachine>(), credential, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException(secretLeak));

        var audit = new CapturingAuditWriter();
        var controller = new MachinesController(db, factory.Object, credStore.Object, audit);

        var result = await controller.TestConnection(machine.Id, null, CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var payload = System.Text.Json.JsonSerializer.Serialize(ok.Value);
        payload.Should().NotContain(secretLeak, "the raw exception message must never reach the HTTP response");
        payload.Should().NotContain("CORP.INTERNAL");
        payload.Should().Contain("correlationId", "the caller gets a correlation id to look up the detail");
        // The detail is preserved server-side for admins via the audit trail.
        audit.Calls.Should().ContainSingle(c => c.Action == "MACHINE_CONNECTION_TEST_FAILED")
            .Which.Details.Should().Contain(secretLeak);
    }

    private static ProblemDetails AssertProblemDetails(BadRequestObjectResult result, string? code = null)
    {
        var problem = result.Value.Should().BeOfType<ProblemDetails>().Subject;
        problem.Status.Should().Be(400);
        problem.Detail.Should().NotBeNullOrWhiteSpace();
        problem.Extensions.Should().ContainKey("code");
        if (code is not null)
            problem.Extensions["code"].Should().Be(code);
        return problem;
    }
}

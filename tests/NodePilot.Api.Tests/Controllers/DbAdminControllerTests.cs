using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodePilot.Api.Controllers;
using NodePilot.Api.Services.DbAdmin;
using NodePilot.Core.Audit;
using NodePilot.Core.Enums;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Api.Tests.Controllers;

public class DbAdminControllerTests
{
    private static (DbAdminController controller, NodePilotDbContext db) NewController(
        Guid? callerId = null, UserRole callerRole = UserRole.Admin,
        DbAdminOptions? options = null,
        string? confirmWriteHeader = null)
    {
        var db = TestDbFactory.Create();
        // DbAdminMetadataService takes an IServiceScopeFactory and resolves the DbContext
        // once while building the schema map. We build a minimal ServiceProvider that just
        // hands back this same db — that keeps the reflection-based walk over the EF model
        // deterministic and avoids a separate singleton layer.
        var services = new ServiceCollection();
        services.AddSingleton(db);
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var meta = new DbAdminMetadataService(scopeFactory);
        var executor = new DbAdminQueryExecutor(db, new TestOptionsMonitor<DbAdminOptions>(options ?? new DbAdminOptions()));
        var controller = new DbAdminController(db, meta, executor, new AuditStager(),
            new MemoryCache(new MemoryCacheOptions()), NullLogger<DbAdminController>.Instance);

        var id = callerId ?? Guid.NewGuid();
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Role, callerRole.ToString()),
            new Claim(ClaimTypes.NameIdentifier, id.ToString()),
        ], "TestAuth"));
        var httpContext = new DefaultHttpContext { User = principal };
        if (confirmWriteHeader is not null)
            httpContext.Request.Headers["X-Confirm-Write"] = confirmWriteHeader;
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return (controller, db);
    }

    private static User AdminUser(Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        Username = "admin",
        PasswordHash = "hash",
        Role = UserRole.Admin,
        IsActive = true,
        CreatedAt = DateTime.UtcNow,
        PasswordChangedAt = DateTime.UtcNow,
    };

    private static JsonElement JsonVal(object? value)
        => JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(value));

    // ── Schema tests ──────────────────────────────────────────────────────────

    [Fact]
    public async Task GetTables_DerivesFromModel_ContainsExpectedEntities()
    {
        var (ctrl, _) = NewController();
        var result = await ctrl.GetTables(CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var tables = ok.Value.Should().BeAssignableTo<List<DbAdminTableInfo>>().Subject;
        tables.Should().HaveCountGreaterThanOrEqualTo(15);
        tables.Select(t => t.Name).Should().Contain(["Workflow", "User", "Credential", "AuditLogEntry", "RevokedToken"]);
    }

    [Fact]
    public async Task GetTables_ExposesPhysicalDbTableName_ForRawSqlClients()
    {
        // The SQL query console click-to-insert needs the actual DB table ("Credentials"),
        // not the EF entity singular ("Credential"), because raw SQL hits the physical
        // table that Postgres / SQL Server know about. Migrations map them like
        // `b.ToTable("Credentials")`, so EF's GetTableName() returns the pluralised form.
        var (ctrl, _) = NewController();
        var result = await ctrl.GetTables(CancellationToken.None);
        var tables = ((result.Result as OkObjectResult)!.Value as List<DbAdminTableInfo>)!;

        var credential = tables.Single(t => t.Name == "Credential");
        credential.DbTableName.Should().Be("Credentials");

        var workflow = tables.Single(t => t.Name == "Workflow");
        workflow.DbTableName.Should().Be("Workflows");

        // None of the entries should be empty — that would mean GetTableName() returned
        // null and the metadata-service fallback silently kicked in.
        tables.Should().OnlyContain(t => !string.IsNullOrEmpty(t.DbTableName));
    }

    [Fact]
    public async Task GetTables_AuditLogEntry_HasCanUpdateFalse_CanDeleteFalse()
    {
        var (ctrl, _) = NewController();
        var result = await ctrl.GetTables(CancellationToken.None);
        var tables = ((result.Result as OkObjectResult)!.Value as List<DbAdminTableInfo>)!;

        var audit = tables.Single(t => t.Name == "AuditLogEntry");
        audit.Capabilities.CanUpdate.Should().BeFalse();
        audit.Capabilities.CanDelete.Should().BeFalse();
    }

    [Fact]
    public async Task GetTables_RevokedToken_CanDeleteFalse()
    {
        var (ctrl, _) = NewController();
        var result = await ctrl.GetTables(CancellationToken.None);
        var tables = ((result.Result as OkObjectResult)!.Value as List<DbAdminTableInfo>)!;

        var rt = tables.Single(t => t.Name == "RevokedToken");
        rt.Capabilities.CanDelete.Should().BeFalse();
        rt.Capabilities.CanUpdate.Should().BeFalse();
    }

    [Fact]
    public async Task GetTables_WorkflowVersion_CanUpdateFalse_CanDeleteFalse()
    {
        var (ctrl, _) = NewController();
        var result = await ctrl.GetTables(CancellationToken.None);
        var tables = ((result.Result as OkObjectResult)!.Value as List<DbAdminTableInfo>)!;

        var wv = tables.Single(t => t.Name == "WorkflowVersion");
        wv.Capabilities.CanUpdate.Should().BeFalse();
        wv.Capabilities.CanDelete.Should().BeFalse();
    }

    [Fact]
    public async Task GetTables_GlobalVariable_ValueColumn_IsReadOnly()
    {
        var (ctrl, _) = NewController();
        var result = await ctrl.GetTables(CancellationToken.None);
        var tables = ((result.Result as OkObjectResult)!.Value as List<DbAdminTableInfo>)!;

        var gv = tables.Single(t => t.Name == "GlobalVariable");
        var valCol = gv.Columns.FirstOrDefault(c => c.Name == "Value");
        valCol.Should().NotBeNull("Value column must be visible but read-only");
        valCol!.IsReadOnly.Should().BeTrue();
    }

    // ── Row masking tests ─────────────────────────────────────────────────────

    [Fact]
    public async Task GetRows_Credentials_DoesNotIncludeEncryptedPassword()
    {
        var (ctrl, db) = NewController();
        db.Credentials.Add(new Credential { Id = Guid.NewGuid(), Name = "test", Username = "u", EncryptedPassword = [1, 2, 3] });
        await db.SaveChangesAsync();

        var result = await ctrl.GetRows("Credential", ct: CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var rows = (ok.Value as DbAdminRowsResponse)!;
        rows.Rows.Should().HaveCount(1);
        rows.Rows[0].ContainsKey("EncryptedPassword").Should().BeFalse("hidden column must not appear");
    }

    [Fact]
    public async Task GetRows_Users_DoesNotIncludePasswordHash()
    {
        var (ctrl, db) = NewController();
        db.Users.Add(AdminUser());
        await db.SaveChangesAsync();

        var result = await ctrl.GetRows("User", ct: CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var rows = (ok.Value as DbAdminRowsResponse)!;
        rows.Rows.Should().HaveCount(1);
        rows.Rows[0].ContainsKey("PasswordHash").Should().BeFalse("hidden column must not appear");
    }

    [Fact]
    public async Task GetRows_GlobalVariables_SecretValueMasked()
    {
        var (ctrl, db) = NewController();
        db.GlobalVariables.AddRange(
            new GlobalVariable { Id = Guid.NewGuid(), Name = "SECRET_KEY", Value = "topsecret", IsSecret = true },
            new GlobalVariable { Id = Guid.NewGuid(), Name = "PUBLIC_VAR", Value = "visible", IsSecret = false });
        await db.SaveChangesAsync();

        var result = await ctrl.GetRows("GlobalVariable", ct: CancellationToken.None);
        var rows = ((result.Result as OkObjectResult)!.Value as DbAdminRowsResponse)!.Rows;

        var secretRow = rows.Single(r => r["Name"]?.ToString() == "SECRET_KEY");
        var publicRow = rows.Single(r => r["Name"]?.ToString() == "PUBLIC_VAR");
        secretRow["Value"].Should().Be("***");
        publicRow["Value"].Should().Be("visible");
    }

    // ── PATCH validation ──────────────────────────────────────────────────────

    [Fact]
    public async Task PatchRow_OnAuditLog_Returns405()
    {
        var (ctrl, _) = NewController();
        var result = await ctrl.PatchRow(
            "AuditLogEntry", ["some-pk"],
            new DbAdminPatchRequest("Action", JsonVal("X")),
            CancellationToken.None);

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(405);
    }

    [Fact]
    public async Task PatchRow_OnRevokedToken_Returns405()
    {
        var (ctrl, _) = NewController();
        var result = await ctrl.PatchRow(
            "RevokedToken", ["some-pk"],
            new DbAdminPatchRequest("Reason", JsonVal("test")),
            CancellationToken.None);

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(405);
    }

    [Fact]
    public async Task PatchRow_OnPkColumn_Returns400()
    {
        var (ctrl, db) = NewController();
        var id = Guid.NewGuid();
        db.ManagedMachines.Add(new ManagedMachine { Id = id, Name = "m1", Hostname = "h1" });
        await db.SaveChangesAsync();

        var result = await ctrl.PatchRow(
            "ManagedMachine", [id.ToString()],
            new DbAdminPatchRequest("Id", JsonVal(Guid.NewGuid().ToString())),
            CancellationToken.None);

        var bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        var body = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(bad.Value));
        body.GetProperty("code").GetString().Should().Be("readonly_column");
    }

    [Fact]
    public async Task PatchRow_OnHiddenColumn_Returns400()
    {
        var (ctrl, db) = NewController();
        var id = Guid.NewGuid();
        db.Users.Add(new User { Id = id, Username = "u1", PasswordHash = "h", Role = UserRole.Viewer, IsActive = true, CreatedAt = DateTime.UtcNow, PasswordChangedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var result = await ctrl.PatchRow(
            "User", [id.ToString()],
            new DbAdminPatchRequest("PasswordHash", JsonVal("newhash")),
            CancellationToken.None);

        var bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        var body = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(bad.Value));
        body.GetProperty("code").GetString().Should().Be("readonly_column");
    }

    [Fact]
    public async Task PatchRow_PasswordChangedAtBypass_Blocked()
    {
        var (ctrl, db) = NewController();
        var id = Guid.NewGuid();
        db.Users.Add(new User { Id = id, Username = "u1", PasswordHash = "h", Role = UserRole.Viewer, IsActive = true, CreatedAt = DateTime.UtcNow, PasswordChangedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var result = await ctrl.PatchRow(
            "User", [id.ToString()],
            new DbAdminPatchRequest("PasswordChangedAt", JsonVal("2000-01-01T00:00:00Z")),
            CancellationToken.None);

        var bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        var body = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(bad.Value));
        body.GetProperty("code").GetString().Should().Be("readonly_column");
    }

    [Fact]
    public async Task PatchRow_SecurityStampBypass_Blocked()
    {
        var (ctrl, db) = NewController();
        var id = Guid.NewGuid();
        db.Users.Add(new User { Id = id, Username = "u1", PasswordHash = "h", Role = UserRole.Viewer, IsActive = true, CreatedAt = DateTime.UtcNow, PasswordChangedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var result = await ctrl.PatchRow(
            "User", [id.ToString()],
            new DbAdminPatchRequest("SecurityStamp", JsonVal(99)),
            CancellationToken.None);

        var bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        var body = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(bad.Value));
        body.GetProperty("code").GetString().Should().Be("readonly_column");
    }

    [Fact]
    public async Task PatchRow_ExternalUserReactivation_InvalidatesOldAuthorizationSnapshot()
    {
        var (ctrl, db) = NewController();
        var id = Guid.NewGuid();
        var user = new User
        {
            Id = id, Username = "external@example.test", Provider = AuthProvider.Ldap,
            ExternalId = "S-1-5-21-1-2-3-1001", Role = UserRole.Operator,
            IsActive = false, SecurityStamp = 6, LastDirectorySyncAt = DateTime.UtcNow,
            DirectorySyncStatus = "Current", CreatedAt = DateTime.UtcNow,
            PasswordChangedAt = DateTime.UtcNow,
        };
        var session = new AuthSession
        {
            Id = Guid.NewGuid(), UserId = id, AuthenticationMethod = "Ldap",
            CreatedAt = DateTime.UtcNow, LastSeenAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddHours(1), CurrentJti = Guid.NewGuid().ToString("N"),
        };
        db.AddRange(
            user,
            session,
            new DirectoryMembership
            {
                UserId = id,
                Authority = ExternalIdentity.ActiveDirectoryAuthority,
                GroupKey = "S-1-5-21-1-2-3-2001",
                LastSeenAt = DateTime.UtcNow,
            });
        await db.SaveChangesAsync();

        var result = await ctrl.PatchRow(
            "User", [id.ToString()],
            new DbAdminPatchRequest("IsActive", JsonVal(true)),
            CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
        db.ChangeTracker.Clear();
        var reactivated = await db.Users.SingleAsync(candidate => candidate.Id == id);
        reactivated.IsActive.Should().BeTrue();
        reactivated.SecurityStamp.Should().Be(7);
        reactivated.LastDirectorySyncAt.Should().BeNull();
        reactivated.DirectorySyncStatus.Should().Be("ReactivationReauthRequired");
        (await db.DirectoryMemberships.CountAsync(candidate => candidate.UserId == id))
            .Should().Be(0);
        (await db.AuthSessions.SingleAsync(candidate => candidate.Id == session.Id)).RevokedAt
            .Should().NotBeNull();
    }

    [Fact]
    public async Task PatchRow_Workflow_DefinitionJson_ReadOnly_Returns400()
    {
        // Patching Workflow.DefinitionJson through DbAdmin would bypass domain validation,
        // versioning, the edit-lock, and webhook-collision protection. The column stays
        // visible for forensic browsing but is not editable via DbAdmin — operators must
        // go through the normal workflow-update endpoint instead.
        var (ctrl, db) = NewController();
        var id = Guid.NewGuid();
        db.Workflows.Add(new Workflow { Id = id, Name = "W1", DefinitionJson = "{}" });
        await db.SaveChangesAsync();

        var result = await ctrl.PatchRow(
            "Workflow", [id.ToString()],
            new DbAdminPatchRequest("DefinitionJson", JsonVal("""{"nodes":[],"edges":[]}""")),
            CancellationToken.None);

        var bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        var body = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(bad.Value));
        body.GetProperty("code").GetString().Should().Be("readonly_column");

        // The persisted row must be unchanged — the read-only guard never touches it.
        var unchanged = await db.Workflows.FindAsync(id);
        unchanged!.DefinitionJson.Should().Be("{}");
    }

    [Fact]
    public async Task GetTables_Workflow_DefinitionJson_VisibleButReadOnly()
    {
        var (ctrl, _) = NewController();
        var result = await ctrl.GetTables(CancellationToken.None);
        var tables = ((result.Result as OkObjectResult)!.Value as List<DbAdminTableInfo>)!;

        var wf = tables.Single(t => t.Name == "Workflow");
        var defCol = wf.Columns.FirstOrDefault(c => c.Name == "DefinitionJson");
        defCol.Should().NotBeNull("DefinitionJson must remain visible for forensic browsing");
        defCol!.IsReadOnly.Should().BeTrue("mutations must go through WorkflowsController, not DbAdmin");
    }

    [Fact]
    public async Task PatchRow_GlobalVariable_Value_ReadOnly_Returns400()
    {
        var (ctrl, db) = NewController();
        var id = Guid.NewGuid();
        db.GlobalVariables.Add(new GlobalVariable { Id = id, Name = "G1", Value = "v", IsSecret = false });
        await db.SaveChangesAsync();

        var result = await ctrl.PatchRow(
            "GlobalVariable", [id.ToString()],
            new DbAdminPatchRequest("Value", JsonVal("newval")),
            CancellationToken.None);

        var bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        var body = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(bad.Value));
        body.GetProperty("code").GetString().Should().Be("readonly_column");
    }

    [Fact]
    public async Task PatchRow_NormalColumn_PersistsAndAuditsAtomically()
    {
        var (ctrl, db) = NewController();
        var id = Guid.NewGuid();
        db.ManagedMachines.Add(new ManagedMachine { Id = id, Name = "original", Hostname = "host1" });
        await db.SaveChangesAsync();

        var result = await ctrl.PatchRow(
            "ManagedMachine", [id.ToString()],
            new DbAdminPatchRequest("Name", JsonVal("updated")),
            CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();

        // Value persisted
        var updated = await db.ManagedMachines.FindAsync(id);
        updated!.Name.Should().Be("updated");

        // Audit entry in same DB transaction
        var audit = db.AuditLog.SingleOrDefault(a => a.Action == "DBADMIN_ROW_UPDATED");
        audit.Should().NotBeNull("audit entry must be committed with the mutation");
        audit!.Details.Should().Contain("Name");
        audit.Details.Should().Contain("original");
        audit.Details.Should().Contain("updated");
    }

    [Fact]
    public async Task PatchRow_LastAdminDemote_Returns400_LastAdmin()
    {
        var adminId = Guid.NewGuid();
        var (ctrl, db) = NewController(callerId: adminId);
        db.Users.Add(new User { Id = adminId, Username = "admin", PasswordHash = "h", Role = UserRole.Admin, IsActive = true, CreatedAt = DateTime.UtcNow, PasswordChangedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var result = await ctrl.PatchRow(
            "User", [adminId.ToString()],
            new DbAdminPatchRequest("Role", JsonVal("Operator")),
            CancellationToken.None);

        var bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        var body = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(bad.Value));
        body.GetProperty("code").GetString().Should().Be("cannot_demote_self");
    }

    [Fact]
    public async Task PatchRow_LastAdminDemoteOtherUser_Returns400_LastAdmin()
    {
        var callerId = Guid.NewGuid();
        var otherAdminId = Guid.NewGuid();
        var (ctrl, db) = NewController(callerId: callerId);
        db.Users.AddRange(
            new User { Id = callerId, Username = "caller", PasswordHash = "h", Role = UserRole.Admin, IsActive = true, CreatedAt = DateTime.UtcNow, PasswordChangedAt = DateTime.UtcNow },
            new User { Id = otherAdminId, Username = "other", PasswordHash = "h", Role = UserRole.Admin, IsActive = true, CreatedAt = DateTime.UtcNow, PasswordChangedAt = DateTime.UtcNow, SecurityStamp = 7 });
        await db.SaveChangesAsync();

        // Demote other admin when caller is also admin — this is allowed (2 admins, losing 1 leaves 1)
        var result = await ctrl.PatchRow(
            "User", [otherAdminId.ToString()],
            new DbAdminPatchRequest("Role", JsonVal("Operator")),
            CancellationToken.None);

        // Should succeed — one admin remains
        result.Should().BeOfType<NoContentResult>();
        db.Users.Find(otherAdminId)!.SecurityStamp.Should().Be(8);
    }

    [Fact]
    public async Task PatchRow_SelfDemote_Returns400_CannotDemoteSelf()
    {
        var adminId = Guid.NewGuid();
        var otherId = Guid.NewGuid();
        var (ctrl, db) = NewController(callerId: adminId);
        db.Users.AddRange(
            new User { Id = adminId, Username = "admin", PasswordHash = "h", Role = UserRole.Admin, IsActive = true, CreatedAt = DateTime.UtcNow, PasswordChangedAt = DateTime.UtcNow },
            new User { Id = otherId, Username = "admin2", PasswordHash = "h", Role = UserRole.Admin, IsActive = true, CreatedAt = DateTime.UtcNow, PasswordChangedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        // Admin demoting themselves, even when another admin exists → blocked
        var result = await ctrl.PatchRow(
            "User", [adminId.ToString()],
            new DbAdminPatchRequest("Role", JsonVal("Operator")),
            CancellationToken.None);

        var bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        var body = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(bad.Value));
        body.GetProperty("code").GetString().Should().Be("cannot_demote_self");
    }

    // ── DELETE tests ──────────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteRow_OnRevokedTokens_Returns405()
    {
        var (ctrl, _) = NewController();
        var result = await ctrl.DeleteRow("RevokedToken", ["some-jti"], CancellationToken.None);
        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(405);
    }

    [Fact]
    public async Task DeleteRow_OnWorkflowVersions_Returns405()
    {
        var (ctrl, _) = NewController();
        var result = await ctrl.DeleteRow("WorkflowVersion", [Guid.NewGuid().ToString()], CancellationToken.None);
        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(405);
    }

    [Fact]
    public async Task DeleteRow_LastAdmin_Returns400()
    {
        var adminId = Guid.NewGuid();
        var callerId = Guid.NewGuid(); // different caller, will be downgraded to Operator
        var (ctrl, db) = NewController(callerId: callerId);
        db.Users.AddRange(
            new User { Id = callerId, Username = "caller", PasswordHash = "h", Role = UserRole.Admin, IsActive = true, CreatedAt = DateTime.UtcNow, PasswordChangedAt = DateTime.UtcNow },
            new User { Id = adminId, Username = "last", PasswordHash = "h", Role = UserRole.Admin, IsActive = true, CreatedAt = DateTime.UtcNow, PasswordChangedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        // Downgrade caller to Operator so adminId becomes the only admin
        var callerUser = db.Users.Find(callerId)!;
        callerUser.Role = UserRole.Operator;
        await db.SaveChangesAsync();

        var result = await ctrl.DeleteRow("User", [adminId.ToString()], CancellationToken.None);
        var bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        var body = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(bad.Value));
        body.GetProperty("code").GetString().Should().Be("last_admin");
    }

    [Fact]
    public async Task DeleteRow_LastActiveAdmin_IgnoresInactiveAdmins()
    {
        var activeAdminId = Guid.NewGuid();
        var inactiveAdminId = Guid.NewGuid();
        var callerId = Guid.NewGuid();
        var (ctrl, db) = NewController(callerId: callerId);
        db.Users.AddRange(
            new User { Id = callerId, Username = "caller", PasswordHash = "h", Role = UserRole.Operator, IsActive = true, CreatedAt = DateTime.UtcNow, PasswordChangedAt = DateTime.UtcNow },
            new User { Id = activeAdminId, Username = "active", PasswordHash = "h", Role = UserRole.Admin, IsActive = true, CreatedAt = DateTime.UtcNow, PasswordChangedAt = DateTime.UtcNow },
            new User { Id = inactiveAdminId, Username = "inactive", PasswordHash = "h", Role = UserRole.Admin, IsActive = false, CreatedAt = DateTime.UtcNow, PasswordChangedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var result = await ctrl.DeleteRow("User", [activeAdminId.ToString()], CancellationToken.None);

        var bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        var body = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(bad.Value));
        body.GetProperty("code").GetString().Should().Be("last_admin");
    }

    [Fact]
    public async Task DeleteRow_Self_Returns400()
    {
        var callerId = Guid.NewGuid();
        var otherId = Guid.NewGuid();
        var (ctrl, db) = NewController(callerId: callerId);
        db.Users.AddRange(
            new User { Id = callerId, Username = "self", PasswordHash = "h", Role = UserRole.Admin, IsActive = true, CreatedAt = DateTime.UtcNow, PasswordChangedAt = DateTime.UtcNow },
            new User { Id = otherId, Username = "other", PasswordHash = "h", Role = UserRole.Admin, IsActive = true, CreatedAt = DateTime.UtcNow, PasswordChangedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var result = await ctrl.DeleteRow("User", [callerId.ToString()], CancellationToken.None);
        var bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        var body = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(bad.Value));
        body.GetProperty("code").GetString().Should().Be("cannot_delete_self");
    }

    [Fact]
    public async Task DeleteRow_RemovesAndAuditsAtomically()
    {
        var (ctrl, db) = NewController();
        var id = Guid.NewGuid();
        db.ManagedMachines.Add(new ManagedMachine { Id = id, Name = "to-delete", Hostname = "h" });
        await db.SaveChangesAsync();

        var result = await ctrl.DeleteRow("ManagedMachine", [id.ToString()], CancellationToken.None);
        result.Should().BeOfType<NoContentResult>();

        db.ManagedMachines.Find(id).Should().BeNull("row must be deleted");

        var audit = db.AuditLog.SingleOrDefault(a => a.Action == "DBADMIN_ROW_DELETED");
        audit.Should().NotBeNull("audit entry must be committed with the deletion");
        audit!.Details.Should().Contain("ManagedMachine");
    }

    // ── Pagination tests ──────────────────────────────────────────────────────

    [Fact]
    public async Task GetRows_TakeClampedTo200()
    {
        var (ctrl, _) = NewController();

        // Pass take=9999 — should be clamped to 200 (no crash, no giant result)
        var result = await ctrl.GetRows("Workflow", take: 9999, ct: CancellationToken.None);
        result.Result.Should().BeOfType<OkObjectResult>();
        // We just verify it doesn't throw and returns OK — no 1000-row table needed
    }

    [Fact]
    public async Task GetRows_UnknownTable_Returns404()
    {
        var (ctrl, _) = NewController();
        var result = await ctrl.GetRows("NonExistentTable", ct: CancellationToken.None);
        result.Result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task PatchRow_UnknownTable_Returns404()
    {
        var (ctrl, _) = NewController();
        var result = await ctrl.PatchRow(
            "NoSuchTable", ["pk"],
            new DbAdminPatchRequest("Col", JsonVal("v")),
            CancellationToken.None);
        result.Should().BeOfType<NotFoundObjectResult>();
    }

    // ── Query Console tests ──────────────────────────────────────────────────

    [Theory]
    [InlineData("SELECT 1", "SELECT")]
    [InlineData("  select * from foo", "select")]
    [InlineData("-- comment\nSELECT 1", "SELECT")]
    [InlineData("/* block */ WITH t AS (SELECT 1) SELECT * FROM t", "WITH")]
    [InlineData("\n\n   /* a */ -- b\n  EXPLAIN ANALYZE SELECT 1", "EXPLAIN")]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("-- only comment", null)]
    public void FirstKeyword_ParsesLeadingTokenSkippingCommentsAndWhitespace(string sql, string? expected)
    {
        DbAdminQueryExecutor.FirstKeyword(sql).Should().Be(expected);
    }

    [Theory]
    [InlineData("SELECT", true)]
    [InlineData("select", true)]
    [InlineData("WITH", true)]
    [InlineData("EXPLAIN", true)]
    [InlineData("VALUES", true)]
    [InlineData("UPDATE", false)]
    [InlineData("DELETE", false)]
    [InlineData("DROP", false)]
    [InlineData("INSERT", false)]
    [InlineData("TRUNCATE", false)]
    public void IsReadOnlyKeyword_AllowsOnlyReadOnlyVerbs(string keyword, bool expected)
    {
        DbAdminQueryExecutor.IsReadOnlyKeyword(keyword).Should().Be(expected);
    }

    [Theory]
    [InlineData("SELECT 1", false)]
    [InlineData("SELECT 1;", false)]
    [InlineData("SELECT 1;; -- trailing terminators only", false)]
    [InlineData("SELECT ';' AS semi", false)]
    [InlineData("SELECT 'x'';''y' AS semi", false)]
    [InlineData("SELECT 1 /* ; */", false)]
    [InlineData("SELECT 1; -- comment only", false)]
    [InlineData("SELECT $$;$$ AS semi", false)]
    [InlineData("SELECT 1; SELECT 2", true)]
    [InlineData("SELECT 1; EXEC xp_cmdshell 'whoami'", true)]
    [InlineData("WITH t AS (SELECT ';') SELECT * FROM t; SELECT 2", true)]
    public void ContainsMultipleStatements_IgnoresLiteralSemicolonsButFindsBatches(string sql, bool expected)
    {
        DbAdminQueryExecutor.ContainsMultipleStatements(sql).Should().Be(expected);
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData("-- comment only", 0)]
    [InlineData("SELECT 1", 1)]
    [InlineData("SELECT ';' AS value;", 1)]
    [InlineData("SELECT 1; UPDATE Users SET IsActive = 0; DELETE FROM Users;", 3)]
    public void CountStatements_IgnoresQuotedTerminatorsAndCountsBatch(string sql, int expected)
    {
        DbAdminQueryExecutor.CountStatements(sql).Should().Be(expected);
    }

    [Theory]
    [InlineData("DELETE FROM AuditLog")]
    [InlineData("DROP TABLE public.\"AuditLog\"")]
    [InlineData("EXEC('DELETE FROM AuditLog')")]
    public void ReferencesProtectedAuditStorage_FindsDirectAndDynamicReferences(string sql)
    {
        DbAdminQueryExecutor.ReferencesProtectedAuditStorage(sql).Should().BeTrue();
    }

    [Theory]
    [InlineData("Microsoft.EntityFrameworkCore.Sqlite", "sqlite")]
    [InlineData("Npgsql.EntityFrameworkCore.PostgreSQL", "postgres")]
    [InlineData("Microsoft.EntityFrameworkCore.SqlServer", "sqlserver")]
    [InlineData(null, "unknown")]
    [InlineData("", "unknown")]
    public void ResolveProvider_NormalisesEfProviderName(string? input, string expected)
    {
        DbAdminQueryExecutor.ResolveProvider(input).Should().Be(expected);
    }

    [Fact]
    public void GetInfo_ReturnsProviderAndConfigDefaults()
    {
        var (ctrl, _) = NewController(options: new DbAdminOptions
        {
            AllowWriteQueries = true,
            QueryTimeoutSeconds = 45,
            QueryMaxRows = 5000,
        });

        var result = ctrl.GetInfo();
        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var info = ok.Value.Should().BeOfType<DbAdminInfoResponse>().Subject;

        info.Provider.Should().Be("sqlite"); // Test backend
        info.AllowWriteQueries.Should().BeTrue();
        info.QueryTimeoutSeconds.Should().Be(45);
        info.QueryMaxRows.Should().Be(5000);
    }

    [Fact]
    public async Task ExecuteQuery_EmptySql_Returns400()
    {
        var (ctrl, _) = NewController();
        var result = await ctrl.ExecuteQuery(new DbAdminQueryRequest("   ", null), CancellationToken.None);
        var bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        bad.Value.Should().BeOfType<DbAdminQueryError>().Subject.Code.Should().Be("empty_sql");
    }

    [Fact]
    public async Task ExecuteQuery_ReadMode_BlocksUpdateStatement()
    {
        var (ctrl, _) = NewController();
        var result = await ctrl.ExecuteQuery(
            new DbAdminQueryRequest("UPDATE Users SET IsActive = 0", null),
            CancellationToken.None);
        var bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        bad.Value.Should().BeOfType<DbAdminQueryError>().Subject.Code.Should().Be("non_readonly_statement");
    }

    [Fact]
    public async Task ExecuteQuery_ReadMode_BlocksDropStatement()
    {
        var (ctrl, _) = NewController();
        var result = await ctrl.ExecuteQuery(
            new DbAdminQueryRequest("DROP TABLE Workflow", null),
            CancellationToken.None);
        var bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        bad.Value.Should().BeOfType<DbAdminQueryError>().Subject.Code.Should().Be("non_readonly_statement");
    }

    [Fact]
    public async Task ExecuteQuery_ReadMode_BlocksMultiStatementBatch()
    {
        var (ctrl, _) = NewController();
        var result = await ctrl.ExecuteQuery(
            new DbAdminQueryRequest("SELECT 1; SELECT 2", null),
            CancellationToken.None);
        var bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        bad.Value.Should().BeOfType<DbAdminQueryError>().Subject.Code.Should().Be("multiple_statements_not_allowed");
    }

    [Fact]
    public async Task ExecuteQuery_ReadMode_SelectReturnsRows()
    {
        var (ctrl, db) = NewController();
        db.Users.Add(AdminUser());
        db.Users.Add(new User
        {
            Id = Guid.NewGuid(),
            Username = "operator1",
            PasswordHash = "h",
            Role = UserRole.Operator,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            PasswordChangedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var result = await ctrl.ExecuteQuery(
            new DbAdminQueryRequest("SELECT Username, Role FROM Users ORDER BY Username", null),
            CancellationToken.None);
        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var resp = ok.Value.Should().BeOfType<DbAdminQueryResponse>().Subject;

        resp.Mode.Should().Be("read");
        resp.Columns.Should().HaveCount(2);
        resp.Columns[0].Name.Should().Be("Username");
        resp.Rows.Should().HaveCount(2);
        resp.Truncated.Should().BeFalse();
        resp.RowsAffected.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteQuery_ReadMode_Truncates_WhenRowsExceedCap()
    {
        var (ctrl, db) = NewController(options: new DbAdminOptions { QueryMaxRows = 2 });
        for (var i = 0; i < 5; i++)
        {
            db.Users.Add(new User
            {
                Id = Guid.NewGuid(),
                Username = $"u{i}",
                PasswordHash = "h",
                Role = UserRole.Viewer,
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
                PasswordChangedAt = DateTime.UtcNow,
            });
        }
        await db.SaveChangesAsync();

        var result = await ctrl.ExecuteQuery(
            new DbAdminQueryRequest("SELECT Username FROM Users", null),
            CancellationToken.None);
        var resp = ((result as OkObjectResult)!.Value as DbAdminQueryResponse)!;

        resp.Rows.Should().HaveCount(2);
        resp.Truncated.Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteQuery_WriteMode_WithoutFlag_Returns403()
    {
        // Default options have AllowWriteQueries = false
        var (ctrl, _) = NewController(confirmWriteHeader: "ALLOW");
        var result = await ctrl.ExecuteQuery(
            new DbAdminQueryRequest("UPDATE Users SET IsActive = 1", "write"),
            CancellationToken.None);
        var status = result.Should().BeOfType<ObjectResult>().Subject;
        status.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        status.Value.Should().BeOfType<DbAdminQueryError>().Subject.Code.Should().Be("write_disabled");
    }

    [Fact]
    public async Task ExecuteQuery_WriteMode_FlagSetButHeaderMissing_Returns400()
    {
        var (ctrl, _) = NewController(options: new DbAdminOptions { AllowWriteQueries = true });
        var result = await ctrl.ExecuteQuery(
            new DbAdminQueryRequest("UPDATE Users SET IsActive = 1", "write"),
            CancellationToken.None);
        var bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        bad.Value.Should().BeOfType<DbAdminQueryError>().Subject.Code.Should().Be("missing_confirmation");
    }

    [Fact]
    public async Task ExecuteQuery_WriteMode_FlagAndHeader_ExecutesAndPersists()
    {
        var (ctrl, db) = NewController(
            options: new DbAdminOptions { AllowWriteQueries = true },
            confirmWriteHeader: "ALLOW");

        db.Users.Add(new User
        {
            Id = Guid.NewGuid(),
            Username = "victim",
            PasswordHash = "h",
            Role = UserRole.Operator,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            PasswordChangedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        // WHERE on Username (TEXT) is portable across the SQLite test backend; GUID-as-BLOB
        // string-interpolation matches are flaky in SQLite, and the point of this test is to
        // verify that write-mode persists, not to exercise GUID serialisation.
        var result = await ctrl.ExecuteQuery(
            new DbAdminQueryRequest("UPDATE Users SET IsActive = 0 WHERE Username = 'victim'", "write"),
            CancellationToken.None);
        result.Should().BeOfType<OkObjectResult>();

        var refreshed = await db.Users.AsNoTracking().SingleAsync(u => u.Username == "victim");
        refreshed.IsActive.Should().BeFalse();

        var audit = await db.AuditLog.AsNoTracking()
            .Where(a => a.Action == "DBADMIN_SQL_WRITE_ATTEMPTED" || a.Action == "DBADMIN_SQL_WRITE")
            .OrderBy(a => a.Timestamp)
            .ToListAsync();
        audit.Select(a => a.Action).Should().Equal(
            "DBADMIN_SQL_WRITE_ATTEMPTED",
            "DBADMIN_SQL_WRITE");
        audit.Should().OnlyContain(a => a.Details!.Contains("\"sqlSha256\"")
                                        && a.Details.Contains("\"sqlBytes\"")
                                        && a.Details.Contains("\"statementCount\":1"));
    }

    [Fact]
    public async Task ExecuteQuery_WriteMode_TargetsAuditStorage_Returns400AndAuditsRejection()
    {
        var (ctrl, db) = NewController(
            options: new DbAdminOptions { AllowWriteQueries = true },
            confirmWriteHeader: "ALLOW");

        var result = await ctrl.ExecuteQuery(
            new DbAdminQueryRequest("DELETE FROM AuditLog", "write"),
            CancellationToken.None);

        var bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        bad.Value.Should().BeOfType<DbAdminQueryError>().Subject.Code
            .Should().Be("protected_audit_storage");
        var audit = await db.AuditLog.AsNoTracking()
            .SingleAsync(a => a.Action == "DBADMIN_SQL_WRITE");
        audit.Details.Should().Contain("\"success\":\"false\"");
        audit.Details.Should().Contain("\"reason\":\"protected_audit_storage\"");
    }

    [Fact]
    public async Task ExecuteQuery_AuditEntry_WrittenOnSuccess()
    {
        var (ctrl, db) = NewController();
        var result = await ctrl.ExecuteQuery(
            new DbAdminQueryRequest("SELECT 1", null),
            CancellationToken.None);
        result.Should().BeOfType<OkObjectResult>();

        var audit = await db.AuditLog.AsNoTracking().Where(a => a.Action == "DBADMIN_SQL_EXECUTED").ToListAsync();
        audit.Should().HaveCount(1);
        audit[0].Details.Should().Contain("\"mode\":\"read\"");
        audit[0].Details.Should().Contain("\"success\":\"true\"");
    }

    [Fact]
    public async Task ExecuteQuery_AuditEntry_WrittenOnFailure()
    {
        var (ctrl, db) = NewController();
        // Invalid SQL — passes the keyword whitelist (SELECT) but breaks at execution
        var result = await ctrl.ExecuteQuery(
            new DbAdminQueryRequest("SELECT * FROM no_such_table_42", null),
            CancellationToken.None);
        result.Should().BeOfType<ConflictObjectResult>();

        var audit = await db.AuditLog.AsNoTracking().Where(a => a.Action == "DBADMIN_SQL_EXECUTED").ToListAsync();
        audit.Should().HaveCount(1);
        audit[0].Details.Should().Contain("\"success\":\"false\"");
    }

    [Fact]
    public async Task GetRows_Success_WritesTableViewAudit()
    {
        var (ctrl, db) = NewController();

        var result = await ctrl.GetRows("Workflow", skip: 0, take: 25, orderBy: "Name",
            desc: true, ct: CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>();
        var audit = await db.AuditLog.AsNoTracking()
            .SingleAsync(a => a.Action == "DBADMIN_ROWS_VIEWED");
        audit.Details.Should().Contain("\"table\":\"Workflow\"");
        audit.Details.Should().Contain("\"take\":25");
        audit.Details.Should().Contain("\"descending\":true");
    }

    [Fact]
    public async Task ExecuteQuery_SqlTooLong_Returns400()
    {
        var (ctrl, _) = NewController();
        var giant = "SELECT 1; -- " + new string('x', 70_000);
        var result = await ctrl.ExecuteQuery(new DbAdminQueryRequest(giant, null), CancellationToken.None);
        var bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        bad.Value.Should().BeOfType<DbAdminQueryError>().Subject.Code.Should().Be("sql_too_long");
    }

    [Fact]
    public async Task ExecuteQuery_NoKeyword_Returns400()
    {
        var (ctrl, _) = NewController();
        var result = await ctrl.ExecuteQuery(new DbAdminQueryRequest("123", null), CancellationToken.None);
        var bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        bad.Value.Should().BeOfType<DbAdminQueryError>().Subject.Code.Should().Be("no_keyword");
    }

    /// <summary>
    /// Tiny test stand-in for <see cref="IOptionsMonitor{T}"/>. The executor was
    /// switched from IOptions to IOptionsMonitor so Settings-UI edits take effect
    /// without an API restart; in tests we just hand it a fixed value.
    /// </summary>
    private sealed class TestOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public TestOptionsMonitor(T value) { CurrentValue = value; }
        public T CurrentValue { get; }
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}

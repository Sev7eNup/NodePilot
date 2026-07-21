using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NodePilot.Api.Audit;
using NodePilot.Core.Audit;
using NodePilot.Api.Controllers;
using NodePilot.Api.Dtos;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Api.Tests.Controllers;

public class GlobalVariablesControllerTests
{
    private static (GlobalVariablesController controller, CapturingAuditWriter audit)
        NewController(NodePilotDbContext db, string role = "Admin")
    {
        var store = new GlobalVariableStore(db, new NodePilot.Data.Security.DpapiSecretProtector(System.Security.Cryptography.DataProtectionScope.CurrentUser));
        var folders = new GlobalVariableFolderStore(db);
        var audit = new CapturingAuditWriter();
        var controller = new GlobalVariablesController(store, folders, audit);
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            new[]
            {
                new Claim(ClaimTypes.Role, role),
                new Claim(ClaimTypes.Name, "testuser")
            }, "TestAuth"));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };
        return (controller, audit);
    }

    private static async Task<GlobalVariable> Seed(NodePilotDbContext db, string name,
        string value = "plain-value", bool isSecret = false)
    {
        var store = new GlobalVariableStore(db, new NodePilot.Data.Security.DpapiSecretProtector(System.Security.Cryptography.DataProtectionScope.CurrentUser));
        return await store.CreateAsync(name, value, isSecret, null, GlobalVariableFolder.RootFolderId, "seeder", CancellationToken.None);
    }

    // ---- GetAll ----

    [Fact]
    public async Task GetAll_NonSecretValue_ReturnedAsPlaintext()
    {
        var db = TestDbFactory.Create();
        await Seed(db, "MY_VAR", "hello-world", isSecret: false);
        var (controller, _) = NewController(db);

        var result = await controller.GetAll(CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var list = ok.Value.Should().BeAssignableTo<List<GlobalVariableResponse>>().Subject;
        list.Should().ContainSingle(v => v.Name == "MY_VAR" && v.Value == "hello-world");
    }

    [Fact]
    public async Task GetAll_SecretValue_MaskedAs3Stars()
    {
        var db = TestDbFactory.Create();
        await Seed(db, "SECRET_KEY", "super-secret", isSecret: true);
        var (controller, _) = NewController(db);

        var result = await controller.GetAll(CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var list = ok.Value.Should().BeAssignableTo<List<GlobalVariableResponse>>().Subject;
        list.Should().ContainSingle(v => v.Name == "SECRET_KEY" && v.Value == "***");
    }

    // ---- Create ----

    [Fact]
    public async Task Create_ValidName_Returns201()
    {
        var db = TestDbFactory.Create();
        var (controller, _) = NewController(db);

        var result = await controller.Create(
            new CreateGlobalVariableRequest("MY_VAR_1", "value", false, null, null),
            CancellationToken.None);

        result.Result.Should().BeOfType<CreatedAtActionResult>();
        db.GlobalVariables.Should().ContainSingle(v => v.Name == "MY_VAR_1");
    }

    [Fact]
    public async Task Create_InvalidNameWithDot_Returns400()
    {
        var db = TestDbFactory.Create();
        var (controller, _) = NewController(db);

        var result = await controller.Create(
            new CreateGlobalVariableRequest("my.var", "value", false, null, null),
            CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Create_WritesAuditEntry()
    {
        var db = TestDbFactory.Create();
        var (controller, audit) = NewController(db);

        await controller.Create(
            new CreateGlobalVariableRequest("AUDIT_VAR", "val", false, null, null),
            CancellationToken.None);

        audit.Calls.Should().ContainSingle(c => c.Action == "GLOBAL_VARIABLE_CREATED");
    }

    // ---- Update ----

    [Fact]
    public async Task Update_ChangeValue_Persisted()
    {
        var db = TestDbFactory.Create();
        var variable = await Seed(db, "UPDATE_VAR", "old-value");
        var (controller, _) = NewController(db);

        var result = await controller.Update(variable.Id,
            new UpdateGlobalVariableRequest("UPDATE_VAR", "new-value", false, null, null),
            CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
        var updated = db.GlobalVariables.Find(variable.Id)!;
        updated.Value.Should().Be("new-value");
    }

    [Fact]
    public async Task Update_SecretWithNullValue_KeepsExistingCiphertext()
    {
        var db = TestDbFactory.Create();
        var variable = await Seed(db, "SECRET_VAR", "original-secret", isSecret: true);
        var originalCiphertext = db.GlobalVariables.Find(variable.Id)!.Value;
        var (controller, _) = NewController(db);

        // Null value = "leave existing value unchanged"
        var result = await controller.Update(variable.Id,
            new UpdateGlobalVariableRequest("SECRET_VAR", null, true, null, null),
            CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
        db.GlobalVariables.Find(variable.Id)!.Value.Should().Be(originalCiphertext);
    }

    [Fact]
    public async Task Update_DemoteSecretWithoutValue_Returns400()
    {
        var db = TestDbFactory.Create();
        var variable = await Seed(db, "SECRET_VAR", "original-secret", isSecret: true);
        var (controller, _) = NewController(db);

        // Demoting IsSecret=true → false without supplying a new plaintext would force the
        // store to decrypt-and-persist the ciphertext as cleartext (M-24). The store throws
        // InvalidOperationException; the controller now surfaces that as 400 instead of 500.
        var result = await controller.Update(variable.Id,
            new UpdateGlobalVariableRequest("SECRET_VAR", null, false, null, null),
            CancellationToken.None);

        var bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        bad.Value.Should().NotBeNull();
        // Ciphertext stays untouched — guard fires before any field mutation persists.
        db.GlobalVariables.Find(variable.Id)!.IsSecret.Should().BeTrue();
    }

    [Fact]
    public async Task Update_NotFound_Returns404()
    {
        var db = TestDbFactory.Create();
        var (controller, _) = NewController(db);

        var result = await controller.Update(Guid.NewGuid(),
            new UpdateGlobalVariableRequest("X", "v", false, null, null),
            CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Update_NullFolderId_PreservesCurrentFolder()
    {
        var db = TestDbFactory.Create();
        // Place the variable in a non-Root folder so a null-folderId "move to Root" regression is observable.
        var folder = new GlobalVariableFolder
        {
            Id = Guid.NewGuid(), ParentFolderId = GlobalVariableFolder.RootFolderId,
            Name = "Env", Path = "/Env", Depth = 1,
        };
        db.GlobalVariableFolders.Add(folder);
        var variable = new GlobalVariable
        {
            Id = Guid.NewGuid(), Name = "DB_HOST", Value = "db.prod", IsSecret = false,
            FolderId = folder.Id, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        db.GlobalVariables.Add(variable);
        await db.SaveChangesAsync();
        var (controller, _) = NewController(db);

        // folderId omitted (null) must NOT relocate the variable — it stays in /Env. This is the
        // `np globals import --upsert` scenario: the import payload carries no folderId, so a null
        // meaning "Root" would silently strip the folder assignment on every bulk upsert.
        var result = await controller.Update(variable.Id,
            new UpdateGlobalVariableRequest("DB_HOST", "db.prod", false, null, null),
            CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
        db.GlobalVariables.Find(variable.Id)!.FolderId.Should().Be(folder.Id,
            "null folderId on update means \"keep the current folder\", not \"move to Root\"");
    }

    [Fact]
    public async Task Update_NonExistentFolderId_Returns400()
    {
        var db = TestDbFactory.Create();
        var variable = await Seed(db, "MY_VAR", "v");
        var (controller, _) = NewController(db);

        var result = await controller.Update(variable.Id,
            new UpdateGlobalVariableRequest("MY_VAR", "v", false, null, Guid.NewGuid()),
            CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    // ---- Delete ----

    [Fact]
    public async Task Delete_Existing_Returns204()
    {
        var db = TestDbFactory.Create();
        var variable = await Seed(db, "DEL_VAR");
        var (controller, _) = NewController(db);

        var result = await controller.Delete(variable.Id, CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
        db.GlobalVariables.Find(variable.Id).Should().BeNull();
    }

    [Fact]
    public async Task Delete_NotFound_Returns404()
    {
        var db = TestDbFactory.Create();
        var (controller, _) = NewController(db);
        var result = await controller.Delete(Guid.NewGuid(), CancellationToken.None);
        result.Should().BeOfType<NotFoundResult>();
    }
}

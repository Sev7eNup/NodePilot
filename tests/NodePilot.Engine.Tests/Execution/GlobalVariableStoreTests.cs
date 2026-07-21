using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.Data.Security;
using Xunit;

namespace NodePilot.Engine.Tests.Execution;

public class GlobalVariableStoreTests
{
    private static (NodePilotDbContext db, SqliteConnection conn) NewDb()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var db = new NodePilotDbContext(
            new DbContextOptionsBuilder<NodePilotDbContext>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();
        return (db, conn);
    }

    private static GlobalVariableStore NewStore(NodePilotDbContext db)
        => new(db, new DpapiSecretProtector(DataProtectionScope.CurrentUser));

    [Fact]
    public async Task PlainVariable_RoundTripsValueVerbatim()
    {
        var (db, conn) = NewDb();
        try
        {
            var store = NewStore(db);
            var created = await store.CreateAsync("ENVIRONMENT", "prod", isSecret: false, description: null,
                folderId: GlobalVariableFolder.RootFolderId, updatedBy: null, ct: CancellationToken.None);

            created.Value.Should().Be("prod", "plain values are stored as-is, no encoding");
            (await store.GetValueAsync("ENVIRONMENT", CancellationToken.None)).Should().Be("prod");
        }
        finally { conn.Dispose(); }
    }

    [Fact]
    public async Task SecretVariable_StoredAsBase64Ciphertext_DecryptsOnRead()
    {
        var (db, conn) = NewDb();
        try
        {
            var store = NewStore(db);
            var created = await store.CreateAsync("API_KEY", "sk-live-1234", isSecret: true, description: null,
                folderId: GlobalVariableFolder.RootFolderId, updatedBy: null, ct: CancellationToken.None);

            // Ciphertext must not equal plaintext — DPAPI should have turned it into opaque Base64.
            created.Value.Should().NotBe("sk-live-1234");
            IsProbablyBase64(created.Value).Should().BeTrue();

            // Round-trip returns plaintext.
            (await store.GetValueAsync("API_KEY", CancellationToken.None)).Should().Be("sk-live-1234");
        }
        finally { conn.Dispose(); }
    }

    [Fact]
    public async Task GetAllResolvedAsync_ReturnsNameToPlaintextMap()
    {
        var (db, conn) = NewDb();
        try
        {
            var store = NewStore(db);
            await store.CreateAsync("PLAIN", "pv", false, null, GlobalVariableFolder.RootFolderId, null, CancellationToken.None);
            await store.CreateAsync("SECRET", "sv", true, null, GlobalVariableFolder.RootFolderId, null, CancellationToken.None);

            var resolved = await store.GetAllResolvedAsync(CancellationToken.None);

            resolved.Should().HaveCount(2);
            resolved["PLAIN"].Should().Be("pv");
            resolved["SECRET"].Should().Be("sv");
        }
        finally { conn.Dispose(); }
    }

    [Fact]
    public async Task Update_WithoutNewValue_KeepsExistingSecret()
    {
        var (db, conn) = NewDb();
        try
        {
            var store = NewStore(db);
            var created = await store.CreateAsync("TOKEN", "orig", true, null, GlobalVariableFolder.RootFolderId, null, CancellationToken.None);

            await store.UpdateAsync(created.Id, "TOKEN_RENAMED", value: null, isSecret: true,
                description: "renamed", folderId: GlobalVariableFolder.RootFolderId, updatedBy: "admin", ct: CancellationToken.None);

            var reloaded = (await store.GetValueAsync("TOKEN_RENAMED", CancellationToken.None));
            reloaded.Should().Be("orig", "null value on update must leave ciphertext untouched");
        }
        finally { conn.Dispose(); }
    }

    [Fact]
    public async Task Update_NullFolderId_KeepsExistingFolder()
    {
        var (db, conn) = NewDb();
        try
        {
            var store = NewStore(db);
            var folder = new GlobalVariableFolder
            {
                Id = Guid.NewGuid(), ParentFolderId = GlobalVariableFolder.RootFolderId,
                Name = "Env", Path = "/Env", Depth = 1,
            };
            db.GlobalVariableFolders.Add(folder);
            await db.SaveChangesAsync();
            var created = await store.CreateAsync("DB_HOST", "db.prod", false, null,
                folder.Id, null, CancellationToken.None);

            // folderId: null = "leave the existing folder untouched" — the variable must stay in /Env,
            // not be silently relocated to Root. Mirrors the value:null = unchanged convention.
            await store.UpdateAsync(created.Id, "DB_HOST", value: null, isSecret: false,
                description: null, folderId: null, updatedBy: "admin", ct: CancellationToken.None);

            db.GlobalVariables.Find(created.Id)!.FolderId.Should().Be(folder.Id);
        }
        finally { conn.Dispose(); }
    }

    private static bool IsProbablyBase64(string s)
    {
        try { Convert.FromBase64String(s); return true; }
        catch { return false; }
    }
}

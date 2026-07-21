using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NodePilot.Core.Enums;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.Data.Security;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Data.Tests;

/// <summary>
/// Each logical operation runs on its OWN <see cref="NodePilotDbContext"/> over a shared in-memory
/// connection — mirroring production, where the store is resolved per request (scoped). This is
/// what surfaces the route-diff correctness (a single reused context would mask create-time tracking).
/// </summary>
public sealed class NotificationRuleStoreTests : IDisposable
{
    private readonly SqliteConnection _conn;

    public NotificationRuleStoreTests()
    {
        var (conn, seed) = TestDbFactory.CreateWithConnection();
        _conn = conn;
        seed.Dispose(); // only needed it to EnsureCreated the schema on the shared connection
    }

    public void Dispose() => _conn.Dispose();

    private NodePilotDbContext Ctx()
        => new(new DbContextOptionsBuilder<NodePilotDbContext>().UseSqlite(_conn).Options);

    private static NotificationRuleStore Store(NodePilotDbContext c)
        => new(c, new AesGcmSecretProtector(Key()));

    private static byte[] Key()
    {
        var k = new byte[32];
        for (var i = 0; i < k.Length; i++) k[i] = (byte)(i + 3);
        return k;
    }

    private static NotificationRule Draft(string name, params NotificationRoute[] routes) => new()
    {
        Name = name,
        EventTypes = "ExecutionFailed",
        ScopeKind = NotificationScopeKind.Global,
        Routes = routes.ToList(),
    };

    private static NotificationRoute Route(NotificationChannel channel, string target, string? secret = null, Guid id = default)
        => new() { Id = id, Channel = channel, Target = target, Secret = secret };

    [Fact]
    public async Task Create_PersistsRoutes_OrderedByOrder()
    {
        Guid id;
        await using (var c = Ctx())
            id = (await Store(c).CreateAsync(Draft("rule",
                Route(NotificationChannel.Email, "a@x"),
                Route(NotificationChannel.GenericWebhook, "https://hook"),
                Route(NotificationChannel.Email, "b@x")), "alice", CancellationToken.None)).Id;

        await using var read = Ctx();
        var loaded = await Store(read).GetAsync(id, CancellationToken.None);
        loaded!.Routes.Should().HaveCount(3);
        loaded.Routes.Select(r => r.Target).Should().ContainInOrder("a@x", "https://hook", "b@x");
        loaded.UpdatedBy.Should().Be("alice");
    }

    [Fact]
    public async Task Update_KeepsRouteId_WithSentinelSecret_PreservesSecret()
    {
        Guid id, routeId;
        await using (var c = Ctx())
        {
            var created = await Store(c).CreateAsync(Draft("rule",
                Route(NotificationChannel.GenericWebhook, "https://hook", secret: "hmac-secret")), "alice", CancellationToken.None);
            id = created.Id;
            routeId = created.Routes.Single().Id;
        }

        await using (var c = Ctx())
            await Store(c).UpdateAsync(id, Draft("rule-renamed",
                Route(NotificationChannel.GenericWebhook, "https://hook2", secret: NotificationRuleStore.UnchangedSecret, id: routeId)),
                "bob", CancellationToken.None); // must NOT throw a tracking collision

        await using var read = Ctx();
        var store = Store(read);
        var loaded = await store.GetAsync(id, CancellationToken.None);
        loaded!.Name.Should().Be("rule-renamed");
        loaded.Routes.Single().Target.Should().Be("https://hook2");
        loaded.Routes.Single().Id.Should().Be(routeId); // id stable across update
        (await store.GetRouteSecretAsync(routeId, CancellationToken.None)).Should().Be("hmac-secret");
    }

    [Fact]
    public async Task Update_AddAndRemoveRoutes_NoKeyCollision()
    {
        Guid id, keepId;
        await using (var c = Ctx())
        {
            var created = await Store(c).CreateAsync(Draft("rule",
                Route(NotificationChannel.Email, "keep@x"),
                Route(NotificationChannel.Email, "drop@x")), null, CancellationToken.None);
            id = created.Id;
            keepId = created.Routes.Single(r => r.Target == "keep@x").Id;
        }

        await using (var c = Ctx())
            await Store(c).UpdateAsync(id, Draft("rule",
                Route(NotificationChannel.Email, "keep@x", id: keepId),
                Route(NotificationChannel.GenericWebhook, "https://new")), null, CancellationToken.None);

        await using var read = Ctx();
        var loaded = await Store(read).GetAsync(id, CancellationToken.None);
        loaded!.Routes.Select(r => r.Target).Should().BeEquivalentTo(["keep@x", "https://new"]);
    }

    [Fact]
    public async Task CreateAndUpdate_PersistsRouteCondition()
    {
        const string initialCondition = """
        {"type":"comparison","op":"==","left":{"kind":"variable","source":"event","name":"severity"},"right":{"kind":"literal","value":"Critical"}}
        """;
        const string updatedCondition = """
        {"type":"comparison","op":"==","left":{"kind":"variable","source":"event","name":"status"},"right":{"kind":"literal","value":"Failed"}}
        """;

        Guid id, routeId;
        await using (var c = Ctx())
        {
            var route = Route(NotificationChannel.Email, "a@x");
            route.ConditionExpressionJson = initialCondition;
            var created = await Store(c).CreateAsync(Draft("rule", route), null, CancellationToken.None);
            id = created.Id;
            routeId = created.Routes.Single().Id;
        }

        await using (var c = Ctx())
        {
            var route = Route(NotificationChannel.Email, "a@x", id: routeId);
            route.ConditionExpressionJson = updatedCondition;
            await Store(c).UpdateAsync(id, Draft("rule", route), null, CancellationToken.None);
        }

        await using var read = Ctx();
        var loaded = await Store(read).GetAsync(id, CancellationToken.None);
        loaded!.Routes.Single().ConditionExpressionJson.Should().Be(updatedCondition);
    }

    [Fact]
    public async Task Update_NewSecretValue_ReplacesSecret()
    {
        Guid id, routeId;
        await using (var c = Ctx())
        {
            var created = await Store(c).CreateAsync(Draft("rule",
                Route(NotificationChannel.GenericWebhook, "https://hook", secret: "old")), null, CancellationToken.None);
            id = created.Id;
            routeId = created.Routes.Single().Id;
        }

        await using (var c = Ctx())
            await Store(c).UpdateAsync(id, Draft("rule",
                Route(NotificationChannel.GenericWebhook, "https://hook", secret: "new", id: routeId)), null, CancellationToken.None);

        await using var read = Ctx();
        (await Store(read).GetRouteSecretAsync(routeId, CancellationToken.None)).Should().Be("new");
    }

    [Fact]
    public async Task Delete_RemovesSuppressionAndAttemptState()
    {
        Guid id;
        await using (var c = Ctx())
        {
            var created = await Store(c).CreateAsync(Draft("rule", Route(NotificationChannel.Email, "a@x")), null, CancellationToken.None);
            id = created.Id;
            c.NotificationSuppressionStates.Add(new NotificationSuppressionState
            { Id = Guid.NewGuid(), NotificationRuleId = id, DedupKey = "k", LastFiredAt = DateTime.UtcNow });
            c.NotificationDeliveryAttempts.Add(new NotificationDeliveryAttempt
            { Id = Guid.NewGuid(), NotificationRuleId = id, NotificationRouteId = created.Routes.First().Id, EventKey = "e", DedupKey = "k" });
            await c.SaveChangesAsync();
        }

        await using (var c = Ctx())
            await Store(c).DeleteAsync(id, CancellationToken.None);

        await using var read = Ctx();
        (await Store(read).GetAsync(id, CancellationToken.None)).Should().BeNull();
        (await read.NotificationSuppressionStates.CountAsync()).Should().Be(0);
        (await read.NotificationDeliveryAttempts.CountAsync()).Should().Be(0);
    }
}

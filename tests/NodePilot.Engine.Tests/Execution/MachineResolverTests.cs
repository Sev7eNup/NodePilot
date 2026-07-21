using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NodePilot.Core.Models;
using NodePilot.Engine.Execution;
using NodePilot.Engine.Tests.Helpers;
using Xunit;

namespace NodePilot.Engine.Tests.Execution;

/// <summary>
/// Coverage for the MachineResolver lookup-and-fallback semantics: GUID lookup,
/// hostname/name lookup, ad-hoc synthesis when nothing matches, and the negative
/// cache that keeps repeated unregistered lookups out of the DB.
/// </summary>
public class MachineResolverTests
{
    public MachineResolverTests()
    {
        // Tests share process state with the rest of the suite — clear the negative
        // cache so prior tests don't bleed cached "unregistered" entries into ours.
        MachineResolver.InvalidateCache();
    }

    [Fact]
    public async Task ResolveAsync_NullOrEmpty_ReturnsNull()
    {
        await using var db = TestDbContext.Create();

        (await MachineResolver.ResolveAsync(db, null, NullLogger.Instance, CancellationToken.None))
            .Should().BeNull();
        (await MachineResolver.ResolveAsync(db, "", NullLogger.Instance, CancellationToken.None))
            .Should().BeNull();
        (await MachineResolver.ResolveAsync(db, "   ", NullLogger.Instance, CancellationToken.None))
            .Should().BeNull();
    }

    [Fact]
    public async Task ResolveAsync_GuidMatch_ReturnsRegisteredMachine()
    {
        await using var db = TestDbContext.Create();
        var machine = new ManagedMachine
        {
            Id = Guid.NewGuid(),
            Name = "PROD-DC-01",
            Hostname = "prod-dc-01.contoso.local",
            WinRmPort = 5986,
            UseSsl = true,
        };
        db.ManagedMachines.Add(machine);
        await db.SaveChangesAsync();

        var resolved = await MachineResolver.ResolveAsync(
            db, machine.Id.ToString(), NullLogger.Instance, CancellationToken.None);

        resolved.Should().NotBeNull();
        resolved!.Id.Should().Be(machine.Id);
        resolved.Hostname.Should().Be("prod-dc-01.contoso.local");
        resolved.UseSsl.Should().BeTrue();
    }

    [Fact]
    public async Task ResolveAsync_HostnameMatch_ReturnsRegisteredMachine()
    {
        await using var db = TestDbContext.Create();
        var machine = new ManagedMachine
        {
            Id = Guid.NewGuid(),
            Name = "Backup Server",
            Hostname = "backup.contoso.local",
            WinRmPort = 5985,
            UseSsl = false,
        };
        db.ManagedMachines.Add(machine);
        await db.SaveChangesAsync();

        var resolved = await MachineResolver.ResolveAsync(
            db, "backup.contoso.local", NullLogger.Instance, CancellationToken.None);

        resolved.Should().NotBeNull();
        resolved!.Id.Should().Be(machine.Id);
        resolved.Name.Should().Be("Backup Server");
    }

    [Fact]
    public async Task ResolveAsync_NameMatch_ReturnsRegisteredMachine()
    {
        await using var db = TestDbContext.Create();
        var machine = new ManagedMachine
        {
            Id = Guid.NewGuid(),
            Name = "Primary",
            Hostname = "10.0.0.5",
            WinRmPort = 5985,
            UseSsl = false,
        };
        db.ManagedMachines.Add(machine);
        await db.SaveChangesAsync();

        var resolved = await MachineResolver.ResolveAsync(
            db, "Primary", NullLogger.Instance, CancellationToken.None);

        resolved.Should().NotBeNull();
        resolved!.Id.Should().Be(machine.Id);
    }

    [Fact]
    public async Task ResolveAsync_UnregisteredHostname_ReturnsAdHocMachine()
    {
        await using var db = TestDbContext.Create();

        var resolved = await MachineResolver.ResolveAsync(
            db, "ad-hoc-host.contoso.local", NullLogger.Instance, CancellationToken.None);

        resolved.Should().NotBeNull();
        resolved!.Id.Should().Be(Guid.Empty); // marker for ad-hoc
        resolved.Name.Should().Be("ad-hoc-host.contoso.local");
        resolved.Hostname.Should().Be("ad-hoc-host.contoso.local");
        resolved.WinRmPort.Should().Be(5985);
        resolved.UseSsl.Should().BeFalse();
    }

    [Fact]
    public async Task ResolveAsync_UnregisteredGuid_FallsBackToAdHoc()
    {
        // A GUID that doesn't match any machine — the resolver falls through GUID lookup,
        // then hostname lookup (which won't match either since GUIDs aren't valid hostnames
        // in the seeded set), and finally returns an ad-hoc machine using the GUID string
        // as the hostname.
        await using var db = TestDbContext.Create();
        var random = Guid.NewGuid().ToString();

        var resolved = await MachineResolver.ResolveAsync(
            db, random, NullLogger.Instance, CancellationToken.None);

        resolved.Should().NotBeNull();
        resolved!.Id.Should().Be(Guid.Empty);
        resolved.Hostname.Should().Be(random);
    }

    [Fact]
    public async Task ResolveAsync_NegativeCache_SkipsDbOnRepeatLookup()
    {
        await using var db = TestDbContext.Create();

        // First lookup populates the cache
        var first = await MachineResolver.ResolveAsync(
            db, "nonexistent.example", NullLogger.Instance, CancellationToken.None);
        first.Should().NotBeNull();
        first!.Id.Should().Be(Guid.Empty);

        // Now disposing the DB context — the second call should hit the cache and
        // never touch the disposed context. If the cache were broken, EF would throw.
        await db.DisposeAsync();

        var second = await MachineResolver.ResolveAsync(
            null!, "nonexistent.example", NullLogger.Instance, CancellationToken.None);
        second.Should().NotBeNull();
        second!.Id.Should().Be(Guid.Empty);
        second.Hostname.Should().Be("nonexistent.example");
    }

    [Fact]
    public async Task InvalidateCache_DropsNegativeEntries()
    {
        await using var db = TestDbContext.Create();

        // Prime the cache for "freshly-registered"
        _ = await MachineResolver.ResolveAsync(
            db, "freshly-registered", NullLogger.Instance, CancellationToken.None);

        // Operator just registered the machine — invalidate so the next lookup
        // re-queries the DB instead of staying in the negative cache.
        var machine = new ManagedMachine
        {
            Id = Guid.NewGuid(),
            Name = "freshly-registered",
            Hostname = "freshly-registered",
            WinRmPort = 5986,
            UseSsl = true,
        };
        db.ManagedMachines.Add(machine);
        await db.SaveChangesAsync();

        MachineResolver.InvalidateCache();

        var resolved = await MachineResolver.ResolveAsync(
            db, "freshly-registered", NullLogger.Instance, CancellationToken.None);

        resolved.Should().NotBeNull();
        resolved!.Id.Should().Be(machine.Id); // resolved to the registered row, not ad-hoc
        resolved.UseSsl.Should().BeTrue();
    }
}

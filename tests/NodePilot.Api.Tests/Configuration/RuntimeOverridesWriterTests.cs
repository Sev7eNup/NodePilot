using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NodePilot.Api.Configuration;
using Xunit;

namespace NodePilot.Api.Tests.Configuration;

/// <summary>
/// Behaviour tests for <see cref="RuntimeOverridesWriter"/> — the file-level building
/// block that backs the admin Settings UI. Atomic writes, backup rotation, ETag
/// stability and the restart-marker lifecycle are covered here so later work on this
/// feature can build on a known-good foundation.
/// </summary>
public sealed class RuntimeOverridesWriterTests : IDisposable
{
    private readonly string _tempDir;

    public RuntimeOverridesWriterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "np-runtime-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    private RuntimeOverridesWriter NewWriter(string? subPath = null)
    {
        var path = Path.Combine(_tempDir, subPath ?? "appsettings.runtime.json");
        return new RuntimeOverridesWriter(path, NullLogger<RuntimeOverridesWriter>.Instance);
    }

    [Fact]
    public void ReadOrEmpty_FileMissing_ReturnsEmptyObject()
    {
        var writer = NewWriter();
        var root = writer.ReadOrEmpty();
        root.Should().NotBeNull();
        root.Count.Should().Be(0);
    }

    [Fact]
    public void MutateAndWrite_FirstWrite_CreatesFile_NoBackup()
    {
        var writer = NewWriter();
        writer.MutateAndWrite(root => root["Smtp"] = new JsonObject { ["Host"] = "mail.example.com" });

        File.Exists(writer.OverridesPath).Should().BeTrue();
        Directory.GetFiles(_tempDir, "*.bak.*").Should().BeEmpty(
            "first-time write has no prior file to back up");

        var roundtrip = writer.ReadOrEmpty();
        roundtrip["Smtp"]?["Host"]?.GetValue<string>().Should().Be("mail.example.com");
    }

    [Fact]
    public void MutateAndWrite_SecondWrite_CreatesBackup()
    {
        var writer = NewWriter();
        writer.MutateAndWrite(root => root["Smtp"] = new JsonObject { ["Host"] = "v1" });
        writer.MutateAndWrite(root => (root["Smtp"]!.AsObject())["Host"] = "v2");

        var backups = Directory.GetFiles(_tempDir, "*.bak.*");
        backups.Should().HaveCount(1, "the second write rotates the v1 file into a .bak");

        // The backup must contain the OLD value, the live file the NEW value.
        var backupContent = File.ReadAllText(backups[0]);
        backupContent.Should().Contain("v1").And.NotContain("v2");

        var liveContent = File.ReadAllText(writer.OverridesPath);
        liveContent.Should().Contain("v2");
    }

    [Fact]
    public void MutateAndWrite_BackupRotation_KeepsOnlyLastTen()
    {
        var writer = NewWriter();
        // 12 writes -> 11 backups generated, oldest one should be pruned to keep 10.
        for (var i = 0; i < 12; i++)
        {
            writer.MutateAndWrite(root => root["Counter"] = i);
        }
        var backups = Directory.GetFiles(_tempDir, "*.bak.*");
        backups.Length.Should().BeLessThanOrEqualTo(10,
            "writer rotates backups down to the last ten so an aggressive editor doesn't fill the disk");
    }

    [Fact]
    public void ComputeSectionEtag_StableAcrossKeyReordering()
    {
        var writer = NewWriter();
        writer.MutateAndWrite(root => root["Smtp"] = new JsonObject
        {
            ["Host"] = "h", ["Port"] = 25, ["From"] = "a@b.c"
        });
        var etag1 = writer.ComputeSectionEtag("Smtp");

        // Re-write with a different in-memory key order — semantically identical.
        writer.MutateAndWrite(root => root["Smtp"] = new JsonObject
        {
            ["Port"] = 25, ["From"] = "a@b.c", ["Host"] = "h"
        });
        var etag2 = writer.ComputeSectionEtag("Smtp");

        etag2.Should().Be(etag1, "ETag is computed against canonicalised JSON so key order is irrelevant");
    }

    [Fact]
    public void ComputeSectionEtag_ChangesWhenValueChanges()
    {
        var writer = NewWriter();
        writer.MutateAndWrite(root => root["Smtp"] = new JsonObject { ["Host"] = "old" });
        var etag1 = writer.ComputeSectionEtag("Smtp");

        writer.MutateAndWrite(root => (root["Smtp"]!.AsObject())["Host"] = "new");
        var etag2 = writer.ComputeSectionEtag("Smtp");

        etag2.Should().NotBe(etag1);
    }

    [Fact]
    public void ComputeSectionEtag_MissingSection_ReturnsNullEtag()
    {
        var writer = NewWriter();
        var etag = writer.ComputeSectionEtag("Smtp");
        etag.Should().Be(RuntimeOverridesWriter.ComputeEtag(null),
            "missing section must produce a deterministic 'no value' ETag distinct from any real section");
    }

    [Fact]
    public void MarkRestartRequired_AccumulatesSections()
    {
        var writer = NewWriter();
        var t0 = DateTimeOffset.UtcNow;
        writer.MarkRestartRequired(new[] { "Smtp" }, t0);
        writer.MarkRestartRequired(new[] { "Llm", "Smtp" /* duplicate */ }, t0.AddMinutes(5));

        var status = writer.ReadStatus();
        status.RestartRequired.Should().BeTrue();
        status.RestartRequiredFor.Should().BeEquivalentTo(new[] { "Llm", "Smtp" });
        // restartRequiredSince should preserve the OLDEST timestamp (not be overwritten by later marks).
        status.RestartRequiredSince!.Value.Should().BeCloseTo(t0, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void ClearRestartMarker_RemovesPendingState()
    {
        var writer = NewWriter();
        writer.MarkRestartRequired(new[] { "Smtp" }, DateTimeOffset.UtcNow);
        writer.ReadStatus().RestartRequired.Should().BeTrue();

        writer.ClearRestartMarker();

        var status = writer.ReadStatus();
        status.RestartRequired.Should().BeFalse();
        status.RestartRequiredFor.Should().BeEmpty();
        status.RestartRequiredSince.Should().BeNull();
    }

    [Fact]
    public void ClearRestartMarker_FileMissing_NoThrow()
    {
        var writer = NewWriter();
        // Acts before any file exists — must be a no-op, not an exception.
        var act = () => writer.ClearRestartMarker();
        act.Should().NotThrow();
    }

    [Fact]
    public void ClearRestartMarker_NoMarker_DoesNotChurnBackup()
    {
        var writer = NewWriter();
        writer.MutateAndWrite(root => root["Smtp"] = new JsonObject { ["Host"] = "h" });
        var backupsBefore = Directory.GetFiles(_tempDir, "*.bak.*").Length;

        writer.ClearRestartMarker();

        var backupsAfter = Directory.GetFiles(_tempDir, "*.bak.*").Length;
        backupsAfter.Should().Be(backupsBefore,
            "clearing a marker that isn't there must not write the file (no backup churn on every restart)");
    }

    [Fact]
    public void ReadStatus_RecordsLastSaveMetadata()
    {
        var writer = NewWriter();
        writer.RecordLastSave("admin@example.com", DateTimeOffset.UtcNow);
        var status = writer.ReadStatus();
        status.LastSavedBy.Should().Be("admin@example.com");
        status.LastSavedAt.Should().NotBeNull();
    }

    [Fact]
    public void MutateAndWrite_PreservesUnrelatedSections()
    {
        var writer = NewWriter();
        writer.MutateAndWrite(root =>
        {
            root["Smtp"] = new JsonObject { ["Host"] = "smtp" };
            root["Llm"]  = new JsonObject { ["Model"] = "gpt" };
        });

        // A targeted edit on Smtp must not collateral-damage the Llm section.
        writer.MutateAndWrite(root => (root["Smtp"]!.AsObject())["Host"] = "smtp2");

        var roundtrip = writer.ReadOrEmpty();
        roundtrip["Llm"]?["Model"]?.GetValue<string>().Should().Be("gpt");
        roundtrip["Smtp"]?["Host"]?.GetValue<string>().Should().Be("smtp2");
    }

    [Fact]
    public void ReadOrEmpty_NonObjectRoot_Throws()
    {
        var writer = NewWriter();
        File.WriteAllText(writer.OverridesPath, "[1,2,3]");
        var act = () => writer.ReadOrEmpty();
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*JSON object*",
                "the override file must always have an object at the root so we can address sections by name");
    }

    [Fact]
    public void ReadOrEmpty_EmptyFile_ReturnsEmptyObject()
    {
        var writer = NewWriter();
        File.WriteAllText(writer.OverridesPath, string.Empty);
        var root = writer.ReadOrEmpty();
        root.Count.Should().Be(0);
    }

    [Fact]
    public void TryUpdateSectionAtomic_MatchingEtag_WritesAndReturnsNewEtag()
    {
        var writer = NewWriter();
        // Seed: write an initial section.
        writer.MutateAndWrite(root => root["Smtp"] = new JsonObject { ["Host"] = "old" });
        var etag = writer.ComputeSectionEtag("Smtp");

        var result = writer.TryUpdateSectionAtomic(
            "Smtp", etag,
            new JsonObject { ["Host"] = "new" },
            restartRequiredFor: new[] { "Smtp" },
            savedBy: "admin",
            now: DateTimeOffset.UtcNow);

        result.Success.Should().BeTrue();
        result.CurrentEtag.Should().NotBe(etag);
        var fresh = writer.ReadOrEmpty();
        fresh["Smtp"]?["Host"]?.GetValue<string>().Should().Be("new");
        writer.ReadStatus().RestartRequiredFor.Should().Contain("Smtp");
    }

    [Fact]
    public void TryUpdateSectionAtomic_StaleEtag_RejectsWithoutWriting()
    {
        var writer = NewWriter();
        writer.MutateAndWrite(root => root["Smtp"] = new JsonObject { ["Host"] = "v1" });
        var staleEtag = "\"stale-from-a-previous-load\"";

        var result = writer.TryUpdateSectionAtomic(
            "Smtp", staleEtag,
            new JsonObject { ["Host"] = "v2-would-clobber" },
            restartRequiredFor: null, savedBy: "admin", now: DateTimeOffset.UtcNow);

        result.Success.Should().BeFalse();
        result.CurrentSection!["Host"]!.GetValue<string>().Should().Be("v1",
            "the mismatch response must carry the actual on-disk snapshot so the caller can surface it without re-reading");
        var roundtrip = writer.ReadOrEmpty();
        roundtrip["Smtp"]!["Host"]!.GetValue<string>().Should().Be("v1",
            "rejected updates must not touch the file at all — otherwise a concurrent reader could see a partial write");
    }

    [Fact]
    public async Task TryUpdateSectionAtomic_TwoConcurrentSavesWithSameEtag_OnlyFirstSucceeds()
    {
        // The whole reason this method exists: an outer ETag check + a later write would
        // let both racers pass the check and the second clobber the first. The atomic
        // variant must serialise them so exactly one wins and the other reports the
        // mismatch with the freshly-written ETag.
        var writer = NewWriter();
        writer.MutateAndWrite(root => root["Smtp"] = new JsonObject { ["Host"] = "v0" });
        var sharedEtag = writer.ComputeSectionEtag("Smtp");

        var racer1 = Task.Run(() => writer.TryUpdateSectionAtomic(
            "Smtp", sharedEtag,
            new JsonObject { ["Host"] = "from-racer-1" },
            null, "admin", DateTimeOffset.UtcNow));
        var racer2 = Task.Run(() => writer.TryUpdateSectionAtomic(
            "Smtp", sharedEtag,
            new JsonObject { ["Host"] = "from-racer-2" },
            null, "admin", DateTimeOffset.UtcNow));

        var results = await Task.WhenAll(racer1, racer2);
        results.Count(r => r.Success).Should().Be(1,
            "exactly one racer must succeed; the other must observe the post-write ETag and reject");
        results.Count(r => !r.Success).Should().Be(1);

        var loser = results.First(r => !r.Success);
        loser.CurrentSection.Should().NotBeNull(
            "the rejected racer must receive the winning racer's persisted section so the UI can show a three-way diff");
    }

    [Fact]
    public void Constructor_EmptyPath_Throws()
    {
        var act = () => new RuntimeOverridesWriter(string.Empty, NullLogger<RuntimeOverridesWriter>.Instance);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void OverridesPath_ResolvedToAbsolute()
    {
        // Writer must internally resolve to an absolute path so file watchers + mutex
        // names are stable regardless of which directory the host was launched from.
        var rel = "rel-overrides-" + Guid.NewGuid().ToString("N") + ".json";
        var writer = new RuntimeOverridesWriter(rel, NullLogger<RuntimeOverridesWriter>.Instance);
        Path.IsPathRooted(writer.OverridesPath).Should().BeTrue();
    }
}

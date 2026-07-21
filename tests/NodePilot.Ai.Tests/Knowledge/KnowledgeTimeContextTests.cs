using FluentAssertions;
using NodePilot.Ai.Knowledge;
using Xunit;

namespace NodePilot.Ai.Tests.Knowledge;

public class KnowledgeTimeContextTests
{
    // A fixed July instant so the Europe/Berlin case is DST-stable (CEST = UTC+02:00).
    private static readonly DateTimeOffset NowUtc = new(2026, 7, 21, 14, 42, 0, TimeSpan.Zero);

    [Fact]
    public void Build_AlwaysStatesUtcNow()
    {
        var text = KnowledgeTimeContext.Build(NowUtc, null, null);
        text.Should().Contain("Jetzt (UTC): 2026-07-21T14:42:00Z");
        text.Should().Contain("get_next_scheduled_fires");
    }

    [Fact]
    public void Build_NoZoneInfo_OmitsLocalLine()
    {
        var text = KnowledgeTimeContext.Build(NowUtc, null, null);
        text.Should().NotContain("Lokalzeit des Users");
    }

    [Fact]
    public void Build_ValidIanaZone_RendersLocalTimeAndOffset()
    {
        var text = KnowledgeTimeContext.Build(NowUtc, "Europe/Berlin", 120);
        // CEST in July = UTC+02:00 → 16:42 local, honouring DST via the IANA zone.
        text.Should().Contain("Jetzt (Lokalzeit des Users): 2026-07-21 16:42 (Europe/Berlin, UTC+02:00)");
    }

    [Fact]
    public void Build_UnknownZone_FallsBackToOffsetMinutes()
    {
        var text = KnowledgeTimeContext.Build(NowUtc, "Mars/Olympus_Mons", 120);
        text.Should().Contain("Jetzt (Lokalzeit des Users): 2026-07-21 16:42 (UTC+02:00)");
        text.Should().NotContain("Mars/Olympus_Mons");
    }

    [Fact]
    public void Build_NegativeOffset_LabelsCorrectly()
    {
        // No IANA id → pure offset fallback, UTC-05:00 (e.g. US Eastern winter).
        var text = KnowledgeTimeContext.Build(NowUtc, null, -300);
        text.Should().Contain("Jetzt (Lokalzeit des Users): 2026-07-21 09:42 (UTC-05:00)");
    }

    [Fact]
    public void Build_ImplausibleOffset_IsIgnored()
    {
        // > 14h is not a real zone offset → treated as absent, UTC-only.
        var text = KnowledgeTimeContext.Build(NowUtc, null, 6000);
        text.Should().NotContain("Lokalzeit des Users");
    }
}

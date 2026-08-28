namespace NodePilot.Api.Dtos.Settings;

/// <summary>
/// What the process is actually sized to, and why.
///
/// <para>Deliberately separate from <c>SettingsSectionResponse.EffectiveSource</c>: that map
/// carries configuration <em>source names</em> ("runtime", "env") so the UI can grey out
/// env-locked fields. It has no place for the resolved numbers, and under automatic tuning the
/// numbers in the configuration file are not the ones in force — so the UI would otherwise show
/// values that do not match reality.</para>
///
/// <para><see cref="DesiredManualTuning"/> can differ from <see cref="ManualTuning"/>: the
/// switch is saved immediately but only takes effect on restart, because the runspace pool and
/// dispatch worker pool are built once at boot. The UI renders that difference as a restart
/// hint.</para>
/// </summary>
public sealed class EffectiveSizingDto
{
    /// <summary>Mode the process actually booted in.</summary>
    public required bool ManualTuning { get; init; }

    /// <summary>Mode currently saved in configuration — differs after a save until
    /// restart.</summary>
    public required bool DesiredManualTuning { get; init; }

    public required int ProcessorCount { get; init; }

    /// <summary>Detected usable memory, or null when detection failed (CPU-only sizing).</summary>
    public required long? UsableMemoryBytes { get; init; }

    /// <summary><c>Deployment:Mode=Desktop</c> — NodePilot then claims a smaller share of
    /// memory.</summary>
    public required bool IsDesktop { get; init; }

    public required IReadOnlyList<SizedValueDto> Values { get; init; }
}

/// <summary>One resolved knob: the configuration key, the value in force, and which constraint
/// produced it.</summary>
public sealed class SizedValueDto
{
    /// <summary>Configuration key, e.g. <c>Engine:Runspace:MaxRunspaces</c>.</summary>
    public required string Key { get; init; }

    public required int Value { get; init; }

    /// <summary><c>Cpu</c> | <c>Ram</c> | <c>Floor</c> | <c>Ceiling</c> | <c>Manual</c>.</summary>
    public required string Bound { get; init; }
}

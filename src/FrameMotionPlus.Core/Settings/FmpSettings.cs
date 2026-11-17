using System.Globalization;
using System.Text.Json.Serialization;
using FrameMotionPlus.Core.Abstractions;
using FrameMotionPlus.Core.Cadence;
using FrameMotionPlus.Core.Scheduling;
using FrameMotionPlus.Core.Time;

namespace FrameMotionPlus.Core.Settings;

/// <summary>User-editable settings, persisted as JSON. Durations are in milliseconds here for readability.</summary>
public sealed class FmpSettings
{
    public DisplaySettings Display { get; set; } = new();

    public SchedulerSettings Scheduler { get; set; } = new();

    public CadenceSettings Cadence { get; set; } = new();

    public GeneratorSettings Generator { get; set; } = new();

    public TelemetrySettings Telemetry { get; set; } = new();

    /// <summary>
    /// Returns human-readable problems, naming fields as they appear in the JSON; empty when the settings are usable.
    /// Never throws, also not for sections set to <see langword="null"/> in code.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        var count = errors.Count;
        if (Display is null)
        {
            errors.Add("display must not be null.");
        }
        else
        {
            Display.Validate(errors);
        }

        var displayValid = errors.Count == count;
        if (Scheduler is null)
        {
            errors.Add("scheduler must not be null.");
        }
        else
        {
            count = errors.Count;
            Scheduler.Validate(errors);
            if (errors.Count == count && displayValid)
            {
                // Cross-field rules of the scheduler itself, once every field is in range.
                AddIfThrows(errors, () => Scheduler.ToOptions(Display!));
            }
        }

        if (Cadence is null)
        {
            errors.Add("cadence must not be null.");
        }
        else
        {
            count = errors.Count;
            Cadence.Validate(errors);
            if (errors.Count == count)
            {
                AddIfThrows(errors, () => Cadence.ToOptions().Validate());
            }
        }

        if (Generator is null)
        {
            errors.Add("generator must not be null.");
        }
        else
        {
            Generator.Validate(errors);
        }

        if (Telemetry is null)
        {
            errors.Add("telemetry must not be null.");
        }
        else
        {
            Telemetry.Validate(errors);
        }

        return errors;
    }

    private static void AddIfThrows(List<string> errors, Action action)
    {
        try
        {
            action();
        }
        catch (ArgumentException e)
        {
            errors.Add(e.Message.ReplaceLineEndings(" ").Trim());
        }
    }
}

/// <summary>Range checks shared by the settings sections. Comparisons are written so NaN fails them.</summary>
internal static class SettingsRules
{
    public static void Range(List<string> errors, string name, double value, double min, double max)
    {
        if (!(value >= min && value <= max))
        {
            errors.Add(string.Create(CultureInfo.InvariantCulture, $"{name} must be between {min} and {max} (got {value})."));
        }
    }

    public static void Range(List<string> errors, string name, long value, long min, long max)
    {
        if (value < min || value > max)
        {
            errors.Add(string.Create(CultureInfo.InvariantCulture, $"{name} must be between {min} and {max} (got {value})."));
        }
    }

    public static void Defined<TEnum>(List<string> errors, string name, TEnum value)
        where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            errors.Add($"{name} must be one of {string.Join(", ", Enum.GetNames<TEnum>())} (got {value}).");
        }
    }
}

public sealed class DisplaySettings
{
    public const double MinRefreshHz = 10;
    public const double MaxRefreshHz = 1000;

    /// <summary>Fixed refresh rate, or the maximum refresh rate under VRR. NTSC rates such as 119.88 are recognised.</summary>
    public double RefreshHz { get; set; } = 120;

    /// <summary>Lowest refresh rate the panel holds under VRR; at most <see cref="RefreshHz"/>.</summary>
    public double VrrMinimumHz { get; set; } = 48;

    internal void Validate(List<string> errors)
    {
        SettingsRules.Range(errors, "display.refreshHz", RefreshHz, MinRefreshHz, MaxRefreshHz);
        var vrrMax = RefreshHz >= MinRefreshHz && RefreshHz <= MaxRefreshHz ? RefreshHz : MaxRefreshHz;
        SettingsRules.Range(errors, "display.vrrMinimumHz", VrrMinimumHz, 1, vrrMax);
    }
}

public sealed class SchedulerSettings
{
    [JsonConverter(typeof(EnumNameConverter<ClockMode>))]
    public ClockMode ClockMode { get; set; } = ClockMode.FixedRefresh;

    public int MaxMultiplier { get; set; } = 8;

    public double SnapEpsilon { get; set; } = 0.02;

    public double MarginMs { get; set; } = 0.5;

    public double InitialGenerationTimeMs { get; set; } = 2.0;

    public double GenerationTimePercentile { get; set; } = 0.99;

    public double SourceIntervalPercentile { get; set; } = 0.9;

    public double HoldIntervalFactor { get; set; } = 2.5;

    public double SlewUpMsPerSecond { get; set; } = 50;

    public double SlewDownMsPerSecond { get; set; } = 5;

    public double FallbackHeadroom { get; set; } = 0.1;

    public double FallbackDwellMs { get; set; } = 1000;

    internal void Validate(List<string> errors)
    {
        SettingsRules.Defined(errors, "scheduler.clockMode", ClockMode);
        SettingsRules.Range(errors, "scheduler.maxMultiplier", MaxMultiplier, 1, 8);
        SettingsRules.Range(errors, "scheduler.snapEpsilon", SnapEpsilon, 0, 0.25);
        SettingsRules.Range(errors, "scheduler.marginMs", MarginMs, 0, 100);
        SettingsRules.Range(errors, "scheduler.initialGenerationTimeMs", InitialGenerationTimeMs, 0, 1000);
        SettingsRules.Range(errors, "scheduler.generationTimePercentile", GenerationTimePercentile, 0.5, 1);
        SettingsRules.Range(errors, "scheduler.sourceIntervalPercentile", SourceIntervalPercentile, 0.5, 1);
        SettingsRules.Range(errors, "scheduler.holdIntervalFactor", HoldIntervalFactor, 1.5, 10);
        SettingsRules.Range(errors, "scheduler.slewUpMsPerSecond", SlewUpMsPerSecond, 0.01, 1000);
        SettingsRules.Range(errors, "scheduler.slewDownMsPerSecond", SlewDownMsPerSecond, 0.01, 1000);
        SettingsRules.Range(errors, "scheduler.fallbackHeadroom", FallbackHeadroom, 0, 0.9);
        SettingsRules.Range(errors, "scheduler.fallbackDwellMs", FallbackDwellMs, 0, 60_000);
    }

    public SchedulerOptions ToOptions(DisplaySettings display)
    {
        ArgumentNullException.ThrowIfNull(display);
        var options = new SchedulerOptions
        {
            ClockMode = ClockMode,
            DisplayRate = FrameRate.FromDouble(display.RefreshHz),
            MaxMultiplier = MaxMultiplier,
            SnapEpsilon = SnapEpsilon,
            MarginNs = Nanoseconds.FromMilliseconds(MarginMs),
            InitialGenerationTimeNs = Nanoseconds.FromMilliseconds(InitialGenerationTimeMs),
            GenerationTimePercentile = GenerationTimePercentile,
            SourceIntervalPercentile = SourceIntervalPercentile,
            HoldIntervalFactor = HoldIntervalFactor,
            SlewUpNsPerSecond = Nanoseconds.FromMilliseconds(SlewUpMsPerSecond),
            SlewDownNsPerSecond = Nanoseconds.FromMilliseconds(SlewDownMsPerSecond),
            FallbackHeadroom = FallbackHeadroom,
            FallbackDwellNs = Nanoseconds.FromMilliseconds(FallbackDwellMs),
        };
        options.Validate();
        return options;
    }
}

public sealed class CadenceSettings
{
    public double SdrLow { get; set; } = 0.002;

    public double SdrHigh { get; set; } = 0.010;

    public double PqLow { get; set; } = 0.003;

    public double PqHigh { get; set; } = 0.015;

    public int RateWindow { get; set; } = 32;

    internal void Validate(List<string> errors)
    {
        // The metric is normalised to [0, 1] of full-scale code values.
        SettingsRules.Range(errors, "cadence.sdrLow", SdrLow, 0, 1);
        SettingsRules.Range(errors, "cadence.sdrHigh", SdrHigh, 0, 1);
        SettingsRules.Range(errors, "cadence.pqLow", PqLow, 0, 1);
        SettingsRules.Range(errors, "cadence.pqHigh", PqHigh, 0, 1);
        if (SdrLow >= SdrHigh)
        {
            errors.Add(string.Create(CultureInfo.InvariantCulture, $"cadence.sdrLow ({SdrLow}) must be below cadence.sdrHigh ({SdrHigh})."));
        }

        if (PqLow >= PqHigh)
        {
            errors.Add(string.Create(CultureInfo.InvariantCulture, $"cadence.pqLow ({PqLow}) must be below cadence.pqHigh ({PqHigh})."));
        }

        SettingsRules.Range(errors, "cadence.rateWindow", RateWindow, 4, 1024);
    }

    public CadenceOptions ToOptions() => new()
    {
        Sdr = new CadenceThresholds(SdrLow, SdrHigh),
        Pq = new CadenceThresholds(PqLow, PqHigh),
        RateWindow = RateWindow,
    };
}

public sealed class GeneratorSettings
{
    /// <summary>Accepted values of <see cref="Backend"/>.</summary>
    public static IReadOnlyList<string> KnownBackends { get; } = ["vfg", "repeat", "blend"];

    /// <summary>Generator backend name: "vfg", "repeat" or "blend".</summary>
    public string Backend { get; set; } = "vfg";

    [JsonConverter(typeof(EnumNameConverter<FrameGenQuality>))]
    public FrameGenQuality Quality { get; set; } = FrameGenQuality.Medium;

    /// <summary>
    /// Directory of the NVIDIA VFX SDK (the one holding lib/ and features/), which is never shipped with the app; null to
    /// look in $FMP_VFX_SDK, a repository checkout's lib/VideoFX and then the installer's /usr/local/VideoFX.
    /// </summary>
    public string? VfxSdkDirectory { get; set; }

    internal void Validate(List<string> errors)
    {
        if (VfxSdkDirectory is not null && string.IsNullOrWhiteSpace(VfxSdkDirectory))
        {
            errors.Add("generator.vfxSdkDirectory must be a directory or null (got an empty string).");
        }

        var known = false;
        foreach (var name in KnownBackends)
        {
            known |= string.Equals(name, Backend, StringComparison.Ordinal);
        }

        if (!known)
        {
            errors.Add($"generator.backend must be one of {string.Join(", ", KnownBackends)} (got {(Backend is null ? "null" : $"'{Backend}'")}).");
        }

        SettingsRules.Defined(errors, "generator.quality", Quality);
    }
}

public sealed class TelemetrySettings
{
    public const int MaxRingCapacity = 1 << 16;

    public bool Enabled { get; set; }

    public string? CsvPath { get; set; }

    public int RingCapacity { get; set; } = 4096;

    internal void Validate(List<string> errors)
    {
        if (RingCapacity < 2 || RingCapacity > MaxRingCapacity || (RingCapacity & (RingCapacity - 1)) != 0)
        {
            errors.Add(string.Create(CultureInfo.InvariantCulture, $"telemetry.ringCapacity must be a power of two from 2 to {MaxRingCapacity} (got {RingCapacity})."));
        }

        if (CsvPath is not null && string.IsNullOrWhiteSpace(CsvPath))
        {
            errors.Add("telemetry.csvPath must be a file path or absent, not empty.");
        }
    }
}

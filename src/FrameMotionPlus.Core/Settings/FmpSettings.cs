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
    /// <summary>The settings file format this build writes (<see cref="Version"/>).</summary>
    public const int CurrentVersion = 1;

    /// <summary>The file format's version; 1 (files from before it had none and read as 1).</summary>
    public int Version { get; set; } = CurrentVersion;

    /// <summary><c>fmp capture</c>'s device and mode (null: not set, the command line's default).</summary>
    public CaptureSettings Capture { get; set; } = new();

    /// <summary><c>fmp capture</c>'s sound (null values: not set).</summary>
    public AudioSettings Audio { get; set; } = new();

    /// <summary><c>fmp capture</c>'s HDR handling and metadata (null values: not set).</summary>
    public HdrSettings Hdr { get; set; } = new();

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
        if (Version < 1 || Version > CurrentVersion)
        {
            errors.Add(string.Create(CultureInfo.InvariantCulture, $"version must be 1 to {CurrentVersion} (got {Version}): a newer fmp wrote this file."));
        }

        if (Capture is null || Audio is null || Hdr is null)
        {
            errors.Add($"{(Capture is null ? "capture" : Audio is null ? "audio" : "hdr")} must not be null.");
        }
        else
        {
            SettingsRules.NotBlank(errors, "capture.device", Capture.Device);
            SettingsRules.NotBlank(errors, "capture.mode", Capture.Mode);
            SettingsRules.NotBlank(errors, "audio.mode", Audio.Mode);
            SettingsRules.NotBlank(errors, "audio.sink", Audio.Sink);
            SettingsRules.NotBlank(errors, "audio.source", Audio.Source);
            SettingsRules.NotBlank(errors, "hdr.mode", Hdr.Mode);
        }

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

    public static void NotBlank(List<string> errors, string name, string? value)
    {
        if (value is not null && string.IsNullOrWhiteSpace(value))
        {
            errors.Add($"{name} must be a value or absent, not empty.");
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

    /// <summary><c>--clock</c> of fmp capture and fmp play: auto, fixed or vrr (null: not set).</summary>
    public string? Clock { get; set; }

    /// <summary>true: <c>--fullscreen</c>; false: a window (<c>--windowed</c>); null: the command's own default.</summary>
    public bool? Fullscreen { get; set; }

    /// <summary>The window's size when not fullscreen, WIDTHxHEIGHT (<c>--window</c>; null: not set).</summary>
    public string? Window { get; set; }

    internal void Validate(List<string> errors)
    {
        SettingsRules.Range(errors, "display.refreshHz", RefreshHz, MinRefreshHz, MaxRefreshHz);
        var vrrMax = RefreshHz >= MinRefreshHz && RefreshHz <= MaxRefreshHz ? RefreshHz : MaxRefreshHz;
        SettingsRules.Range(errors, "display.vrrMinimumHz", VrrMinimumHz, 1, vrrMax);
        SettingsRules.NotBlank(errors, "display.clock", Clock);
        SettingsRules.NotBlank(errors, "display.window", Window);
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
    public static IReadOnlyList<string> KnownBackends { get; } = ["vfg", "repeat", "blend", "auto", "none"];

    /// <summary>
    /// Generator backend name: "vfg", "repeat", "blend", "auto" (VFG when the SDK is there) or "none". The default is the
    /// command line's (<c>--generator auto</c>), so the defaults <c>fmp settings</c> prints, used as a settings file, run as no file.
    /// </summary>
    public string Backend { get; set; } = "auto";

    /// <summary><c>--multiplier</c> of fmp capture and fmp play, 1 to 8 (null: not set).</summary>
    public int? Multiplier { get; set; }

    [JsonConverter(typeof(EnumNameConverter<FrameGenQuality>))]
    public FrameGenQuality Quality { get; set; } = FrameGenQuality.Medium;

    /// <summary>
    /// <c>--quality-switch</c> (true) or <c>--no-quality-switch</c> (false) of fmp capture and fmp play: VFG loads the other
    /// quality's instance at start so Q switches between the two live (null: not set, the build's default).
    /// </summary>
    public bool? QualitySwitch { get; set; }

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
        if (Multiplier is { } m)
        {
            SettingsRules.Range(errors, "generator.multiplier", m, 1, 8);
        }
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

/// <summary><c>fmp capture</c>'s device and mode. Null: not set (the command line's own default applies).</summary>
public sealed class CaptureSettings
{
    /// <summary>The V4L2 device, /dev/videoN.</summary>
    public string? Device { get; set; }

    /// <summary>A <c>--mode</c> preset such as 4k30-hdr.</summary>
    public string? Mode { get; set; }
}

/// <summary><c>fmp capture</c>'s sound. Null: not set. Values are checked as the command line checks them.</summary>
public sealed class AudioSettings
{
    /// <summary><c>--audio</c>: on, off or auto.</summary>
    public string? Mode { get; set; }

    /// <summary><c>--audio-sink</c>: a sink's node name or part of its name or description.</summary>
    public string? Sink { get; set; }

    /// <summary><c>--audio-source</c>: a PipeWire source's node name.</summary>
    public string? Source { get; set; }

    /// <summary><c>--audio-offset-ms</c>: trim on top of the measured video latency.</summary>
    public double? OffsetMs { get; set; }
}

/// <summary><c>fmp capture</c>'s HDR handling and the metadata it sends. Null: not set.</summary>
public sealed class HdrSettings
{
    /// <summary><c>--hdr</c>: follow, on, off, auto or 10bit.</summary>
    public string? Mode { get; set; }

    /// <summary><c>--hdr-peak-nits</c>.</summary>
    public int? PeakNits { get; set; }

    /// <summary><c>--hdr-max-cll</c>.</summary>
    public int? MaxCll { get; set; }

    /// <summary><c>--hdr-max-fall</c>.</summary>
    public int? MaxFall { get; set; }
}

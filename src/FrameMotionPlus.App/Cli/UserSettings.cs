using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FrameMotionPlus.App.Capture;
using FrameMotionPlus.Core.Abstractions;
using FrameMotionPlus.Core.Settings;

namespace FrameMotionPlus.App.Cli;

/// <summary>Which command a settings file is applied to.</summary>
internal enum SettingsTarget
{
    Capture = 0,
    Play = 1,
}

/// <summary>How a saved value is stored in the JSON file.</summary>
internal enum SettingKind
{
    Text = 0,
    Integer = 1,
    Number = 2,
    Boolean = 3,
}

/// <summary>
/// One saved setting: its dotted key, the command-line option it stands for (checked exactly as that option is), how it
/// is read from <see cref="FmpSettings"/>, and what applies when it is not set.
/// </summary>
internal sealed record SettingKey(string Name, string Option, SettingKind Kind, bool CaptureOnly, string Default, Func<FmpSettings, string?> Read);

/// <summary>A settings file as loaded for a command: where from, what it sets, what this build did not know.</summary>
internal sealed record LoadedSettings(string? Path, bool Exists, bool Ignored, FmpSettings Settings, IReadOnlyDictionary<string, string> Values, IReadOnlyList<string> Unknown)
{
    public static LoadedSettings None(string? path, bool ignored) =>
        new(path, false, ignored, new FmpSettings(), new Dictionary<string, string>(StringComparer.Ordinal), []);
}

/// <summary>The command line after the settings file was applied, and the account of it for the start-up line.</summary>
internal sealed record AppliedSettings(string[] Args, IReadOnlyList<string> Applied, IReadOnlyList<string> Overridden);

/// <summary>
/// Saved settings: a JSON file at <c>$XDG_CONFIG_HOME/fmp/settings.json</c> (else <c>~/.config/fmp/settings.json</c>; on
/// Windows <c>%APPDATA%\fmp\settings.json</c>), read by <c>fmp capture</c> and <c>fmp play</c> when it exists.
/// Precedence: built-in defaults &lt; the file &lt; the command line. A saved value is turned into the option it stands for
/// and given to the command's own parser, so it is checked exactly as typed options are; an option given on the command
/// line (or one that conflicts with it) wins and the saved value is not used. <c>--settings &lt;file&gt;</c> reads another
/// file, <c>--no-settings</c> none. A missing file is normal; a malformed one is one error naming the file and the position;
/// a key this build does not know is reported and skipped.
/// </summary>
internal static class UserSettings
{
    private static readonly CultureInfo Ic = CultureInfo.InvariantCulture;

    /// <summary>Every saved setting, in the order <c>fmp settings show</c> lists them.</summary>
    public static readonly IReadOnlyList<SettingKey> Keys =
    [
        new("capture.device", "(device)", SettingKind.Text, true, "the first capture device with a 10-bit mode", s => s.Capture.Device),
        new("capture.mode", "--mode", SettingKind.Text, true, "the device's largest 10-bit mode", s => s.Capture.Mode),
        new("generator.backend", "--generator", SettingKind.Text, false, "auto", s => s.Generator.Backend),
        new("generator.quality", "--quality", SettingKind.Text, false, "medium", s => s.Generator.Quality.ToString().ToLowerInvariant()),
        new("generator.multiplier", "--multiplier", SettingKind.Integer, false, "2", s => s.Generator.Multiplier?.ToString(Ic)),
        new("generator.qualitySwitch", "--quality-switch", SettingKind.Boolean, false, Play.PlayOptions.QualitySwitchDefault ? "true" : "false", s => s.Generator.QualitySwitch is { } q ? (q ? "true" : "false") : null),
        new("display.clock", "--clock", SettingKind.Text, false, "auto", s => s.Display.Clock),
        new("display.fullscreen", "--fullscreen", SettingKind.Boolean, false, "capture: true, play: false", s => s.Display.Fullscreen is { } f ? (f ? "true" : "false") : null),
        new("display.window", "--window", SettingKind.Text, false, "1280x720", s => s.Display.Window),
        new("audio.mode", "--audio", SettingKind.Text, true, "auto", s => s.Audio.Mode),
        new("audio.sink", "--audio-sink", SettingKind.Text, true, "your selected default output, followed", s => s.Audio.Sink),
        new("audio.source", "--audio-source", SettingKind.Text, true, "the capture device's own", s => s.Audio.Source),
        new("audio.offsetMs", "--audio-offset-ms", SettingKind.Number, true, "0", s => s.Audio.OffsetMs?.ToString("0.###", Ic)),
        new("hdr.mode", "--hdr", SettingKind.Text, true, "follow", s => s.Hdr.Mode),
        new("hdr.peakNits", "--hdr-peak-nits", SettingKind.Integer, true, "1000", s => s.Hdr.PeakNits?.ToString(Ic)),
        new("hdr.maxCll", "--hdr-max-cll", SettingKind.Integer, true, "the peak", s => s.Hdr.MaxCll?.ToString(Ic)),
        new("hdr.maxFall", "--hdr-max-fall", SettingKind.Integer, true, "0", s => s.Hdr.MaxFall?.ToString(Ic)),
        new("generator.vfxSdkDirectory", "--vfx-sdk", SettingKind.Text, false, "$FMP_VFX_SDK, lib/VideoFX of a checkout, /usr/local/VideoFX", s => s.Generator.VfxSdkDirectory),
    ];

    /// <summary>The default file, or null when no home or config directory is known.</summary>
    public static string? DefaultPath()
    {
        if (OperatingSystem.IsWindows())
        {
            var appData = Environment.GetEnvironmentVariable("APPDATA");
            return string.IsNullOrEmpty(appData) ? null : Path.Combine(appData, "fmp", "settings.json");
        }

        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (!string.IsNullOrEmpty(xdg) && Path.IsPathRooted(xdg))
        {
            return Path.Combine(xdg, "fmp", "settings.json");
        }

        var home = Environment.GetEnvironmentVariable("HOME");
        return string.IsNullOrEmpty(home) ? null : Path.Combine(home, ".config", "fmp", "settings.json");
    }

    public static SettingKey Find(string name) =>
        Keys.FirstOrDefault(k => string.Equals(k.Name, name, StringComparison.Ordinal))
        ?? throw new UsageException($"Unknown setting '{name}'; the settings are: {string.Join(", ", Keys.Select(k => k.Name))}.");

    /// <summary>
    /// Takes <c>--no-settings</c> out of <paramref name="args"/> and loads the file the command uses: <c>--settings</c>'s
    /// (left in the arguments for the command's parser), else the default one if it exists.
    /// </summary>
    public static LoadedSettings LoadFor(string[] args, out string[] rest)
    {
        var none = args.Contains("--no-settings", StringComparer.Ordinal);
        rest = [.. args.Where(a => a != "--no-settings")];
        var explicitPath = OptionValue(rest, "settings");
        if (none && explicitPath is not null)
        {
            throw new UsageException("Give --settings <file> or --no-settings, not both.");
        }

        if (none)
        {
            return LoadedSettings.None(DefaultPath(), ignored: true);
        }

        return explicitPath is not null ? Load(explicitPath, mustExist: true) : Load(DefaultPath(), mustExist: false);
    }

    /// <summary>Loads a settings file leniently (unknown keys reported), validating every saved value as its option is.</summary>
    public static LoadedSettings Load(string? path, bool mustExist)
    {
        if (path is null || (!mustExist && !File.Exists(path)))
        {
            return LoadedSettings.None(path, ignored: false);
        }

        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new UsageException($"Cannot read settings '{path}': {e.Message}", e);
        }

        var present = new List<string>();
        var unknown = new List<string>();
        FmpSettings settings;
        try
        {
            settings = FmpSettingsSerializer.DeserializeLenient(text, present, unknown);
        }
        catch (JsonException e)
        {
            throw new UsageException($"Invalid settings '{path}': {Describe(e)}", e);
        }

        var errors = settings.Validate();
        if (errors.Count > 0)
        {
            throw new UsageException($"Invalid settings '{path}': {string.Join(" ", errors)}");
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in Keys)
        {
            if (present.Contains(key.Name) && key.Read(settings) is { } value)
            {
                try
                {
                    Check(key, value);
                }
                catch (UsageException e)
                {
                    throw new UsageException($"Invalid settings '{path}': {key.Name}: {e.Message}", e);
                }

                values[key.Name] = value;
            }
        }

        return new LoadedSettings(path, true, false, settings, values, unknown);
    }

    /// <summary>
    /// Checks one value exactly as the command line checks its option (the option parsed alone by <c>fmp capture</c>'s
    /// parser); throws <see cref="UsageException"/> with the parser's message. Returns the value as it is saved.
    /// </summary>
    public static string Check(SettingKey key, string value)
    {
        switch (key.Name)
        {
            case "capture.device":
            case "generator.vfxSdkDirectory":
                // A saved value is handed to the option parser: one that starts with a dash would be read as an option.
                return string.IsNullOrWhiteSpace(value) || value.StartsWith('-')
                    ? throw new UsageException($"'{key.Name}' needs a path (not one starting with '-').")
                    : value;
            case "display.fullscreen":
                return value.ToLowerInvariant() switch
                {
                    "true" or "on" or "yes" or "1" => "true",
                    "false" or "off" or "no" or "0" => "false",
                    _ => throw new UsageException($"'display.fullscreen' expects true (fullscreen) or false (a window), got '{value}'."),
                };
            case "generator.qualitySwitch":
                return value.ToLowerInvariant() switch
                {
                    "true" or "on" or "yes" or "1" => "true",
                    "false" or "off" or "no" or "0" => "false",
                    _ => throw new UsageException($"'generator.qualitySwitch' expects true (load both VFG qualities at start, Q switches) or false (one instance), got '{value}'."),
                };
            case "display.window":
                // --window also chooses a window on the command line; here it is only the size.
                _ = CaptureOptions.Parse(["--window", value]);
                return value.ToLowerInvariant();
            case "generator.backend":
                _ = CaptureOptions.Parse(["--generator", value]);
                return value.ToLowerInvariant() is "off" ? "none" : value.ToLowerInvariant();
            case "generator.quality":
                _ = Vfg.VfgBenchOptions.ParseMode(value);
                return value.ToLowerInvariant();
            case "audio.mode":
                _ = CaptureOptions.Parse(["--audio", value]);
                return value.ToLowerInvariant();
            case "audio.sink":
            case "audio.source":
                _ = CaptureOptions.Parse([key.Option, value]);
                return value;
            default:
                _ = CaptureOptions.Parse([key.Option, value]);
                return key.Kind == SettingKind.Text ? value.ToLowerInvariant() : value;
        }
    }

    /// <summary>
    /// The command line with the saved values added: each one becomes its option unless the command line gives that option
    /// or one that conflicts with it (then the command line wins: built-in defaults &lt; file &lt; command line).
    /// </summary>
    public static AppliedSettings Apply(SettingsTarget target, string[] args, LoadedSettings loaded)
    {
        var values = loaded.Values;
        if (values.Count == 0)
        {
            return new AppliedSettings(args, [], []);
        }

        var given = new HashSet<string>(StringComparer.Ordinal);
        var positional = false;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal))
            {
                var eq = args[i].IndexOf('=', StringComparison.Ordinal);
                given.Add(eq > 0 ? args[i][2..eq] : args[i][2..]);
            }
            else if (i == 0)
            {
                positional = true;
            }
        }

        var applied = new List<string>();
        var overridden = new List<string>();
        var front = new List<string>();
        var back = new List<string>();
        if (given.Contains("list"))
        {
            return new AppliedSettings(args, [], [.. values.Keys.Select(k => $"{k} (not used with --list)")]);
        }

        string? Value(string key) => values.TryGetValue(key, out var v) ? v : null;
        bool Any(params string[] options) => options.Any(given.Contains);
        void Use(string key, params string[] option)
        {
            applied.Add($"{key}={values[key]}");
            back.AddRange(option);
        }

        void Skip(string key, string why) => overridden.Add($"{key} ({why})");

        var capture = target == SettingsTarget.Capture;
        if (capture && Value("capture.device") is { } device)
        {
            if (positional)
            {
                Skip("capture.device", "a device was given");
            }
            else
            {
                applied.Add($"capture.device={device}");
                front.Add(device);
            }
        }

        if (capture && Value("capture.mode") is { } mode)
        {
            if (Any("mode", "size", "fps", "format"))
            {
                Skip("capture.mode", "--mode/--size/--fps/--format given");
            }
            else
            {
                Use("capture.mode", "--mode", mode);
            }
        }

        var gate = !capture && given.Contains("gate");
        foreach (var (key, option, conflicts) in new (string, string, string[])[]
        {
            ("generator.backend", "--generator", ["generator"]),
            ("generator.quality", "--quality", ["quality"]),
            ("generator.multiplier", "--multiplier", ["multiplier", "output-hz"]),
            ("display.clock", "--clock", ["clock"]),
        })
        {
            if (Value(key) is not { } v)
            {
                continue;
            }

            if (gate && key.StartsWith("generator.", StringComparison.Ordinal))
            {
                Skip(key, "--gate runs VFG Medium at 2x");
            }
            else if (Any(conflicts))
            {
                Skip(key, $"--{conflicts[0]} given");
            }
            else
            {
                Use(key, option, v);
            }
        }

        if (Value("generator.qualitySwitch") is { } qualitySwitch)
        {
            if (gate)
            {
                Skip("generator.qualitySwitch", "--gate runs VFG Medium at 2x");
            }
            else if (Any("quality-switch", "no-quality-switch"))
            {
                Skip("generator.qualitySwitch", "--quality-switch/--no-quality-switch given");
            }
            else
            {
                Use("generator.qualitySwitch", qualitySwitch == "true" ? "--quality-switch" : "--no-quality-switch");
            }
        }

        // Fullscreen or a window, and the window's size (which, saved, does not by itself choose a window).
        var offscreen = given.Contains("offscreen");
        var fullscreenGiven = Any("fullscreen", "windowed", "window", "offscreen");
        var windowed = capture ? given.Contains("windowed") || given.Contains("window") : !given.Contains("fullscreen");
        if (Value("display.fullscreen") is { } fullscreen)
        {
            if (fullscreenGiven)
            {
                Skip("display.fullscreen", offscreen ? "--offscreen given" : "--fullscreen/--windowed/--window given");
            }
            else if (fullscreen == "true")
            {
                Use("display.fullscreen", "--fullscreen");
                windowed = false;
            }
            else
            {
                if (capture)
                {
                    Use("display.fullscreen", "--windowed");
                }
                else
                {
                    applied.Add("display.fullscreen=false");
                }

                windowed = true;
            }
        }

        if (Value("display.window") is { } window)
        {
            if (Any("window", "offscreen"))
            {
                Skip("display.window", offscreen ? "--offscreen given" : "--window given");
            }
            else if (!windowed)
            {
                Skip("display.window", "fullscreen");
            }
            else
            {
                Use("display.window", "--window", window);
            }
        }

        if (capture)
        {
            var audioOff = string.Equals(OptionValue(args, "audio"), "off", StringComparison.OrdinalIgnoreCase);
            if (Value("audio.mode") is { } audio)
            {
                if (given.Contains("audio"))
                {
                    Skip("audio.mode", "--audio given");
                }
                else if (audio == "off" && Any("audio-source", "audio-sink", "audio-offset-ms"))
                {
                    Skip("audio.mode", "--audio-source/--audio-sink/--audio-offset-ms given, which turn it on");
                }
                else
                {
                    Use("audio.mode", "--audio", audio);
                    audioOff = audio == "off";
                }
            }

            foreach (var (key, option) in new[] { ("audio.sink", "audio-sink"), ("audio.source", "audio-source"), ("audio.offsetMs", "audio-offset-ms") })
            {
                if (Value(key) is not { } v)
                {
                    continue;
                }

                if (given.Contains(option))
                {
                    Skip(key, $"--{option} given");
                }
                else if (audioOff)
                {
                    Skip(key, "audio is off");
                }
                else
                {
                    Use(key, "--" + option, v);
                }
            }

            foreach (var (key, option) in new[] { ("hdr.mode", "hdr"), ("hdr.peakNits", "hdr-peak-nits"), ("hdr.maxCll", "hdr-max-cll"), ("hdr.maxFall", "hdr-max-fall") })
            {
                if (Value(key) is not { } v)
                {
                    continue;
                }

                if (given.Contains(option))
                {
                    Skip(key, $"--{option} given");
                }
                else
                {
                    Use(key, "--" + option, v);
                }
            }
        }

        if (Value("generator.vfxSdkDirectory") is { } sdk)
        {
            // Not an option added: the command reads it from the settings (after --vfx-sdk and $FMP_VFX_SDK).
            if (given.Contains("vfx-sdk"))
            {
                Skip("generator.vfxSdkDirectory", "--vfx-sdk given");
            }
            else
            {
                applied.Add($"generator.vfxSdkDirectory={sdk}");
            }
        }

        string[] result = capture ? [.. front, .. args, .. back] : [.. args, .. back];
        return new AppliedSettings(result, applied, overridden);
    }

    /// <summary>The start-up line: which file, and every value taken from it (nothing is applied unseen).</summary>
    public static string Describe(LoadedSettings loaded, AppliedSettings applied)
    {
        if (loaded.Ignored)
        {
            return "  settings    none (--no-settings)";
        }

        if (!loaded.Exists)
        {
            return $"  settings    none ({(loaded.Path is null ? "no config directory known" : $"no file at {loaded.Path}")}; fmp settings set <key> <value> saves one)";
        }

        var used = applied.Applied.Count == 0 ? "no value used from it" : string.Join(", ", applied.Applied);
        var over = applied.Overridden.Count == 0 ? "" : $"; not used: {string.Join(", ", applied.Overridden)}";
        var unknown = loaded.Unknown.Count == 0 ? "" : $"; unknown keys skipped: {string.Join(", ", loaded.Unknown)}";
        return $"  settings    {loaded.Path}: {used}{over}{unknown}";
    }

    /// <summary><c>fmp settings set</c>: checks the value as its option is, then writes it into the file (created with its
    /// directory if need be), keeping every other key, atomically (a temporary file renamed over it).</summary>
    public static string Set(string path, string name, string value)
    {
        var key = Find(name);
        string saved;
        try
        {
            saved = Check(key, value);
        }
        catch (UsageException e)
        {
            throw new UsageException($"{key.Name}: {e.Message}", e);
        }

        var root = ReadNode(path);
        var parts = key.Name.Split('.');
        if (root[parts[0]] is not JsonObject section)
        {
            section = [];
            root[parts[0]] = section;
        }

        section[parts[1]] = key.Kind switch
        {
            SettingKind.Integer => JsonValue.Create(long.Parse(saved, NumberStyles.Integer, Ic)),
            SettingKind.Number => JsonValue.Create(double.Parse(saved, NumberStyles.Float, Ic)),
            SettingKind.Boolean => JsonValue.Create(saved == "true"),
            _ => key.Name == "generator.quality" ? JsonValue.Create(QualityName(saved)) : JsonValue.Create(saved),
        };
        if (root["version"] is null)
        {
            root["version"] = JsonValue.Create(FmpSettings.CurrentVersion);
        }

        Write(path, root);
        return saved;
    }

    /// <summary><c>fmp settings unset</c>: removes the key (and its section once empty); false when it was not set.</summary>
    public static bool Unset(string path, string name)
    {
        var key = Find(name);
        if (!File.Exists(path))
        {
            return false;
        }

        var root = ReadNode(path);
        var parts = key.Name.Split('.');
        if (root[parts[0]] is not JsonObject section || !section.Remove(parts[1]))
        {
            return false;
        }

        if (section.Count == 0)
        {
            _ = root.Remove(parts[0]);
        }

        Write(path, root);
        return true;
    }

    private static JsonObject ReadNode(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        // The whole file must still load (a malformed one is reported, never overwritten).
        _ = Load(path, mustExist: true);
        var text = File.ReadAllText(path);
        try
        {
            return JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
                ?? throw new UsageException($"Invalid settings '{path}': not a JSON object.");
        }
        catch (JsonException e)
        {
            throw new UsageException($"Invalid settings '{path}': {Describe(e)}", e);
        }
    }

    private static void Write(string path, JsonObject root)
    {
        var temporary = $"{path}.tmp-{Environment.ProcessId}";
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using (var stream = File.Create(temporary))
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                root.WriteTo(writer);
            }

            File.AppendAllText(temporary, "\n", Encoding.UTF8);
            _ = Load(temporary, mustExist: true); // what is written must load
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new UsageException($"Cannot write settings '{path}': {e.Message}", e);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static string QualityName(string quality) => quality switch
    {
        "low" => nameof(FrameGenQuality.Low),
        "high" => nameof(FrameGenQuality.High),
        _ => nameof(FrameGenQuality.Medium),
    };

    /// <summary>The value of <c>--name value</c> or <c>--name=value</c> in <paramref name="args"/>, or null.</summary>
    private static string? OptionValue(string[] args, string name)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--" + name && i + 1 < args.Length)
            {
                return args[i + 1];
            }

            if (args[i].StartsWith("--" + name + "=", StringComparison.Ordinal))
            {
                return args[i][(name.Length + 3)..];
            }
        }

        return null;
    }

    /// <summary>One line; adds the JSON path when the message does not already carry it.</summary>
    internal static string Describe(JsonException e)
    {
        var message = e.Message.ReplaceLineEndings(" ").Trim();
        return e.Path is { Length: > 0 } p && !message.Contains(p, StringComparison.Ordinal) ? $"{message} Path: {p}." : message;
    }
}

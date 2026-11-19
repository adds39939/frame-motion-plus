using System.Text.Json;
using FrameMotionPlus.Core.Settings;

namespace FrameMotionPlus.App.Cli;

internal static class SettingsCommand
{
    /// <summary>Said after <c>set</c>/<c>unset</c> rewrote a file that had comments (the values are kept, the comments not).</summary>
    internal const string CommentsDropped = "note: the file had comments; set and unset rewrite it without them (every value and unknown key is kept)";

    public static int Run(string[] args, TextWriter output)
    {
        if (args.Length > 0 && args[0] is "show" or "path" or "set" or "unset")
        {
            return RunSaved(args, output);
        }

        var reader = new ArgumentReader(args, new HashSet<string>(StringComparer.Ordinal));
        var path = reader.String("validate");
        reader.EnsureAllConsumed();

        if (path is null)
        {
            output.WriteLine(FmpSettingsSerializer.Serialize(new FmpSettings()));
            return CommandDispatcher.ExitOk;
        }

        // An unreadable file is a usage error; a readable file that is not valid settings fails the check.
        var errors = Check(ReadText(path));
        if (errors.Count == 0)
        {
            output.WriteLine($"{path}: OK");
            return CommandDispatcher.ExitOk;
        }

        foreach (var e in errors)
        {
            output.WriteLine($"{path}: {e}");
        }

        return CommandDispatcher.ExitCheckFailed;
    }

    /// <summary>
    /// <c>fmp settings show|path|set &lt;key&gt; &lt;value&gt;|unset &lt;key&gt;</c> on the saved settings file (the default one,
    /// or <c>--settings &lt;file&gt;</c>).
    /// </summary>
    private static int RunSaved(string[] args, TextWriter output)
    {
        var verb = args[0];
        var positional = new List<string>();
        var options = new List<string>();
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal))
            {
                options.Add(args[i]);
                if (!args[i].Contains('=', StringComparison.Ordinal) && i + 1 < args.Length)
                {
                    options.Add(args[++i]);
                }
            }
            else
            {
                positional.Add(args[i]);
            }
        }

        var reader = new ArgumentReader(options, new HashSet<string>(StringComparer.Ordinal));
        var path = reader.String("settings") ?? UserSettings.DefaultPath()
            ?? throw new UsageException("No config directory is known (neither XDG_CONFIG_HOME nor HOME is set); give --settings <file>.");
        reader.EnsureAllConsumed();
        var expected = verb switch { "set" => 2, "unset" => 1, _ => 0 };
        if (positional.Count != expected)
        {
            throw new UsageException(verb switch
            {
                "set" => "fmp settings set <key> <value> (fmp settings show lists the keys).",
                "unset" => "fmp settings unset <key>.",
                _ => $"fmp settings {verb} takes no arguments.",
            });
        }

        switch (verb)
        {
            case "path":
                output.WriteLine(path);
                return CommandDispatcher.ExitOk;
            case "set":
                {
                    var saved = UserSettings.Set(path, positional[0], positional[1], out var dropped);
                    output.WriteLine($"{positional[0]} = {saved} (saved in {path})");
                    if (dropped)
                    {
                        output.WriteLine(CommentsDropped);
                    }

                    return CommandDispatcher.ExitOk;
                }

            case "unset":
                {
                    var removed = UserSettings.Unset(path, positional[0], out var dropped);
                    output.WriteLine(removed
                        ? $"{positional[0]} removed from {path} (the default applies)"
                        : $"{positional[0]} was not set in {path}");
                    if (dropped)
                    {
                        output.WriteLine(CommentsDropped);
                    }

                    return CommandDispatcher.ExitOk;
                }
            default:
                Show(path, output);
                return CommandDispatcher.ExitOk;
        }
    }

    private static void Show(string path, TextWriter output)
    {
        var loaded = UserSettings.Load(path, mustExist: false);
        output.WriteLine(loaded.Exists
            ? $"settings file {path} (fmp capture and fmp play read it; options on the command line win)"
            : $"settings file {path}: none yet (fmp settings set <key> <value> creates it); the built-in defaults apply");
        foreach (var key in UserSettings.Keys)
        {
            var from = loaded.Values.TryGetValue(key.Name, out var value) ? $"{value,-24} from the file" : $"{"(" + key.Default + ")",-24} default";
            output.WriteLine($"  {key.Name,-26} {from}{(key.CaptureOnly ? "  [capture]" : "")}");
        }

        foreach (var unknown in loaded.Unknown)
        {
            output.WriteLine($"  {unknown,-26} unknown to this fmp: skipped");
        }
    }

    /// <summary>
    /// Reads, parses and validates a settings file for a command that runs with it (<c>fmp simulate --settings</c>).
    /// Any problem is a <see cref="UsageException"/>, so the command stops with a message instead of crashing on a
    /// null section or running with nonsense values.
    /// </summary>
    public static FmpSettings Load(string path)
    {
        var text = ReadText(path);
        FmpSettings settings;
        try
        {
            settings = FmpSettingsSerializer.Deserialize(text);
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

        return settings;
    }

    private static List<string> Check(string text)
    {
        try
        {
            return [.. FmpSettingsSerializer.Deserialize(text).Validate()];
        }
        catch (JsonException e)
        {
            return [Describe(e)];
        }
    }

    private static string ReadText(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new UsageException($"Cannot read settings '{path}': {e.Message}", e);
        }
    }

    /// <summary>One line; adds the JSON path when the message does not already carry it.</summary>
    private static string Describe(JsonException e)
    {
        var message = e.Message.ReplaceLineEndings(" ").Trim();
        return e.Path is { Length: > 0 } p && !message.Contains(p, StringComparison.Ordinal)
            ? $"{message} Path: {p}."
            : message;
    }
}

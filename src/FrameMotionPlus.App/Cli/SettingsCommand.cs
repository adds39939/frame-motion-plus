using System.Text.Json;
using FrameMotionPlus.Core.Settings;

namespace FrameMotionPlus.App.Cli;

internal static class SettingsCommand
{
    public static int Run(string[] args, TextWriter output)
    {
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

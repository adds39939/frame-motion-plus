using System.Runtime.ExceptionServices;
using System.Text;
using FrameMotionPlus.App.Cli;

namespace FrameMotionPlus.Core.Tests.Cli;

/// <summary>The <c>fmp</c> command line, in-process through its dispatcher.</summary>
public sealed class CommandLineTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "fmp-cli-tests-" + Guid.NewGuid().ToString("N"));

    public CommandLineTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string File(string json)
    {
        var path = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".json");
        System.IO.File.WriteAllText(path, json);
        return path;
    }

    private static (int Exit, string Out, string Err) Run(params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exit = CommandDispatcher.Run(args, output, error);
        return (exit, output.ToString(), error.ToString());
    }

    /// <summary>
    /// <see cref="Run"/> on its own background thread, failing if it does not return in time: a settings regression
    /// once made <c>simulate</c> spin on a display rate of long.MaxValue Hz, which must fail the test, not hang the run.
    /// </summary>
    private static (int Exit, string Out, string Err) RunBounded(params string[] args)
    {
        var timeout = TimeSpan.FromSeconds(60);
        (int Exit, string Out, string Err) result = default;
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            // An exception escaping this thread would take the test host down; hand it to the test thread instead.
            try
            {
                result = Run(args);
            }
            catch (Exception e)
            {
                failure = ExceptionDispatchInfo.Capture(e);
            }
        }) { IsBackground = true, Name = "fmp-cli" };
        thread.Start();
        Assert.True(thread.Join(timeout), $"'fmp {string.Join(' ', args)}' did not return within {timeout.TotalSeconds:F0} s");
        failure?.Throw();
        return result;
    }

    // ---- Review regressions: null sections and an empty path crashed the binary with exit 134 (finding 4) ----

    [Theory]
    [InlineData("""{ "scheduler": null }""", "'scheduler'")]
    [InlineData("""{ "cadence": null }""", "'cadence'")]
    [InlineData("""{ "telemetry": null }""", "'telemetry'")]
    public void Settings_validate_reports_a_null_section_as_invalid(string json, string property)
    {
        var path = File(json);
        var (exit, output, error) = Run("settings", "--validate", path);

        Assert.Equal(CommandDispatcher.ExitCheckFailed, exit);
        Assert.Contains(property, output, StringComparison.Ordinal);
        Assert.Contains("null", output, StringComparison.Ordinal);
        Assert.Empty(error);
    }

    [Theory]
    [InlineData("""{ "scheduler": null }""")]
    [InlineData("""{ "cadence": null }""")]
    [InlineData("""{ "telemetry": null }""")]
    [InlineData("""{ "display": { "refreshHz": 1e300 } }""")]
    public void Simulate_with_an_invalid_settings_file_is_a_usage_error(string json)
    {
        var path = File(json);
        var (exit, _, error) = RunBounded("simulate", "--settings", path, "--duration-s", "2");

        Assert.Equal(CommandDispatcher.ExitUsage, exit);
        Assert.Contains($"Invalid settings '{path}'", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("settings", "--validate=")]
    [InlineData("simulate", "--settings=")]
    [InlineData("simulate", "--csv=")]
    public void An_empty_option_value_is_a_usage_error(string command, string option)
    {
        var (exit, _, error) = Run(command, option);

        Assert.Equal(CommandDispatcher.ExitUsage, exit);
        Assert.Contains("needs a value", error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_settings_file_is_a_usage_error()
    {
        var (exit, _, error) = Run("settings", "--validate", Path.Combine(_directory, "absent.json"));
        Assert.Equal(CommandDispatcher.ExitUsage, exit);
        Assert.Contains("Cannot read settings", error, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unexpected_exception_is_one_line_and_a_non_zero_exit_not_a_crash()
    {
        // Printing the default settings to a writer that fails stands in for any bug below the dispatcher.
        using var error = new StringWriter();
        var exit = CommandDispatcher.Run(["settings"], new FailingWriter(), error);

        Assert.Equal(CommandDispatcher.ExitInternalError, exit);
        var line = Assert.Single(error.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.Equal("fmp: internal error: InvalidOperationException: writer is broken", line.TrimEnd('\r'));
    }

    [Fact]
    public void Exit_codes_are_the_documented_values()
    {
        // Literal values, as documented in the usage text and README: scripts depend on them, so they must not drift.
        Assert.Equal(0, CommandDispatcher.ExitOk);
        Assert.Equal(1, CommandDispatcher.ExitCheckFailed);
        Assert.Equal(2, CommandDispatcher.ExitUsage);
        Assert.Equal(70, CommandDispatcher.ExitInternalError);
        Assert.Contains("0 success, 1 check or validation failed, 2 usage error, 70 internal error", Run("--help").Out, StringComparison.Ordinal);
    }

    // ---- Review regressions: typos and nonsense reported OK with exit 0 (finding 5) ----

    [Fact]
    public void Settings_validate_rejects_the_review_example()
    {
        var path = File("""{ "scheduler": { "marginMS": 3 }, "generator": { "backend": "nope", "quality": 99 }, "Display": { "refreshHz": -1 } }""");
        var (exit, output, _) = Run("settings", "--validate", path);

        Assert.Equal(CommandDispatcher.ExitCheckFailed, exit);
        Assert.Contains("'marginMS'", output, StringComparison.Ordinal);
        Assert.DoesNotContain("OK", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Settings_validate_lists_every_out_of_range_value()
    {
        var path = File("""{ "display": { "refreshHz": 1e300, "vrrMinimumHz": -5 }, "cadence": { "rateWindow": 2000000000, "sdrHigh": 5 }, "telemetry": { "ringCapacity": 1073741824 } }""");
        var (exit, output, _) = Run("settings", "--validate", path);

        Assert.Equal(CommandDispatcher.ExitCheckFailed, exit);
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(5, lines.Length);
        Assert.All(lines, l => Assert.StartsWith($"{path}: ", l, StringComparison.Ordinal));
    }

    [Fact]
    public void Settings_validate_accepts_the_defaults_and_a_partial_file()
    {
        var (_, defaults, _) = Run("settings");
        var (exit, output, _) = Run("settings", "--validate", File(defaults));
        Assert.Equal(CommandDispatcher.ExitOk, exit);
        Assert.EndsWith(": OK", output.TrimEnd(), StringComparison.Ordinal);

        var partial = File("""{ "display": { "refreshHz": 119.88 }, /* comment */ "scheduler": { "clockMode": "Vrr", }, }""");
        Assert.Equal(CommandDispatcher.ExitOk, Run("settings", "--validate", partial).Exit);
    }

    [Fact]
    public void Simulate_runs_with_a_valid_settings_file()
    {
        var path = File("""{ "display": { "refreshHz": 120 }, "scheduler": { "marginMs": 0.75 } }""");
        var (exit, output, error) = RunBounded("simulate", "--settings", path, "--duration-s", "3", "--check");

        Assert.True(exit == CommandDispatcher.ExitOk, error + output);
    }

    private sealed class FailingWriter : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value) => throw new InvalidOperationException("writer is broken");

        public override void Write(string? value) => throw new InvalidOperationException("writer is broken");

        public override void WriteLine(string? value) => throw new InvalidOperationException("writer is broken");
    }
}

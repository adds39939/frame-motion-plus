using System.Text.Json;
using FrameMotionPlus.Core.Abstractions;
using FrameMotionPlus.Core.Scheduling;
using FrameMotionPlus.Core.Settings;
using FrameMotionPlus.Core.Time;

namespace FrameMotionPlus.Core.Tests.Settings;

public class SettingsTests
{
    [Fact]
    public void Defaults_round_trip_through_json()
    {
        var json = FmpSettingsSerializer.Serialize(new FmpSettings());
        var back = FmpSettingsSerializer.Deserialize(json);
        Assert.Equal(json, FmpSettingsSerializer.Serialize(back));
        Assert.Empty(back.Validate());
    }

    [Fact]
    public void Json_uses_camel_case_and_enum_names()
    {
        var json = FmpSettingsSerializer.Serialize(new FmpSettings());
        Assert.Contains("\"clockMode\": \"FixedRefresh\"", json, StringComparison.Ordinal);
        Assert.Contains("\"refreshHz\": 120", json, StringComparison.Ordinal);
        Assert.Contains("\"quality\": \"Medium\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Partial_documents_keep_defaults_and_accept_comments()
    {
        const string Json = """
            {
              // only override what differs
              "display": { "refreshHz": 119.88 },
              "scheduler": { "clockMode": "Vrr", "marginMs": 0.75, },
            }
            """;
        var settings = FmpSettingsSerializer.Deserialize(Json);
        Assert.Equal(ClockMode.Vrr, settings.Scheduler.ClockMode);
        Assert.Equal(8, settings.Scheduler.MaxMultiplier);
        Assert.Equal(FrameGenQuality.Medium, settings.Generator.Quality);

        var options = settings.Scheduler.ToOptions(settings.Display);
        Assert.Equal(new FrameRate(120000, 1001), options.DisplayRate);
        Assert.Equal(750_000, options.MarginNs);
        Assert.Equal(ClockMode.Vrr, options.ClockMode);
        Assert.Equal(5_000_000, options.SlewDownNsPerSecond);
    }

    [Fact]
    public void Invalid_values_are_reported_not_thrown()
    {
        var settings = new FmpSettings();
        settings.Scheduler.MaxMultiplier = 0;
        settings.Cadence.SdrHigh = 0.0001;
        settings.Telemetry.RingCapacity = 1000;
        var errors = settings.Validate();
        Assert.Equal(3, errors.Count);
    }

    [Fact]
    public void Malformed_json_throws_json_exception()
    {
        Assert.ThrowsAny<JsonException>(() => FmpSettingsSerializer.Deserialize("{ \"scheduler\": { \"maxMultiplier\": \"lots\" } }"));
        Assert.ThrowsAny<JsonException>(() => FmpSettingsSerializer.Deserialize("null"));
        Assert.ThrowsAny<JsonException>(() => FmpSettingsSerializer.Deserialize("[]"));
    }

    // ---- Review regressions: null sections (finding 4) ----

    [Theory]
    [InlineData("""{ "scheduler": null }""", "scheduler")]
    [InlineData("""{ "cadence": null }""", "cadence")]
    [InlineData("""{ "telemetry": null }""", "telemetry")]
    [InlineData("""{ "display": null }""", "display")]
    [InlineData("""{ "generator": null }""", "generator")]
    [InlineData("""{ "generator": { "backend": null } }""", "backend")]
    public void Null_sections_and_strings_are_rejected_when_parsing(string json, string property)
    {
        var e = Assert.ThrowsAny<JsonException>(() => FmpSettingsSerializer.Deserialize(json));
        Assert.Contains($"'{property}'", e.Message, StringComparison.Ordinal);
        Assert.Contains("null", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Nullable_csv_path_may_be_null()
    {
        var settings = FmpSettingsSerializer.Deserialize("""{ "telemetry": { "csvPath": null } }""");
        Assert.Null(settings.Telemetry.CsvPath);
        Assert.Empty(settings.Validate());
    }

    [Fact]
    public void Validate_reports_null_sections_set_in_code_instead_of_throwing()
    {
        var settings = new FmpSettings { Display = null!, Scheduler = null!, Cadence = null!, Generator = null!, Telemetry = null! };
        var errors = settings.Validate();
        Assert.Equal(
            ["display must not be null.", "scheduler must not be null.", "cadence must not be null.", "generator must not be null.", "telemetry must not be null."],
            errors);

        var onlyDisplay = new FmpSettings { Display = null! };
        Assert.Equal(["display must not be null."], onlyDisplay.Validate());
    }

    // ---- Review regressions: typos and nonsense (finding 5) ----

    [Fact]
    public void The_review_example_file_is_rejected()
    {
        const string Json = """{ "scheduler": { "marginMS": 3 }, "generator": { "backend": "nope", "quality": 99 }, "Display": { "refreshHz": -1 } }""";
        var e = Assert.ThrowsAny<JsonException>(() => FmpSettingsSerializer.Deserialize(Json));
        Assert.Contains("'marginMS'", e.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{ "scheduler": { "marginMS": 3 } }""", "'marginMS'")]
    [InlineData("""{ "scheduler": { "notAThing": true } }""", "'notAThing'")]
    [InlineData("""{ "Display": { "refreshHz": 60 } }""", "'Display'")]
    [InlineData("""{ "display": { "refreshHz": 60 }, "displays": {} }""", "'displays'")]
    public void Unknown_or_misspelt_properties_are_rejected(string json, string property)
    {
        var e = Assert.ThrowsAny<JsonException>(() => FmpSettingsSerializer.Deserialize(json));
        Assert.Contains(property, e.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{ "scheduler": { "maxMultiplier": 2, "maxMultiplier": 4 } }""")]
    [InlineData("""{ "display": { "refreshHz": 60 }, "display": { "refreshHz": 120 } }""")]
    public void Duplicate_properties_are_rejected(string json)
    {
        var e = Assert.ThrowsAny<JsonException>(() => FmpSettingsSerializer.Deserialize(json));
        Assert.Contains("Duplicate", e.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{ "scheduler": { "clockMode": 7 } }""")]
    [InlineData("""{ "scheduler": { "clockMode": 1 } }""")]
    [InlineData("""{ "scheduler": { "clockMode": "7" } }""")]
    [InlineData("""{ "scheduler": { "clockMode": "FixedRefresh, Vrr" } }""")]
    [InlineData("""{ "scheduler": { "clockMode": "" } }""")]
    [InlineData("""{ "scheduler": { "clockMode": null } }""")]
    [InlineData("""{ "generator": { "quality": 99 } }""")]
    [InlineData("""{ "generator": { "quality": "Low, Medium" } }""")]
    [InlineData("""{ "generator": { "quality": "Ultra" } }""")]
    public void Enum_values_must_be_one_defined_name(string json)
    {
        var e = Assert.ThrowsAny<JsonException>(() => FmpSettingsSerializer.Deserialize(json));
        Assert.Contains("Expected one of", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Enum_names_are_read_case_insensitively_and_written_exactly()
    {
        var settings = FmpSettingsSerializer.Deserialize("""{ "scheduler": { "clockMode": "vrr" }, "generator": { "quality": "HIGH" } }""");
        Assert.Equal(ClockMode.Vrr, settings.Scheduler.ClockMode);
        Assert.Equal(FrameGenQuality.High, settings.Generator.Quality);
        var json = FmpSettingsSerializer.Serialize(settings);
        Assert.Contains("\"clockMode\": \"Vrr\"", json, StringComparison.Ordinal);
        Assert.Contains("\"quality\": \"High\"", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{ "display": { "refreshHz": 1e300 } }""", "display.refreshHz")]
    [InlineData("""{ "display": { "refreshHz": 1e999 } }""", "display.refreshHz")]
    [InlineData("""{ "display": { "refreshHz": -1 } }""", "display.refreshHz")]
    [InlineData("""{ "display": { "refreshHz": 0 } }""", "display.refreshHz")]
    [InlineData("""{ "display": { "refreshHz": 0.0004 } }""", "display.refreshHz")]
    [InlineData("""{ "display": { "vrrMinimumHz": -5 } }""", "display.vrrMinimumHz")]
    [InlineData("""{ "display": { "refreshHz": 60, "vrrMinimumHz": 240 } }""", "display.vrrMinimumHz")]
    [InlineData("""{ "scheduler": { "marginMs": 1e300 } }""", "scheduler.marginMs")]
    [InlineData("""{ "scheduler": { "marginMs": -0.5 } }""", "scheduler.marginMs")]
    [InlineData("""{ "scheduler": { "maxMultiplier": 9 } }""", "scheduler.maxMultiplier")]
    [InlineData("""{ "scheduler": { "slewUpMsPerSecond": 1e-9 } }""", "scheduler.slewUpMsPerSecond")]
    [InlineData("""{ "scheduler": { "fallbackDwellMs": 1e300 } }""", "scheduler.fallbackDwellMs")]
    [InlineData("""{ "scheduler": { "initialGenerationTimeMs": 1e12 } }""", "scheduler.initialGenerationTimeMs")]
    [InlineData("""{ "scheduler": { "holdIntervalFactor": 1e6 } }""", "scheduler.holdIntervalFactor")]
    [InlineData("""{ "scheduler": { "generationTimePercentile": 0.0001 } }""", "scheduler.generationTimePercentile")]
    [InlineData("""{ "cadence": { "rateWindow": 2000000000 } }""", "cadence.rateWindow")]
    [InlineData("""{ "cadence": { "sdrHigh": 5 } }""", "cadence.sdrHigh")]
    [InlineData("""{ "cadence": { "pqHigh": 1e300 } }""", "cadence.pqHigh")]
    [InlineData("""{ "cadence": { "pqLow": 0.02, "pqHigh": 0.01 } }""", "cadence.pqLow")]
    [InlineData("""{ "telemetry": { "ringCapacity": 1073741824 } }""", "telemetry.ringCapacity")]
    [InlineData("""{ "telemetry": { "ringCapacity": 1000 } }""", "telemetry.ringCapacity")]
    [InlineData("""{ "telemetry": { "enabled": true, "csvPath": "" } }""", "telemetry.csvPath")]
    [InlineData("""{ "generator": { "backend": "nope" } }""", "generator.backend")]
    [InlineData("""{ "generator": { "backend": "VFG" } }""", "generator.backend")]
    public void Out_of_range_values_are_reported_with_the_json_field_name(string json, string field)
    {
        var errors = FmpSettingsSerializer.Deserialize(json).Validate();
        var error = Assert.Single(errors);
        Assert.StartsWith(field, error, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_known_backend_is_accepted()
    {
        foreach (var backend in GeneratorSettings.KnownBackends)
        {
            var settings = FmpSettingsSerializer.Deserialize($$"""{ "generator": { "backend": "{{backend}}" } }""");
            Assert.Empty(settings.Validate());
        }
    }

    [Fact]
    public void Undefined_enum_values_set_in_code_are_reported()
    {
        var settings = new FmpSettings();
        settings.Scheduler.ClockMode = (ClockMode)7;
        settings.Generator.Quality = (FrameGenQuality)99;
        var errors = settings.Validate();
        Assert.Equal(2, errors.Count);
        Assert.StartsWith("scheduler.clockMode", errors[0], StringComparison.Ordinal);
        Assert.StartsWith("generator.quality", errors[1], StringComparison.Ordinal);
    }
}

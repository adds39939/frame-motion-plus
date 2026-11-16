using System.Text.Json;
using System.Text.Json.Serialization;

namespace FrameMotionPlus.Core.Settings;

/// <summary>
/// Strict reading: a misspelt or misplaced property, a property given twice, or <see langword="null"/> for a
/// non-nullable section or string is an error rather than silently ignored or crashing later.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    NumberHandling = JsonNumberHandling.Strict,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    RespectNullableAnnotations = true,
    AllowDuplicateProperties = false)]
[JsonSerializable(typeof(FmpSettings))]
internal sealed partial class FmpSettingsJsonContext : JsonSerializerContext;

/// <summary>Reflection-free (source-generated) JSON for <see cref="FmpSettings"/>; safe under NativeAOT.</summary>
public static class FmpSettingsSerializer
{
    public static string Serialize(FmpSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return JsonSerializer.Serialize(settings, FmpSettingsJsonContext.Default.FmpSettings);
    }

    /// <summary>
    /// Parses settings; missing properties keep their defaults. Throws <see cref="JsonException"/> for malformed JSON,
    /// unknown or duplicate properties, null sections and enum values that are not a single defined name. Value ranges
    /// are checked separately by <see cref="FmpSettings.Validate"/>.
    /// </summary>
    public static FmpSettings Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return JsonSerializer.Deserialize(json, FmpSettingsJsonContext.Default.FmpSettings)
            ?? throw new JsonException("Settings document is null.");
    }
}

/// <summary>
/// Reads an enum from exactly one of its defined names (case-insensitive) and writes it by name. Unlike the default
/// string enum converter it rejects numbers (<c>"clockMode": 7</c> would otherwise be cast to an undefined value)
/// and comma lists (<c>"Low, Medium"</c> would otherwise be OR-ed into <c>High</c>). Generic and reflection-free,
/// so NativeAOT-safe.
/// </summary>
internal sealed class EnumNameConverter<TEnum> : JsonConverter<TEnum>
    where TEnum : struct, Enum
{
    public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var name = reader.GetString();
            if (name is { Length: > 0 }
                && char.IsLetter(name[0])
                && !name.Contains(',', StringComparison.Ordinal)
                && Enum.TryParse<TEnum>(name, ignoreCase: true, out var value)
                && Enum.IsDefined(value))
            {
                return value;
            }
        }

        throw new JsonException($"Expected one of {string.Join(", ", Enum.GetNames<TEnum>())} as a string.");
    }

    public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value.ToString());
    }
}

using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

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

/// <summary>As <see cref="FmpSettingsJsonContext"/>, except that unknown properties are skipped (and reported by the caller).</summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    NumberHandling = JsonNumberHandling.Strict,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    RespectNullableAnnotations = true,
    AllowDuplicateProperties = false)]
[JsonSerializable(typeof(FmpSettings))]
internal sealed partial class FmpSettingsLenientJsonContext : JsonSerializerContext;

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

    /// <summary>
    /// Parses a user's settings file as <see cref="Deserialize"/> does, except that a property this build does not know
    /// is skipped and its dotted name (<c>audio.sinc</c>) added to <paramref name="unknown"/> instead of failing. The
    /// dotted names of the known leaf properties the file sets to a value other than null go to <paramref name="present"/>.
    /// Throws <see cref="JsonException"/> (with the line and byte position) for malformed JSON, a wrong type, a duplicate
    /// property or a null section.
    /// </summary>
    public static FmpSettings DeserializeLenient(string json, ICollection<string> present, ICollection<string> unknown)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(present);
        ArgumentNullException.ThrowIfNull(unknown);
        var settings = JsonSerializer.Deserialize(json, FmpSettingsLenientJsonContext.Default.FmpSettings)
            ?? throw new JsonException("Settings document is null.");
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        Walk(document.RootElement, FmpSettingsJsonContext.Default.FmpSettings, "", present, unknown);
        return settings;
    }

    /// <summary>The dotted names of every leaf setting this build knows (<c>capture.mode</c>, <c>scheduler.marginMs</c> ...).</summary>
    public static IReadOnlyList<string> KnownKeys()
    {
        var keys = new List<string>();
        Collect(FmpSettingsJsonContext.Default.FmpSettings, "", keys);
        return keys;
    }

    private static void Collect(JsonTypeInfo type, string prefix, List<string> keys)
    {
        foreach (var property in type.Properties)
        {
            var section = FmpSettingsJsonContext.Default.GetTypeInfo(property.PropertyType);
            if (section is { Kind: JsonTypeInfoKind.Object })
            {
                Collect(section, prefix + property.Name + ".", keys);
            }
            else
            {
                keys.Add(prefix + property.Name);
            }
        }
    }

    private static void Walk(JsonElement element, JsonTypeInfo type, string prefix, ICollection<string> present, ICollection<string> unknown)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var member in element.EnumerateObject())
        {
            JsonPropertyInfo? match = null;
            foreach (var property in type.Properties)
            {
                if (string.Equals(property.Name, member.Name, StringComparison.Ordinal))
                {
                    match = property;
                    break;
                }
            }

            var name = prefix + member.Name;
            if (match is null)
            {
                unknown.Add(name);
                continue;
            }

            var section = FmpSettingsJsonContext.Default.GetTypeInfo(match.PropertyType);
            if (section is { Kind: JsonTypeInfoKind.Object })
            {
                Walk(member.Value, section, name + ".", present, unknown);
            }
            else if (member.Value.ValueKind != JsonValueKind.Null)
            {
                present.Add(name);
            }
        }
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

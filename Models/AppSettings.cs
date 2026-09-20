using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Soundrel.Models;

public sealed class AppSettings
{
    public const int CurrentVersion = 5;

    public const float DefaultFastFadeSeconds = 2.0f;

    public const float DefaultMediumFadeSeconds = 5.0f;

    public const float DefaultSlowFadeSeconds = 10.0f;

    public const float DefaultCrossfadeStaggerSeconds = 1.0f;

    public int Version { get; init; }

    public string? SelectedLibraryPath { get; init; }

    public ImmediateTransitionMode PreferredImmediateTransitionMode { get; init; } =
        ImmediateTransitionMode.HardCut;

    public float MusicVolume { get; init; } = 1.0f;

    public float AmbienceVolume { get; init; } = 1.0f;

    public float MasterVolume { get; init; } = 1.0f;

    [JsonConverter(typeof(SettingsTimingValueConverter))]
    public float FastFadeSeconds { get; init; } = DefaultFastFadeSeconds;

    [JsonConverter(typeof(SettingsTimingValueConverter))]
    public float MediumFadeSeconds { get; init; } = DefaultMediumFadeSeconds;

    [JsonConverter(typeof(SettingsTimingValueConverter))]
    public float SlowFadeSeconds { get; init; } = DefaultSlowFadeSeconds;

    [JsonConverter(typeof(SettingsTimingValueConverter))]
    public float CrossfadeStaggerSeconds { get; init; } = DefaultCrossfadeStaggerSeconds;

    public IReadOnlyList<LibraryAmbiencePresets> AmbiencePresets { get; init; } =
        Array.Empty<LibraryAmbiencePresets>();

    public static AppSettings CreateDefault() => new()
    {
        Version = CurrentVersion,
        SelectedLibraryPath = null,
        PreferredImmediateTransitionMode = ImmediateTransitionMode.HardCut,
        MusicVolume = 1.0f,
        AmbienceVolume = 1.0f,
        MasterVolume = 1.0f,
        FastFadeSeconds = DefaultFastFadeSeconds,
        MediumFadeSeconds = DefaultMediumFadeSeconds,
        SlowFadeSeconds = DefaultSlowFadeSeconds,
        CrossfadeStaggerSeconds = DefaultCrossfadeStaggerSeconds,
        AmbiencePresets = Array.Empty<LibraryAmbiencePresets>(),
    };
}

// Timing values are intentionally tolerant on read. A bad timing value should
// be recoverable independently of the other settings in the document.
public sealed class SettingsTimingValueConverter : JsonConverter<float>
{
    public override float Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetSingle(out float number))
        {
            return number;
        }

        if (reader.TokenType == JsonTokenType.String &&
            float.TryParse(
                reader.GetString(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out float namedNumber))
        {
            return namedNumber;
        }

        if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
        {
            reader.Skip();
        }

        return float.NaN;
    }

    public override void Write(
        Utf8JsonWriter writer,
        float value,
        JsonSerializerOptions options) => writer.WriteNumberValue(value);
}

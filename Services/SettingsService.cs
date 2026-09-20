using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Soundrel.Models;

namespace Soundrel.Services;

public sealed record SettingsLoadResult(
    AppSettings Settings,
    string? Warning,
    bool RequiresPersistenceMigration = false);

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters =
        {
            new TolerantImmediateTransitionModeConverter(),
            new SettingsVolumeValueConverter(),
        },
    };

    public SettingsService(string? settingsPath = null)
    {
        SettingsPath = Path.GetFullPath(settingsPath ?? DefaultSettingsPath);
    }

    public static string DefaultSettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Soundrel",
        "settings.json");

    public string SettingsPath { get; }

    public SettingsLoadResult Load()
    {
        try
        {
            using FileStream stream = File.OpenRead(SettingsPath);
            using JsonDocument document = JsonDocument.Parse(stream);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return UseDefaults("Settings file is empty or malformed; defaults are being used.");
            }

            AppSettings? settings = DeserializeCoreSettings(document.RootElement);

            if (settings is null)
            {
                return UseDefaults("Settings file is empty or malformed; defaults are being used.");
            }

            string? selectedLibraryPath = ReadSelectedLibraryPath(
                document.RootElement,
                out string? selectedLibraryPathWarning);

            if (settings.Version is >= 1 and <= 3)
            {
                if ((settings.Version >= 2 && !Enum.IsDefined(settings.PreferredImmediateTransitionMode)) ||
                    (settings.Version >= 3 && !AreValidVolumes(settings)))
                {
                    return UseDefaults("Settings file is empty or malformed; defaults are being used.");
                }

                return new SettingsLoadResult(
                    new AppSettings
                    {
                        Version = AppSettings.CurrentVersion,
                        SelectedLibraryPath = selectedLibraryPath,
                        PreferredImmediateTransitionMode = settings.Version >= 2
                            ? settings.PreferredImmediateTransitionMode
                            : ImmediateTransitionMode.HardCut,
                        MusicVolume = settings.Version >= 3 ? settings.MusicVolume : 1.0f,
                        AmbienceVolume = settings.Version >= 3 ? settings.AmbienceVolume : 1.0f,
                        MasterVolume = settings.Version >= 3 ? settings.MasterVolume : 1.0f,
                        AmbiencePresets = Array.Empty<LibraryAmbiencePresets>(),
                    },
                    selectedLibraryPathWarning,
                    true);
            }

            if (settings.Version == 4)
            {
                if (!Enum.IsDefined(settings.PreferredImmediateTransitionMode) || !AreValidVolumes(settings))
                {
                    return UseDefaults("Settings file contains an invalid transition mode or volume; defaults are being used.");
                }

                List<string> migrationWarnings = [];
                float migratedFastFadeSeconds = UseTimingOrDefault(
                    settings.FastFadeSeconds,
                    AppSettings.DefaultFastFadeSeconds,
                    "fastFadeSeconds",
                    migrationWarnings);
                float migratedMediumFadeSeconds = UseTimingOrDefault(
                    settings.MediumFadeSeconds,
                    AppSettings.DefaultMediumFadeSeconds,
                    "mediumFadeSeconds",
                    migrationWarnings);
                float migratedSlowFadeSeconds = UseTimingOrDefault(
                    settings.SlowFadeSeconds,
                    AppSettings.DefaultSlowFadeSeconds,
                    "slowFadeSeconds",
                    migrationWarnings);
                float migratedCrossfadeStaggerSeconds = settings.CrossfadeStaggerSeconds;
                if (!IsValidStagger(
                        migratedCrossfadeStaggerSeconds,
                        migratedMediumFadeSeconds))
                {
                    migratedCrossfadeStaggerSeconds = GetDefaultStaggerSeconds(migratedMediumFadeSeconds);
                    migrationWarnings.Add("crossfadeStaggerSeconds");
                }

                List<string> migrationWarningMessages = [];
                if (selectedLibraryPathWarning is not null)
                {
                    migrationWarningMessages.Add(selectedLibraryPathWarning);
                }

                if (migrationWarnings.Count > 0)
                {
                    migrationWarningMessages.Add(
                        $"Settings contained invalid timing value(s) for {string.Join(", ", migrationWarnings)}; documented defaults were used.");
                }

                return new SettingsLoadResult(
                    CopySettings(
                        settings,
                        Array.Empty<LibraryAmbiencePresets>(),
                        migratedFastFadeSeconds,
                        migratedMediumFadeSeconds,
                        migratedSlowFadeSeconds,
                        migratedCrossfadeStaggerSeconds,
                        selectedLibraryPath: selectedLibraryPath),
                    CreateWarning(migrationWarningMessages),
                    true);
            }

            if (settings.Version != AppSettings.CurrentVersion)
            {
                return UseDefaults($"Settings version {settings.Version} is not supported; defaults are being used.");
            }

            IReadOnlyList<LibraryAmbiencePresets> ambiencePresets =
                ReadAmbiencePresets(document.RootElement, out List<string> presetWarnings);
            if (selectedLibraryPathWarning is not null)
            {
                presetWarnings.Add(selectedLibraryPathWarning);
            }
            ImmediateTransitionMode preferredTransitionMode = settings.PreferredImmediateTransitionMode;
            if (!Enum.IsDefined(preferredTransitionMode))
            {
                preferredTransitionMode = ImmediateTransitionMode.HardCut;
                presetWarnings.Add(
                    "Settings contained an invalid preferredImmediateTransitionMode; HardCut was used.");
            }

            float musicVolume = UseVolumeOrDefault(
                settings.MusicVolume,
                "musicVolume",
                presetWarnings);
            float ambienceVolume = UseVolumeOrDefault(
                settings.AmbienceVolume,
                "ambienceVolume",
                presetWarnings);
            float masterVolume = UseVolumeOrDefault(
                settings.MasterVolume,
                "masterVolume",
                presetWarnings);
            List<string> invalidTimingNames = [];
            float fastFadeSeconds = UseTimingOrDefault(
                settings.FastFadeSeconds,
                AppSettings.DefaultFastFadeSeconds,
                "fastFadeSeconds",
                invalidTimingNames);
            float mediumFadeSeconds = UseTimingOrDefault(
                settings.MediumFadeSeconds,
                AppSettings.DefaultMediumFadeSeconds,
                "mediumFadeSeconds",
                invalidTimingNames);
            float slowFadeSeconds = UseTimingOrDefault(
                settings.SlowFadeSeconds,
                AppSettings.DefaultSlowFadeSeconds,
                "slowFadeSeconds",
                invalidTimingNames);
            float crossfadeStaggerSeconds = settings.CrossfadeStaggerSeconds;
            if (!IsValidStagger(crossfadeStaggerSeconds, mediumFadeSeconds))
            {
                crossfadeStaggerSeconds = GetDefaultStaggerSeconds(mediumFadeSeconds);
                invalidTimingNames.Add("crossfadeStaggerSeconds");
            }

            if (invalidTimingNames.Count == 0)
            {
                return new SettingsLoadResult(
                    CopySettings(
                        settings,
                        ambiencePresets,
                        preferredImmediateTransitionMode: preferredTransitionMode,
                        musicVolume: musicVolume,
                        ambienceVolume: ambienceVolume,
                        masterVolume: masterVolume,
                        selectedLibraryPath: selectedLibraryPath),
                    CreateWarning(presetWarnings));
            }

            presetWarnings.Add(
                $"Settings contained invalid timing value(s) for {string.Join(", ", invalidTimingNames)}; documented defaults were used.");
            return new SettingsLoadResult(
                CopySettings(
                    settings,
                    ambiencePresets,
                    fastFadeSeconds,
                    mediumFadeSeconds,
                    slowFadeSeconds,
                    crossfadeStaggerSeconds,
                    preferredTransitionMode,
                    musicVolume,
                    ambienceVolume,
                    masterVolume,
                    selectedLibraryPath),
                CreateWarning(presetWarnings));
        }
        catch (FileNotFoundException)
        {
            return UseDefaults("Settings file was not found; defaults are being used.");
        }
        catch (DirectoryNotFoundException)
        {
            return UseDefaults("Settings file was not found; defaults are being used.");
        }
        catch (JsonException)
        {
            return UseDefaults("Settings file is empty or malformed; defaults are being used.");
        }
        catch (IOException)
        {
            return UseDefaults("Settings file could not be read; defaults are being used.");
        }
        catch (UnauthorizedAccessException)
        {
            return UseDefaults("Settings file could not be read; defaults are being used.");
        }
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (!Enum.IsDefined(settings.PreferredImmediateTransitionMode))
        {
            throw new ArgumentOutOfRangeException(
                nameof(settings),
                settings.PreferredImmediateTransitionMode,
                "The preferred immediate transition mode is not supported.");
        }

        if (!IsValidVolume(settings.MusicVolume))
        {
            throw new ArgumentOutOfRangeException(
                nameof(settings),
                settings.MusicVolume,
                "The music volume must be finite and between 0 and 1.");
        }

        if (!IsValidVolume(settings.AmbienceVolume))
        {
            throw new ArgumentOutOfRangeException(
                nameof(settings),
                settings.AmbienceVolume,
                "The ambience volume must be finite and between 0 and 1.");
        }

        if (!IsValidVolume(settings.MasterVolume))
        {
            throw new ArgumentOutOfRangeException(
                nameof(settings),
                settings.MasterVolume,
                "The master volume must be finite and between 0 and 1.");
        }

        if (!IsValidDuration(settings.FastFadeSeconds))
        {
            throw new ArgumentOutOfRangeException(
                nameof(settings.FastFadeSeconds),
                settings.FastFadeSeconds,
                "The fast fade duration must be finite and greater than zero.");
        }

        if (!IsValidDuration(settings.MediumFadeSeconds))
        {
            throw new ArgumentOutOfRangeException(
                nameof(settings.MediumFadeSeconds),
                settings.MediumFadeSeconds,
                "The medium fade duration must be finite and greater than zero.");
        }

        if (!IsValidDuration(settings.SlowFadeSeconds))
        {
            throw new ArgumentOutOfRangeException(
                nameof(settings.SlowFadeSeconds),
                settings.SlowFadeSeconds,
                "The slow fade duration must be finite and greater than zero.");
        }

        if (!IsValidStagger(
                settings.CrossfadeStaggerSeconds,
                settings.MediumFadeSeconds))
        {
            throw new ArgumentOutOfRangeException(
                nameof(settings.CrossfadeStaggerSeconds),
                settings.CrossfadeStaggerSeconds,
                "The crossfade stagger must be finite, non-negative, and no greater than the medium fade duration.");
        }

        IReadOnlyList<LibraryAmbiencePresets> ambiencePresets =
            ValidateAndNormalizeAmbiencePresets(settings.AmbiencePresets);

        string directory = Path.GetDirectoryName(SettingsPath)!;
        Directory.CreateDirectory(directory);

        string temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(SettingsPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            var persistedSettings = new AppSettings
            {
                Version = AppSettings.CurrentVersion,
                SelectedLibraryPath = settings.SelectedLibraryPath,
                PreferredImmediateTransitionMode = settings.PreferredImmediateTransitionMode,
                MusicVolume = settings.MusicVolume,
                AmbienceVolume = settings.AmbienceVolume,
                MasterVolume = settings.MasterVolume,
                FastFadeSeconds = settings.FastFadeSeconds,
                MediumFadeSeconds = settings.MediumFadeSeconds,
                SlowFadeSeconds = settings.SlowFadeSeconds,
                CrossfadeStaggerSeconds = settings.CrossfadeStaggerSeconds,
                AmbiencePresets = ambiencePresets,
            };

            using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None))
            {
                JsonSerializer.Serialize(stream, persistedSettings, JsonOptions);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, SettingsPath, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static SettingsLoadResult UseDefaults(string warning) =>
        new(AppSettings.CreateDefault(), warning);

    private static AppSettings? DeserializeCoreSettings(JsonElement root)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (JsonProperty property in root.EnumerateObject())
            {
                if (property.Name.Equals("ambiencePresets", StringComparison.OrdinalIgnoreCase) ||
                    property.Name.Equals("selectedLibraryPath", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                property.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        return JsonSerializer.Deserialize<AppSettings>(stream.ToArray(), JsonOptions);
    }

    private static AppSettings CopySettings(
        AppSettings settings,
        IReadOnlyList<LibraryAmbiencePresets> ambiencePresets,
        float? fastFadeSeconds = null,
        float? mediumFadeSeconds = null,
        float? slowFadeSeconds = null,
        float? crossfadeStaggerSeconds = null,
        ImmediateTransitionMode? preferredImmediateTransitionMode = null,
        float? musicVolume = null,
        float? ambienceVolume = null,
        float? masterVolume = null,
        string? selectedLibraryPath = null) =>
        new()
        {
            Version = AppSettings.CurrentVersion,
            SelectedLibraryPath = selectedLibraryPath,
            PreferredImmediateTransitionMode = preferredImmediateTransitionMode ?? settings.PreferredImmediateTransitionMode,
            MusicVolume = musicVolume ?? settings.MusicVolume,
            AmbienceVolume = ambienceVolume ?? settings.AmbienceVolume,
            MasterVolume = masterVolume ?? settings.MasterVolume,
            FastFadeSeconds = fastFadeSeconds ?? settings.FastFadeSeconds,
            MediumFadeSeconds = mediumFadeSeconds ?? settings.MediumFadeSeconds,
            SlowFadeSeconds = slowFadeSeconds ?? settings.SlowFadeSeconds,
            CrossfadeStaggerSeconds = crossfadeStaggerSeconds ?? settings.CrossfadeStaggerSeconds,
            AmbiencePresets = ambiencePresets,
        };

    private static string? ReadSelectedLibraryPath(
        JsonElement root,
        out string? warning)
    {
        warning = null;
        if (!TryGetProperty(root, "selectedLibraryPath", out JsonElement property) ||
            property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.String)
        {
            return property.GetString();
        }

        warning = "Settings contained an invalid selectedLibraryPath; it was cleared.";
        return null;
    }

    private static string? CreateWarning(IReadOnlyCollection<string> warnings) =>
        warnings.Count == 0 ? null : string.Join(" ", warnings);

    private static IReadOnlyList<LibraryAmbiencePresets> ReadAmbiencePresets(
        JsonElement root,
        out List<string> warnings)
    {
        warnings = [];
        if (!TryGetProperty(root, "ambiencePresets", out JsonElement property))
        {
            return Array.Empty<LibraryAmbiencePresets>();
        }

        if (property.ValueKind != JsonValueKind.Array)
        {
            warnings.Add("Invalid ambience preset data was ignored.");
            return Array.Empty<LibraryAmbiencePresets>();
        }

        List<LibraryAmbiencePresets> libraries = [];
        Dictionary<string, int> libraryIndexes = new(StringComparer.OrdinalIgnoreCase);
        foreach (JsonElement libraryElement in property.EnumerateArray())
        {
            if (libraryElement.ValueKind != JsonValueKind.Object ||
                !TryGetStringProperty(libraryElement, "libraryRootPath", out string? rawLibraryRoot) ||
                !AmbiencePresetPath.TryNormalizeLibraryRootPath(rawLibraryRoot, out string libraryRoot))
            {
                warnings.Add("An invalid ambience preset library entry was ignored.");
                continue;
            }

            bool duplicateLibrary = libraryIndexes.ContainsKey(libraryRoot);
            if (duplicateLibrary)
            {
                warnings.Add(
                    $"Duplicate ambience preset library '{libraryRoot}' was merged during normalization.");
            }

            if (!TryGetProperty(libraryElement, "presets", out JsonElement presetsElement) ||
                presetsElement.ValueKind != JsonValueKind.Array)
            {
                warnings.Add("An invalid ambience preset library entry was ignored.");
                continue;
            }

            List<AmbiencePreset> presets = [];
            Dictionary<string, int> presetIndexes = new(StringComparer.OrdinalIgnoreCase);
            foreach (JsonElement presetElement in presetsElement.EnumerateArray())
            {
                if (presetElement.ValueKind != JsonValueKind.Object ||
                    !TryGetStringProperty(presetElement, "name", out string? rawName))
                {
                    warnings.Add("An invalid ambience preset entry was ignored.");
                    continue;
                }

                string name = rawName?.Trim() ?? string.Empty;
                if (name.Length == 0 ||
                    !TryGetProperty(presetElement, "tracks", out JsonElement tracksElement) ||
                    tracksElement.ValueKind != JsonValueKind.Array)
                {
                    warnings.Add("An invalid ambience preset entry was ignored.");
                    continue;
                }

                if (!string.Equals(rawName, name, StringComparison.Ordinal))
                {
                    warnings.Add($"Ambience preset name '{rawName}' was trimmed while loading.");
                }

                List<AmbiencePresetTrack> tracks = [];
                Dictionary<string, int> trackIndexes = new(StringComparer.OrdinalIgnoreCase);
                foreach (JsonElement trackElement in tracksElement.EnumerateArray())
                {
                    if (trackElement.ValueKind != JsonValueKind.Object ||
                        !TryGetStringProperty(trackElement, "relativePath", out string? rawTrackPath) ||
                        !TryGetProperty(trackElement, "sourceVolume", out JsonElement sourceVolumeElement) ||
                        sourceVolumeElement.ValueKind != JsonValueKind.Number ||
                        !sourceVolumeElement.TryGetSingle(out float sourceVolume) ||
                        !IsValidVolume(sourceVolume) ||
                        !AmbiencePresetPath.TryNormalizeRelativeTrackPath(
                            rawTrackPath,
                            libraryRoot,
                            out string relativeTrackPath))
                    {
                        warnings.Add("An invalid ambience preset track entry was ignored.");
                        continue;
                    }

                    AmbiencePresetTrack track = new(relativeTrackPath, sourceVolume);
                    if (trackIndexes.TryGetValue(relativeTrackPath, out int existingTrackIndex))
                    {
                        tracks[existingTrackIndex] = track;
                        warnings.Add(
                            $"Duplicate ambience track '{relativeTrackPath}' was normalized with its last value.");
                    }
                    else
                    {
                        trackIndexes.Add(relativeTrackPath, tracks.Count);
                        tracks.Add(track);
                    }
                }

                AmbiencePreset preset = new(name, tracks.ToArray());
                if (presetIndexes.TryGetValue(name, out int existingPresetIndex))
                {
                    presets[existingPresetIndex] = preset;
                    warnings.Add($"Duplicate ambience preset '{name}' was normalized with its last value.");
                }
                else
                {
                    presetIndexes.Add(name, presets.Count);
                    presets.Add(preset);
                }
            }

            if (libraryIndexes.TryGetValue(libraryRoot, out int existingLibraryIndex))
            {
                List<AmbiencePreset> mergedPresets = libraries[existingLibraryIndex].Presets.ToList();
                MergePresets(mergedPresets, presets, warnings);
                libraries[existingLibraryIndex] = new LibraryAmbiencePresets(
                    libraryRoot,
                    mergedPresets.ToArray());
            }
            else
            {
                libraryIndexes.Add(libraryRoot, libraries.Count);
                libraries.Add(new LibraryAmbiencePresets(libraryRoot, presets.ToArray()));
            }
        }

        return libraries.ToArray();
    }

    private static void MergePresets(
        List<AmbiencePreset> destination,
        IReadOnlyList<AmbiencePreset> incoming,
        ICollection<string> warnings)
    {
        Dictionary<string, int> presetIndexes = new(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < destination.Count; index++)
        {
            presetIndexes[destination[index].Name] = index;
        }

        foreach (AmbiencePreset preset in incoming)
        {
            if (presetIndexes.TryGetValue(preset.Name, out int existingPresetIndex))
            {
                destination[existingPresetIndex] = preset;
                warnings.Add(
                    $"Duplicate ambience preset '{preset.Name}' was normalized with its last valid value.");
            }
            else
            {
                presetIndexes.Add(preset.Name, destination.Count);
                destination.Add(preset);
            }
        }
    }

    private static bool TryGetProperty(
        JsonElement element,
        string propertyName,
        out JsonElement property)
    {
        foreach (JsonProperty candidate in element.EnumerateObject())
        {
            if (candidate.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
            {
                property = candidate.Value;
                return true;
            }
        }

        property = default;
        return false;
    }

    private static bool TryGetStringProperty(
        JsonElement element,
        string propertyName,
        out string? value)
    {
        if (TryGetProperty(element, propertyName, out JsonElement property) &&
            property.ValueKind == JsonValueKind.String)
        {
            value = property.GetString();
            return value is not null;
        }

        value = null;
        return false;
    }

    private static IReadOnlyList<LibraryAmbiencePresets> ValidateAndNormalizeAmbiencePresets(
        IReadOnlyList<LibraryAmbiencePresets>? libraries)
    {
        if (libraries is null)
        {
            throw new ArgumentOutOfRangeException(
                nameof(AppSettings.AmbiencePresets),
                "Ambience presets cannot be null.");
        }

        List<LibraryAmbiencePresets> normalizedLibraries = [];
        HashSet<string> libraryRoots = new(StringComparer.OrdinalIgnoreCase);
        foreach (LibraryAmbiencePresets? library in libraries)
        {
            if (library is null ||
                !AmbiencePresetPath.TryNormalizeLibraryRootPath(
                    library.LibraryRootPath,
                    out string normalizedLibraryRoot))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(AppSettings.AmbiencePresets),
                    "Each ambience preset library must have a rooted library path.");
            }

            if (!libraryRoots.Add(normalizedLibraryRoot))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(AppSettings.AmbiencePresets),
                    "Ambience preset library paths must be unique case-insensitively.");
            }

            if (library.Presets is null)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(AppSettings.AmbiencePresets),
                    "Ambience preset collections cannot be null.");
            }

            List<AmbiencePreset> normalizedPresets = [];
            HashSet<string> presetNames = new(StringComparer.OrdinalIgnoreCase);
            foreach (AmbiencePreset? preset in library.Presets)
            {
                if (preset is null || string.IsNullOrEmpty(preset.Name) || preset.Name != preset.Name.Trim())
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(AppSettings.AmbiencePresets),
                        "Preset names must be non-empty and trimmed.");
                }

                if (!presetNames.Add(preset.Name))
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(AppSettings.AmbiencePresets),
                        "Preset names must be unique case-insensitively within a library.");
                }

                if (preset.Tracks is null)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(AppSettings.AmbiencePresets),
                        "Ambience preset track collections cannot be null.");
                }

                List<AmbiencePresetTrack> normalizedTracks = [];
                HashSet<string> trackPaths = new(StringComparer.OrdinalIgnoreCase);
                foreach (AmbiencePresetTrack? track in preset.Tracks)
                {
                    if (track is null ||
                        !IsValidVolume(track.SourceVolume) ||
                        !AmbiencePresetPath.TryNormalizeRelativeTrackPath(
                            track.RelativePath,
                            normalizedLibraryRoot,
                            out string normalizedTrackPath))
                    {
                        throw new ArgumentOutOfRangeException(
                            nameof(AppSettings.AmbiencePresets),
                            "Ambience preset tracks must use safe relative paths and finite source volumes between 0 and 1.");
                    }

                    if (!trackPaths.Add(normalizedTrackPath))
                    {
                        throw new ArgumentOutOfRangeException(
                            nameof(AppSettings.AmbiencePresets),
                            "Ambience preset track paths must be unique case-insensitively.");
                    }

                    normalizedTracks.Add(new AmbiencePresetTrack(normalizedTrackPath, track.SourceVolume));
                }

                normalizedPresets.Add(new AmbiencePreset(preset.Name, normalizedTracks.ToArray()));
            }

            normalizedLibraries.Add(new LibraryAmbiencePresets(
                normalizedLibraryRoot,
                normalizedPresets.ToArray()));
        }

        return normalizedLibraries.ToArray();
    }

    private static bool AreValidVolumes(AppSettings settings) =>
        IsValidVolume(settings.MusicVolume) &&
        IsValidVolume(settings.AmbienceVolume) &&
        IsValidVolume(settings.MasterVolume);

    private static float UseVolumeOrDefault(
        float value,
        string name,
        ICollection<string> warnings)
    {
        if (IsValidVolume(value))
        {
            return value;
        }

        warnings.Add($"Settings contained invalid {name}; 1.0 was used.");
        return 1.0f;
    }

    private static bool IsValidVolume(float value) =>
        float.IsFinite(value) && value >= 0.0f && value <= 1.0f;

    private static float UseTimingOrDefault(
        float value,
        float defaultValue,
        string name,
        ICollection<string> invalidNames)
    {
        if (IsValidDuration(value))
        {
            return value;
        }

        invalidNames.Add(name);
        return defaultValue;
    }

    private static bool IsValidDuration(float value) =>
        float.IsFinite(value) && value > 0.0f;

    private static bool IsValidStagger(float value) =>
        float.IsFinite(value) &&
        value >= 0.0f;

    private static bool IsValidStagger(float value, float mediumFadeSeconds) =>
        IsValidStagger(value) &&
        value <= mediumFadeSeconds;

    private static float GetDefaultStaggerSeconds(float mediumFadeSeconds) =>
        MathF.Min(AppSettings.DefaultCrossfadeStaggerSeconds, mediumFadeSeconds);

    private sealed class TolerantImmediateTransitionModeConverter : JsonConverter<ImmediateTransitionMode>
    {
        public override ImmediateTransitionMode Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String &&
                Enum.TryParse(
                    reader.GetString(),
                    ignoreCase: true,
                    out ImmediateTransitionMode parsedMode))
            {
                return parsedMode;
            }

            if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int numericMode))
            {
                return (ImmediateTransitionMode)numericMode;
            }

            if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
            {
                reader.Skip();
            }

            return (ImmediateTransitionMode)(-1);
        }

        public override void Write(
            Utf8JsonWriter writer,
            ImmediateTransitionMode value,
            JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());
    }

    private sealed class SettingsVolumeValueConverter : JsonConverter<float>
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
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
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
}

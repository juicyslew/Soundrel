using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Soundrel.Models;

namespace Soundrel.Services;

public sealed record SettingsLoadResult(AppSettings Settings, string? Warning);

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
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
            AppSettings? settings = JsonSerializer.Deserialize<AppSettings>(stream, JsonOptions);

            if (settings is null)
            {
                return UseDefaults("Settings file is empty or malformed; defaults are being used.");
            }

            if (settings.Version == 1)
            {
                return new SettingsLoadResult(
                    new AppSettings
                    {
                        Version = AppSettings.CurrentVersion,
                        SelectedLibraryPath = settings.SelectedLibraryPath,
                        PreferredImmediateTransitionMode = ImmediateTransitionMode.HardCut,
                        MusicVolume = 1.0f,
                        AmbienceVolume = 1.0f,
                        MasterVolume = 1.0f,
                    },
                    null);
            }

            if (settings.Version == 2)
            {
                if (!Enum.IsDefined(settings.PreferredImmediateTransitionMode))
                {
                    return UseDefaults("Settings file is empty or malformed; defaults are being used.");
                }

                return new SettingsLoadResult(
                    new AppSettings
                    {
                        Version = AppSettings.CurrentVersion,
                        SelectedLibraryPath = settings.SelectedLibraryPath,
                        PreferredImmediateTransitionMode = settings.PreferredImmediateTransitionMode,
                        MusicVolume = 1.0f,
                        AmbienceVolume = 1.0f,
                        MasterVolume = 1.0f,
                    },
                    null);
            }

            if (settings.Version != AppSettings.CurrentVersion)
            {
                return UseDefaults($"Settings version {settings.Version} is not supported; defaults are being used.");
            }

            if (!Enum.IsDefined(settings.PreferredImmediateTransitionMode) || !AreValidVolumes(settings))
            {
                return UseDefaults("Settings file contains an invalid transition mode or volume; defaults are being used.");
            }

            return new SettingsLoadResult(settings, null);
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

    private static bool AreValidVolumes(AppSettings settings) =>
        IsValidVolume(settings.MusicVolume) &&
        IsValidVolume(settings.AmbienceVolume) &&
        IsValidVolume(settings.MasterVolume);

    private static bool IsValidVolume(float value) =>
        float.IsFinite(value) && value >= 0.0f && value <= 1.0f;
}

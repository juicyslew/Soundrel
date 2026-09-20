using System.Text.Json;
using Soundrel.Models;
using Soundrel.Services;

namespace Soundrel.Tests;

[TestClass]
public sealed class SettingsServiceTests
{
    private string temporaryDirectory = null!;

    [TestInitialize]
    public void Initialize()
    {
        temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            "Soundrel.Tests",
            Guid.NewGuid().ToString("N"));
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(temporaryDirectory))
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [TestMethod]
    public void DefaultSettingsPathUsesLocalApplicationData()
    {
        string expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Soundrel",
            "settings.json");

        Assert.AreEqual(expected, SettingsService.DefaultSettingsPath);
        Assert.AreEqual(expected, new SettingsService().SettingsPath);
    }

    [TestMethod]
    public void LoadMissingFileReturnsDefaultsWithWarning()
    {
        var service = CreateService();

        SettingsLoadResult result = service.Load();

        Assert.AreEqual(AppSettings.CurrentVersion, result.Settings.Version);
        Assert.IsNull(result.Settings.SelectedLibraryPath);
        Assert.AreEqual(
            ImmediateTransitionMode.HardCut,
            result.Settings.PreferredImmediateTransitionMode);
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.Warning));
    }

    [TestMethod]
    public void CreateDefaultReturnsCurrentHardCutSettingsWithFullVolumesAndDefaultTimings()
    {
        AppSettings settings = AppSettings.CreateDefault();

        Assert.AreEqual(AppSettings.CurrentVersion, settings.Version);
        Assert.IsNull(settings.SelectedLibraryPath);
        Assert.AreEqual(
            ImmediateTransitionMode.HardCut,
            settings.PreferredImmediateTransitionMode);
        Assert.AreEqual(1.0f, settings.MusicVolume);
        Assert.AreEqual(1.0f, settings.AmbienceVolume);
        Assert.AreEqual(1.0f, settings.MasterVolume);
        Assert.AreEqual(AppSettings.DefaultFastFadeSeconds, settings.FastFadeSeconds);
        Assert.AreEqual(AppSettings.DefaultMediumFadeSeconds, settings.MediumFadeSeconds);
        Assert.AreEqual(AppSettings.DefaultSlowFadeSeconds, settings.SlowFadeSeconds);
        Assert.AreEqual(AppSettings.DefaultCrossfadeStaggerSeconds, settings.CrossfadeStaggerSeconds);
    }

    [TestMethod]
    [DataRow(ImmediateTransitionMode.HardCut, "HardCut")]
    [DataRow(ImmediateTransitionMode.Crossfade, "Crossfade")]
    public void SaveAndLoadRoundTripsVersionThreeDocument(
        ImmediateTransitionMode transitionMode,
        string persistedTransitionMode)
    {
        Directory.CreateDirectory(temporaryDirectory);
        var service = CreateService();
        string libraryPath = Path.Combine(temporaryDirectory, "Library");

        service.Save(new AppSettings
        {
            SelectedLibraryPath = libraryPath,
            PreferredImmediateTransitionMode = transitionMode,
            MusicVolume = 0.0f,
            AmbienceVolume = 0.5f,
            MasterVolume = 1.0f,
        });
        SettingsLoadResult result = service.Load();

        Assert.IsNull(result.Warning);
        Assert.AreEqual(AppSettings.CurrentVersion, result.Settings.Version);
        Assert.AreEqual(libraryPath, result.Settings.SelectedLibraryPath);
        Assert.AreEqual(transitionMode, result.Settings.PreferredImmediateTransitionMode);
        Assert.AreEqual(0.0f, result.Settings.MusicVolume);
        Assert.AreEqual(0.5f, result.Settings.AmbienceVolume);
        Assert.AreEqual(1.0f, result.Settings.MasterVolume);

        string json = File.ReadAllText(service.SettingsPath);
        StringAssert.Contains(json, "\n", "Saved JSON should be indented and readable.");

        using JsonDocument document = JsonDocument.Parse(json);
        Assert.AreEqual(AppSettings.CurrentVersion, document.RootElement.GetProperty("version").GetInt32());
        Assert.AreEqual(libraryPath, document.RootElement.GetProperty("selectedLibraryPath").GetString());
        Assert.AreEqual(
            persistedTransitionMode,
            document.RootElement.GetProperty("preferredImmediateTransitionMode").GetString());
        CollectionAssert.AreEquivalent(
            new[]
            {
                "version",
                "selectedLibraryPath",
                "preferredImmediateTransitionMode",
                "musicVolume",
                "ambienceVolume",
                "masterVolume",
                "fastFadeSeconds",
                "mediumFadeSeconds",
                "slowFadeSeconds",
                "crossfadeStaggerSeconds",
                "ambiencePresets",
            },
            document.RootElement.EnumerateObject().Select(property => property.Name).ToArray());
    }

    [TestMethod]
    public void LoadVersionOneDocumentMigratesPathAndDefaultsToHardCut()
    {
        Directory.CreateDirectory(temporaryDirectory);
        var service = CreateService();
        const string libraryPath = @"C:\LegacyLibrary";
        File.WriteAllText(
            service.SettingsPath,
            """
            {
              "version": 1,
              "selectedLibraryPath": "C:\\LegacyLibrary"
            }
            """);

        SettingsLoadResult result = service.Load();

        Assert.IsNull(result.Warning);
        Assert.AreEqual(AppSettings.CurrentVersion, result.Settings.Version);
        Assert.AreEqual(libraryPath, result.Settings.SelectedLibraryPath);
        Assert.AreEqual(
            ImmediateTransitionMode.HardCut,
            result.Settings.PreferredImmediateTransitionMode);
        Assert.AreEqual(1.0f, result.Settings.MusicVolume);
        Assert.AreEqual(1.0f, result.Settings.AmbienceVolume);
        Assert.AreEqual(1.0f, result.Settings.MasterVolume);
        Assert.IsTrue(result.RequiresPersistenceMigration);
    }

    [TestMethod]
    public void LoadVersionTwoDocumentWithoutModeDefaultsToHardCut()
    {
        Directory.CreateDirectory(temporaryDirectory);
        var service = CreateService();
        File.WriteAllText(
            service.SettingsPath,
            """
            {
              "version": 2,
              "selectedLibraryPath": "C:\\Library"
            }
            """);

        SettingsLoadResult result = service.Load();

        Assert.IsNull(result.Warning);
        Assert.AreEqual(@"C:\Library", result.Settings.SelectedLibraryPath);
        Assert.AreEqual(ImmediateTransitionMode.HardCut, result.Settings.PreferredImmediateTransitionMode);
        Assert.AreEqual(1.0f, result.Settings.MusicVolume);
        Assert.AreEqual(1.0f, result.Settings.AmbienceVolume);
        Assert.AreEqual(1.0f, result.Settings.MasterVolume);
    }

    [TestMethod]
    public void LoadVersionTwoDocumentPreservesTransitionModeAndDefaultsVolumes()
    {
        Directory.CreateDirectory(temporaryDirectory);
        var service = CreateService();
        File.WriteAllText(
            service.SettingsPath,
            """
            {
              "version": 2,
              "selectedLibraryPath": "C:\\Library",
              "preferredImmediateTransitionMode": "Crossfade"
            }
            """);

        SettingsLoadResult result = service.Load();

        Assert.IsNull(result.Warning);
        Assert.AreEqual(@"C:\Library", result.Settings.SelectedLibraryPath);
        Assert.AreEqual(ImmediateTransitionMode.Crossfade, result.Settings.PreferredImmediateTransitionMode);
        Assert.AreEqual(1.0f, result.Settings.MusicVolume);
        Assert.AreEqual(1.0f, result.Settings.AmbienceVolume);
        Assert.AreEqual(1.0f, result.Settings.MasterVolume);
    }

    [TestMethod]
    public void LoadVersionThreeDocumentDefaultsMissingVolumes()
    {
        Directory.CreateDirectory(temporaryDirectory);
        var service = CreateService();
        File.WriteAllText(
            service.SettingsPath,
            """
            {
              "version": 3,
              "selectedLibraryPath": "C:\\Library",
              "preferredImmediateTransitionMode": "Crossfade"
            }
            """);

        SettingsLoadResult result = service.Load();

        Assert.IsNull(result.Warning);
        Assert.AreEqual(@"C:\Library", result.Settings.SelectedLibraryPath);
        Assert.AreEqual(ImmediateTransitionMode.Crossfade, result.Settings.PreferredImmediateTransitionMode);
        Assert.AreEqual(1.0f, result.Settings.MusicVolume);
        Assert.AreEqual(1.0f, result.Settings.AmbienceVolume);
        Assert.AreEqual(1.0f, result.Settings.MasterVolume);
    }

    [TestMethod]
    public void LoadCurrentDocumentPreservesValidFractionalTimings()
    {
        Directory.CreateDirectory(temporaryDirectory);
        var service = CreateService();
        File.WriteAllText(
            service.SettingsPath,
            """
            {
              "version": 5,
              "selectedLibraryPath": "C:\\Library",
              "preferredImmediateTransitionMode": "Crossfade",
              "musicVolume": 0.25,
              "ambienceVolume": 0.5,
              "masterVolume": 0.75,
              "fastFadeSeconds": 0.25,
              "mediumFadeSeconds": 3.5,
              "slowFadeSeconds": 12.75,
              "crossfadeStaggerSeconds": 3.25
            }
            """);

        SettingsLoadResult result = service.Load();

        Assert.IsNull(result.Warning);
        Assert.AreEqual(@"C:\Library", result.Settings.SelectedLibraryPath);
        Assert.AreEqual(ImmediateTransitionMode.Crossfade, result.Settings.PreferredImmediateTransitionMode);
        Assert.AreEqual(0.25f, result.Settings.MusicVolume);
        Assert.AreEqual(0.25f, result.Settings.FastFadeSeconds);
        Assert.AreEqual(3.5f, result.Settings.MediumFadeSeconds);
        Assert.AreEqual(12.75f, result.Settings.SlowFadeSeconds);
        Assert.AreEqual(3.25f, result.Settings.CrossfadeStaggerSeconds);
    }

    [TestMethod]
    public void LoadCurrentDocumentDefaultsInvalidTimingsIndividually()
    {
        Directory.CreateDirectory(temporaryDirectory);
        var service = CreateService();
        File.WriteAllText(
            service.SettingsPath,
            """
            {
              "version": 5,
              "selectedLibraryPath": "C:\\Library",
              "preferredImmediateTransitionMode": "Crossfade",
              "musicVolume": 0.25,
              "ambienceVolume": 0.5,
              "masterVolume": 0.75,
              "fastFadeSeconds": -1,
              "mediumFadeSeconds": 3.5,
              "slowFadeSeconds": 0,
              "crossfadeStaggerSeconds": -1
            }
            """);

        SettingsLoadResult result = service.Load();

        Assert.AreEqual(@"C:\Library", result.Settings.SelectedLibraryPath);
        Assert.AreEqual(ImmediateTransitionMode.Crossfade, result.Settings.PreferredImmediateTransitionMode);
        Assert.AreEqual(0.25f, result.Settings.MusicVolume);
        Assert.AreEqual(AppSettings.DefaultFastFadeSeconds, result.Settings.FastFadeSeconds);
        Assert.AreEqual(3.5f, result.Settings.MediumFadeSeconds);
        Assert.AreEqual(AppSettings.DefaultSlowFadeSeconds, result.Settings.SlowFadeSeconds);
        Assert.AreEqual(AppSettings.DefaultCrossfadeStaggerSeconds, result.Settings.CrossfadeStaggerSeconds);
        StringAssert.Contains(result.Warning!, "fastFadeSeconds");
        StringAssert.Contains(result.Warning!, "slowFadeSeconds");
        StringAssert.Contains(result.Warning!, "crossfadeStaggerSeconds");
    }

    [TestMethod]
    public void LoadCurrentDocumentDefaultsStaggerExceedingMediumWithoutDiscardingOtherSettings()
    {
        Directory.CreateDirectory(temporaryDirectory);
        var service = CreateService();
        string libraryPath = Path.Combine(temporaryDirectory, "Library");
        File.WriteAllText(
            service.SettingsPath,
            $$"""
            {
              "version": 5,
              "selectedLibraryPath": {{JsonSerializer.Serialize(libraryPath)}},
              "preferredImmediateTransitionMode": "Crossfade",
              "musicVolume": 0.25,
              "ambienceVolume": 0.5,
              "masterVolume": 0.75,
              "fastFadeSeconds": 0.25,
              "mediumFadeSeconds": 3.5,
              "slowFadeSeconds": 12.75,
              "crossfadeStaggerSeconds": 4.25,
              "ambiencePresets": [
                {
                  "libraryRootPath": {{JsonSerializer.Serialize(libraryPath)}},
                  "presets": [
                    {
                      "name": "Saved Night",
                      "tracks": [
                        { "relativePath": "rain.wav", "sourceVolume": 0.65 }
                      ]
                    }
                  ]
                }
              ]
            }
            """
        );

        SettingsLoadResult result = service.Load();

        Assert.AreEqual(libraryPath, result.Settings.SelectedLibraryPath);
        Assert.AreEqual(ImmediateTransitionMode.Crossfade, result.Settings.PreferredImmediateTransitionMode);
        Assert.AreEqual(0.25f, result.Settings.MusicVolume);
        Assert.AreEqual(0.5f, result.Settings.AmbienceVolume);
        Assert.AreEqual(0.75f, result.Settings.MasterVolume);
        Assert.AreEqual(0.25f, result.Settings.FastFadeSeconds);
        Assert.AreEqual(3.5f, result.Settings.MediumFadeSeconds);
        Assert.AreEqual(12.75f, result.Settings.SlowFadeSeconds);
        Assert.AreEqual(AppSettings.DefaultCrossfadeStaggerSeconds, result.Settings.CrossfadeStaggerSeconds);
        Assert.HasCount(1, result.Settings.AmbiencePresets);
        Assert.AreEqual("Saved Night", result.Settings.AmbiencePresets[0].Presets[0].Name);
        StringAssert.Contains(result.Warning!, "crossfadeStaggerSeconds");
    }

    [TestMethod]
    public void LoadCurrentDocumentBoundsInvalidStaggerFallbackToCustomMedium()
    {
        Directory.CreateDirectory(temporaryDirectory);
        var service = CreateService();
        string libraryPath = Path.Combine(temporaryDirectory, "Library");
        File.WriteAllText(
            service.SettingsPath,
            $$"""
            {
              "version": 5,
              "selectedLibraryPath": {{JsonSerializer.Serialize(libraryPath)}},
              "preferredImmediateTransitionMode": "Crossfade",
              "musicVolume": 0.25,
              "ambienceVolume": 0.5,
              "masterVolume": 0.75,
              "fastFadeSeconds": 0.25,
              "mediumFadeSeconds": 0.5,
              "slowFadeSeconds": 12.75,
              "crossfadeStaggerSeconds": 0.75,
              "ambiencePresets": [
                {
                  "libraryRootPath": {{JsonSerializer.Serialize(libraryPath)}},
                  "presets": [
                    {
                      "name": "Saved Night",
                      "tracks": [
                        { "relativePath": "rain.wav", "sourceVolume": 0.65 }
                      ]
                    }
                  ]
                }
              ]
            }
            """
        );

        SettingsLoadResult result = service.Load();

        Assert.AreEqual(libraryPath, result.Settings.SelectedLibraryPath);
        Assert.AreEqual(ImmediateTransitionMode.Crossfade, result.Settings.PreferredImmediateTransitionMode);
        Assert.AreEqual(0.25f, result.Settings.MusicVolume);
        Assert.AreEqual(0.5f, result.Settings.AmbienceVolume);
        Assert.AreEqual(0.75f, result.Settings.MasterVolume);
        Assert.AreEqual(0.25f, result.Settings.FastFadeSeconds);
        Assert.AreEqual(0.5f, result.Settings.MediumFadeSeconds);
        Assert.AreEqual(12.75f, result.Settings.SlowFadeSeconds);
        Assert.AreEqual(0.5f, result.Settings.CrossfadeStaggerSeconds);
        Assert.HasCount(1, result.Settings.AmbiencePresets);
        Assert.AreEqual("Saved Night", result.Settings.AmbiencePresets[0].Presets[0].Name);
        StringAssert.Contains(result.Warning!, "crossfadeStaggerSeconds");
    }

    [TestMethod]
    [DataRow("-0.1")]
    [DataRow("\"NaN\"")]
    [DataRow("\"Infinity\"")]
    public void LoadCurrentDocumentDefaultsNegativeOrNonFiniteStaggerWithoutDiscardingOtherFields(
        string staggerJson)
    {
        Directory.CreateDirectory(temporaryDirectory);
        var service = CreateService();
        File.WriteAllText(
            service.SettingsPath,
            $$"""
            {
              "version": 5,
              "selectedLibraryPath": "C:\\Library",
              "preferredImmediateTransitionMode": "Crossfade",
              "musicVolume": 0.25,
              "ambienceVolume": 0.5,
              "masterVolume": 0.75,
              "fastFadeSeconds": 0.25,
              "mediumFadeSeconds": 3.5,
              "slowFadeSeconds": 12.75,
              "crossfadeStaggerSeconds": {{staggerJson}}
            }
            """
        );

        SettingsLoadResult result = service.Load();

        Assert.AreEqual(@"C:\Library", result.Settings.SelectedLibraryPath);
        Assert.AreEqual(ImmediateTransitionMode.Crossfade, result.Settings.PreferredImmediateTransitionMode);
        Assert.AreEqual(0.25f, result.Settings.MusicVolume);
        Assert.AreEqual(0.25f, result.Settings.FastFadeSeconds);
        Assert.AreEqual(3.5f, result.Settings.MediumFadeSeconds);
        Assert.AreEqual(12.75f, result.Settings.SlowFadeSeconds);
        Assert.AreEqual(AppSettings.DefaultCrossfadeStaggerSeconds, result.Settings.CrossfadeStaggerSeconds);
        StringAssert.Contains(result.Warning!, "crossfadeStaggerSeconds");
    }

    [TestMethod]
    public void LoadLegacyVersionThreePreservesSavedVolumesAndDefaultsTimings()
    {
        Directory.CreateDirectory(temporaryDirectory);
        var service = CreateService();
        File.WriteAllText(
            service.SettingsPath,
            """
            {
              "version": 3,
              "selectedLibraryPath": "C:\\Library",
              "preferredImmediateTransitionMode": "Crossfade",
              "musicVolume": 0.2,
              "ambienceVolume": 0.4,
              "masterVolume": 0.6
            }
            """);

        SettingsLoadResult result = service.Load();

        Assert.AreEqual(@"C:\Library", result.Settings.SelectedLibraryPath);
        Assert.AreEqual(ImmediateTransitionMode.Crossfade, result.Settings.PreferredImmediateTransitionMode);
        Assert.AreEqual(0.2f, result.Settings.MusicVolume);
        Assert.AreEqual(0.4f, result.Settings.AmbienceVolume);
        Assert.AreEqual(0.6f, result.Settings.MasterVolume);
        Assert.AreEqual(AppSettings.DefaultFastFadeSeconds, result.Settings.FastFadeSeconds);
        Assert.AreEqual(AppSettings.DefaultMediumFadeSeconds, result.Settings.MediumFadeSeconds);
        Assert.AreEqual(AppSettings.DefaultSlowFadeSeconds, result.Settings.SlowFadeSeconds);
        Assert.AreEqual(AppSettings.DefaultCrossfadeStaggerSeconds, result.Settings.CrossfadeStaggerSeconds);
        Assert.IsNull(result.Warning);
        Assert.IsTrue(result.RequiresPersistenceMigration);
    }

    [TestMethod]
    public void LoadCurrentDocumentDoesNotRequestPersistenceMigration()
    {
        Directory.CreateDirectory(temporaryDirectory);
        var service = CreateService();
        File.WriteAllText(
            service.SettingsPath,
            """
            {
              "version": 5,
              "selectedLibraryPath": "C:\\Library",
              "preferredImmediateTransitionMode": "Crossfade",
              "musicVolume": 0.25,
              "ambienceVolume": 0.5,
              "masterVolume": 0.75,
              "fastFadeSeconds": 0.25,
              "mediumFadeSeconds": 3.5,
              "slowFadeSeconds": 12.75,
              "crossfadeStaggerSeconds": 1.25
            }
            """);

        SettingsLoadResult result = service.Load();

        Assert.IsFalse(result.RequiresPersistenceMigration);
    }

    [TestMethod]
    public void LoadVersionFourMigratesTimingValuesAndInitializesPresets()
    {
        Directory.CreateDirectory(temporaryDirectory);
        var service = CreateService();
        File.WriteAllText(
            service.SettingsPath,
            """
            {
              "version": 4,
              "selectedLibraryPath": "C:\\Library",
              "preferredImmediateTransitionMode": "Crossfade",
              "musicVolume": 0.2,
              "ambienceVolume": 0.4,
              "masterVolume": 0.6,
              "fastFadeSeconds": 0.25,
              "mediumFadeSeconds": 3.5,
              "slowFadeSeconds": 12.75,
              "crossfadeStaggerSeconds": 1.25
            }
            """);

        SettingsLoadResult result = service.Load();

        Assert.AreEqual(AppSettings.CurrentVersion, result.Settings.Version);
        Assert.AreEqual(@"C:\Library", result.Settings.SelectedLibraryPath);
        Assert.AreEqual(ImmediateTransitionMode.Crossfade, result.Settings.PreferredImmediateTransitionMode);
        Assert.AreEqual(0.2f, result.Settings.MusicVolume);
        Assert.AreEqual(0.4f, result.Settings.AmbienceVolume);
        Assert.AreEqual(0.6f, result.Settings.MasterVolume);
        Assert.AreEqual(0.25f, result.Settings.FastFadeSeconds);
        Assert.AreEqual(3.5f, result.Settings.MediumFadeSeconds);
        Assert.AreEqual(12.75f, result.Settings.SlowFadeSeconds);
        Assert.AreEqual(1.25f, result.Settings.CrossfadeStaggerSeconds);
        Assert.IsEmpty(result.Settings.AmbiencePresets);
        Assert.IsTrue(result.RequiresPersistenceMigration);
    }

    [TestMethod]
    public void LoadVersionFourDefaultsStaggerExceedingRecoveredMediumWithWarning()
    {
        Directory.CreateDirectory(temporaryDirectory);
        var service = CreateService();
        File.WriteAllText(
            service.SettingsPath,
            """
            {
              "version": 4,
              "preferredImmediateTransitionMode": "Crossfade",
              "fastFadeSeconds": 0.25,
              "mediumFadeSeconds": 3.5,
              "slowFadeSeconds": 12.75,
              "crossfadeStaggerSeconds": 4.25
            }
            """);

        SettingsLoadResult result = service.Load();

        Assert.AreEqual(0.25f, result.Settings.FastFadeSeconds);
        Assert.AreEqual(3.5f, result.Settings.MediumFadeSeconds);
        Assert.AreEqual(12.75f, result.Settings.SlowFadeSeconds);
        Assert.AreEqual(AppSettings.DefaultCrossfadeStaggerSeconds, result.Settings.CrossfadeStaggerSeconds);
        StringAssert.Contains(result.Warning!, "crossfadeStaggerSeconds");
        Assert.IsTrue(result.RequiresPersistenceMigration);
    }

    [TestMethod]
    public void LoadVersionFourBoundsInvalidStaggerFallbackToCustomMedium()
    {
        Directory.CreateDirectory(temporaryDirectory);
        var service = CreateService();
        File.WriteAllText(
            service.SettingsPath,
            """
            {
              "version": 4,
              "selectedLibraryPath": "C:\\Library",
              "preferredImmediateTransitionMode": "Crossfade",
              "musicVolume": 0.2,
              "ambienceVolume": 0.4,
              "masterVolume": 0.6,
              "fastFadeSeconds": 0.25,
              "mediumFadeSeconds": 0.5,
              "slowFadeSeconds": 12.75,
              "crossfadeStaggerSeconds": 0.75
            }
            """);

        SettingsLoadResult result = service.Load();

        Assert.AreEqual(@"C:\Library", result.Settings.SelectedLibraryPath);
        Assert.AreEqual(ImmediateTransitionMode.Crossfade, result.Settings.PreferredImmediateTransitionMode);
        Assert.AreEqual(0.2f, result.Settings.MusicVolume);
        Assert.AreEqual(0.4f, result.Settings.AmbienceVolume);
        Assert.AreEqual(0.6f, result.Settings.MasterVolume);
        Assert.AreEqual(0.25f, result.Settings.FastFadeSeconds);
        Assert.AreEqual(0.5f, result.Settings.MediumFadeSeconds);
        Assert.AreEqual(12.75f, result.Settings.SlowFadeSeconds);
        Assert.AreEqual(0.5f, result.Settings.CrossfadeStaggerSeconds);
        StringAssert.Contains(result.Warning!, "crossfadeStaggerSeconds");
        Assert.IsTrue(result.RequiresPersistenceMigration);
    }

    [TestMethod]
    public void LoadCurrentDocumentRecoversInvalidCoreScalarsWithoutDiscardingPresets()
    {
        Directory.CreateDirectory(temporaryDirectory);
        var service = CreateService();
        string libraryPath = Path.Combine(temporaryDirectory, "Library");
        File.WriteAllText(
            service.SettingsPath,
            $$"""
            {
              "version": 5,
              "selectedLibraryPath": {{JsonSerializer.Serialize(libraryPath)}},
              "preferredImmediateTransitionMode": "NotARealMode",
              "musicVolume": -0.25,
              "ambienceVolume": 0.45,
              "masterVolume": "NaN",
              "fastFadeSeconds": 0.5,
              "mediumFadeSeconds": 4.5,
              "slowFadeSeconds": 9.5,
              "crossfadeStaggerSeconds": 1.5,
              "ambiencePresets": [
                {
                  "libraryRootPath": {{JsonSerializer.Serialize(libraryPath)}},
                  "presets": [
                    {
                      "name": "Saved Night",
                      "tracks": [
                        { "relativePath": "missing-rain.wav", "sourceVolume": 0.65 }
                      ]
                    }
                  ]
                }
              ]
            }
            """);

        SettingsLoadResult result = service.Load();

        Assert.IsNotNull(result.Warning);
        StringAssert.Contains(result.Warning!, "preferredImmediateTransitionMode");
        StringAssert.Contains(result.Warning!, "musicVolume");
        StringAssert.Contains(result.Warning!, "masterVolume");
        Assert.AreEqual(libraryPath, result.Settings.SelectedLibraryPath);
        Assert.AreEqual(ImmediateTransitionMode.HardCut, result.Settings.PreferredImmediateTransitionMode);
        Assert.AreEqual(1.0f, result.Settings.MusicVolume);
        Assert.AreEqual(0.45f, result.Settings.AmbienceVolume);
        Assert.AreEqual(1.0f, result.Settings.MasterVolume);
        Assert.AreEqual(0.5f, result.Settings.FastFadeSeconds);
        Assert.AreEqual(4.5f, result.Settings.MediumFadeSeconds);
        Assert.AreEqual(9.5f, result.Settings.SlowFadeSeconds);
        Assert.AreEqual(1.5f, result.Settings.CrossfadeStaggerSeconds);
        Assert.HasCount(1, result.Settings.AmbiencePresets);
        Assert.AreEqual(
            "Saved Night",
            result.Settings.AmbiencePresets[0].Presets[0].Name);
        Assert.AreEqual(
            "missing-rain.wav",
            result.Settings.AmbiencePresets[0].Presets[0].Tracks[0].RelativePath);

        service.Save(result.Settings);
        SettingsLoadResult reloaded = service.Load();

        Assert.IsNull(reloaded.Warning);
        Assert.AreEqual(ImmediateTransitionMode.HardCut, reloaded.Settings.PreferredImmediateTransitionMode);
        Assert.HasCount(1, reloaded.Settings.AmbiencePresets);
        Assert.AreEqual(
            "Saved Night",
            reloaded.Settings.AmbiencePresets[0].Presets[0].Name);
        Assert.AreEqual(
            0.65f,
            reloaded.Settings.AmbiencePresets[0].Presets[0].Tracks[0].SourceVolume);
    }

    [TestMethod]
    public void LoadCurrentDocumentClearsMalformedSelectedPathWithoutDiscardingPresets()
    {
        Directory.CreateDirectory(temporaryDirectory);
        var service = CreateService();
        string libraryPath = Path.Combine(temporaryDirectory, "Library");
        File.WriteAllText(
            service.SettingsPath,
            $$"""
            {
              "version": 5,
              "selectedLibraryPath": { "not": "a string" },
              "preferredImmediateTransitionMode": "Crossfade",
              "musicVolume": 0.25,
              "ambienceVolume": 0.5,
              "masterVolume": 0.75,
              "fastFadeSeconds": 0.5,
              "mediumFadeSeconds": 4.5,
              "slowFadeSeconds": 9.5,
              "crossfadeStaggerSeconds": 1.5,
              "ambiencePresets": [
                {
                  "libraryRootPath": {{JsonSerializer.Serialize(libraryPath)}},
                  "presets": [
                    {
                      "name": "Saved Night",
                      "tracks": [
                        { "relativePath": "missing-rain.wav", "sourceVolume": 0.65 }
                      ]
                    }
                  ]
                }
              ]
            }
            """);

        SettingsLoadResult result = service.Load();

        Assert.IsNotNull(result.Warning);
        StringAssert.Contains(result.Warning!, "selectedLibraryPath");
        Assert.IsNull(result.Settings.SelectedLibraryPath);
        Assert.AreEqual(ImmediateTransitionMode.Crossfade, result.Settings.PreferredImmediateTransitionMode);
        Assert.AreEqual(0.25f, result.Settings.MusicVolume);
        Assert.AreEqual(0.5f, result.Settings.AmbienceVolume);
        Assert.AreEqual(0.75f, result.Settings.MasterVolume);
        Assert.HasCount(1, result.Settings.AmbiencePresets);

        service.Save(result.Settings);
        SettingsLoadResult reloaded = service.Load();

        Assert.IsNull(reloaded.Warning);
        Assert.IsNull(reloaded.Settings.SelectedLibraryPath);
        Assert.HasCount(1, reloaded.Settings.AmbiencePresets);
        Assert.AreEqual(
            "Saved Night",
            reloaded.Settings.AmbiencePresets[0].Presets[0].Name);
    }

    [TestMethod]
    public void LoadDuplicateLibraryWithMalformedChildPreservesEarlierValidPresets()
    {
        Directory.CreateDirectory(temporaryDirectory);
        var service = CreateService();
        string libraryPath = Path.Combine(temporaryDirectory, "Library");
        string duplicateLibraryPath = libraryPath + Path.DirectorySeparatorChar;
        File.WriteAllText(
            service.SettingsPath,
            $$"""
            {
              "version": 5,
              "ambiencePresets": [
                {
                  "libraryRootPath": {{JsonSerializer.Serialize(libraryPath)}},
                  "presets": [
                    {
                      "name": "Rain",
                      "tracks": [{ "relativePath": "rain.wav", "sourceVolume": 0.4 }]
                    }
                  ]
                },
                {
                  "libraryRootPath": {{JsonSerializer.Serialize(duplicateLibraryPath)}},
                  "presets": [
                    { "name": "Rain", "tracks": { "malformed": true } }
                  ]
                }
              ]
            }
            """);

        SettingsLoadResult result = service.Load();

        Assert.IsNotNull(result.Warning);
        Assert.HasCount(1, result.Settings.AmbiencePresets);
        Assert.HasCount(1, result.Settings.AmbiencePresets[0].Presets);
        Assert.AreEqual("Rain", result.Settings.AmbiencePresets[0].Presets[0].Name);
        Assert.AreEqual(
            0.4f,
            result.Settings.AmbiencePresets[0].Presets[0].Tracks[0].SourceVolume);
    }

    [TestMethod]
    public void LoadDuplicateLibrariesMergesDistinctPresetsAndUsesLastValidDuplicate()
    {
        Directory.CreateDirectory(temporaryDirectory);
        var service = CreateService();
        string libraryPath = Path.Combine(temporaryDirectory, "Library");
        string duplicateLibraryPath = libraryPath + Path.DirectorySeparatorChar;
        File.WriteAllText(
            service.SettingsPath,
            $$"""
            {
              "version": 5,
              "ambiencePresets": [
                {
                  "libraryRootPath": {{JsonSerializer.Serialize(libraryPath)}},
                  "presets": [
                    {
                      "name": "Rain",
                      "tracks": [{ "relativePath": "old-rain.wav", "sourceVolume": 0.2 }]
                    }
                  ]
                },
                {
                  "libraryRootPath": {{JsonSerializer.Serialize(duplicateLibraryPath)}},
                  "presets": [
                    {
                      "name": "Wind",
                      "tracks": [{ "relativePath": "wind.wav", "sourceVolume": 0.6 }]
                    },
                    {
                      "name": "rain",
                      "tracks": [{ "relativePath": "new-rain.wav", "sourceVolume": 0.9 }]
                    }
                  ]
                }
              ]
            }
            """);

        SettingsLoadResult result = service.Load();

        Assert.IsNotNull(result.Warning);
        Assert.HasCount(1, result.Settings.AmbiencePresets);
        IReadOnlyList<AmbiencePreset> presets = result.Settings.AmbiencePresets[0].Presets;
        Assert.HasCount(2, presets);
        Assert.AreEqual("rain", presets[0].Name);
        Assert.AreEqual("new-rain.wav", presets[0].Tracks[0].RelativePath);
        Assert.AreEqual(0.9f, presets[0].Tracks[0].SourceVolume);
        Assert.AreEqual("Wind", presets[1].Name);
        Assert.AreEqual("wind.wav", presets[1].Tracks[0].RelativePath);
    }

    [TestMethod]
    public void SaveAndLoadRoundTripsPresetsForMultipleLibraries()
    {
        var service = CreateService();
        string firstLibrary = Path.Combine(temporaryDirectory, "FirstLibrary");
        string secondLibrary = Path.Combine(temporaryDirectory, "SecondLibrary");
        AppSettings settings = new()
        {
            AmbiencePresets = new[]
            {
                new LibraryAmbiencePresets(
                    firstLibrary + Path.DirectorySeparatorChar,
                    new[]
                    {
                        new AmbiencePreset(
                            "Night",
                            new[] { new AmbiencePresetTrack("rain.wav", 0.35f) }),
                    }),
                new LibraryAmbiencePresets(
                    secondLibrary,
                    new[]
                    {
                        new AmbiencePreset(
                            "Storm",
                            new[] { new AmbiencePresetTrack("wind.mp3", 0.8f) }),
                    }),
            },
        };

        service.Save(settings);
        AppSettings loaded = service.Load().Settings;

        Assert.HasCount(2, loaded.AmbiencePresets);
        Assert.AreEqual(
            Path.GetFullPath(firstLibrary),
            loaded.AmbiencePresets[0].LibraryRootPath);
        Assert.AreEqual("Night", loaded.AmbiencePresets[0].Presets[0].Name);
        Assert.AreEqual("rain.wav", loaded.AmbiencePresets[0].Presets[0].Tracks[0].RelativePath);
        Assert.AreEqual(0.35f, loaded.AmbiencePresets[0].Presets[0].Tracks[0].SourceVolume);
        Assert.AreEqual(
            Path.GetFullPath(secondLibrary),
            loaded.AmbiencePresets[1].LibraryRootPath);
        Assert.AreEqual("wind.mp3", loaded.AmbiencePresets[1].Presets[0].Tracks[0].RelativePath);
    }

    [TestMethod]
    public void LoadNormalizesCaseInsensitiveDuplicatePresetNamesWithLastEntryWinning()
    {
        Directory.CreateDirectory(temporaryDirectory);
        var service = CreateService();
        string libraryPath = Path.Combine(temporaryDirectory, "Library");
        File.WriteAllText(
            service.SettingsPath,
            $$"""
            {
              "version": 5,
              "ambiencePresets": [
                {
                  "libraryRootPath": {{JsonSerializer.Serialize(libraryPath)}},
                  "presets": [
                    { "name": " Rain ", "tracks": [{ "relativePath": "old.wav", "sourceVolume": 0.2 }] },
                    { "name": "rain", "tracks": [{ "relativePath": "new.wav", "sourceVolume": 0.7 }] }
                  ]
                }
              ]
            }
            """);

        SettingsLoadResult result = service.Load();

        Assert.IsNotNull(result.Warning);
        Assert.HasCount(1, result.Settings.AmbiencePresets);
        Assert.HasCount(1, result.Settings.AmbiencePresets[0].Presets);
        AmbiencePreset preset = result.Settings.AmbiencePresets[0].Presets[0];
        Assert.AreEqual("rain", preset.Name);
        Assert.AreEqual("new.wav", preset.Tracks[0].RelativePath);
        Assert.AreEqual(0.7f, preset.Tracks[0].SourceVolume);
    }

    [TestMethod]
    public void LoadInvalidOptionalPresetEntriesPreservesCoreSettingsWithWarning()
    {
        Directory.CreateDirectory(temporaryDirectory);
        var service = CreateService();
        string libraryPath = Path.Combine(temporaryDirectory, "Library");
        File.WriteAllText(
            service.SettingsPath,
            $$"""
            {
              "version": 5,
              "selectedLibraryPath": "C:\\Selected",
              "preferredImmediateTransitionMode": "Crossfade",
              "musicVolume": 0.25,
              "ambienceVolume": 0.5,
              "masterVolume": 0.75,
              "ambiencePresets": [
                {
                  "libraryRootPath": {{JsonSerializer.Serialize(libraryPath)}},
                  "presets": [
                    { "name": "Good", "tracks": [
                      { "relativePath": "../unsafe.wav", "sourceVolume": 0.2 },
                      { "relativePath": "missing.wav", "sourceVolume": 0.6 }
                    ] },
                    { "name": 42, "tracks": [] }
                  ]
                },
                "not an object"
              ]
            }
            """);

        SettingsLoadResult result = service.Load();

        Assert.IsNotNull(result.Warning);
        Assert.AreEqual(@"C:\Selected", result.Settings.SelectedLibraryPath);
        Assert.AreEqual(ImmediateTransitionMode.Crossfade, result.Settings.PreferredImmediateTransitionMode);
        Assert.AreEqual(0.25f, result.Settings.MusicVolume);
        Assert.AreEqual(0.5f, result.Settings.AmbienceVolume);
        Assert.AreEqual(0.75f, result.Settings.MasterVolume);
        Assert.HasCount(1, result.Settings.AmbiencePresets);
        Assert.HasCount(1, result.Settings.AmbiencePresets[0].Presets);
        Assert.AreEqual("missing.wav", result.Settings.AmbiencePresets[0].Presets[0].Tracks[0].RelativePath);
    }

    [TestMethod]
    public void SaveRejectsInvalidPresetWithoutReplacingExistingSettings()
    {
        var service = CreateService();
        service.Save(new AppSettings { SelectedLibraryPath = @"C:\Original" });
        string originalJson = File.ReadAllText(service.SettingsPath);

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            service.Save(new AppSettings
            {
                SelectedLibraryPath = @"C:\Replacement",
                AmbiencePresets = new[]
                {
                    new LibraryAmbiencePresets(
                        @"C:\Library",
                        new[]
                        {
                            new AmbiencePreset(
                                "Bad",
                                new[] { new AmbiencePresetTrack(@"..\unsafe.wav", 0.5f) }),
                        }),
                },
            }));

        Assert.AreEqual(originalJson, File.ReadAllText(service.SettingsPath));
    }

    [TestMethod]
    public void SaveAndLoadPreservesEmptyPresets()
    {
        var service = CreateService();
        string libraryPath = Path.Combine(temporaryDirectory, "Library");

        service.Save(new AppSettings
        {
            AmbiencePresets = new[]
            {
                new LibraryAmbiencePresets(libraryPath, Array.Empty<AmbiencePreset>()),
                new LibraryAmbiencePresets(
                    Path.Combine(temporaryDirectory, "Other"),
                    new[] { new AmbiencePreset("Empty", Array.Empty<AmbiencePresetTrack>()) }),
            },
        });

        AppSettings loaded = service.Load().Settings;

        Assert.HasCount(2, loaded.AmbiencePresets);
        Assert.IsEmpty(loaded.AmbiencePresets[0].Presets);
        Assert.IsEmpty(loaded.AmbiencePresets[1].Presets[0].Tracks);
    }

    [TestMethod]
    [DataRow("\"Unknown\"", "0.2", "0.4", "0.6")]
    [DataRow("\"Crossfade\"", "-0.1", "0.4", "0.6")]
    [DataRow("\"Crossfade\"", "1.1", "0.4", "0.6")]
    [DataRow("\"Crossfade\"", "NaN", "0.4", "0.6")]
    public void LoadVersionThreeDocumentWithInvalidModeOrVolumeUsesSafeDefaults(
        string modeJson,
        string musicVolumeJson,
        string ambienceVolumeJson,
        string masterVolumeJson)
    {
        Directory.CreateDirectory(temporaryDirectory);
        var service = CreateService();
        File.WriteAllText(
            service.SettingsPath,
            $$"""
            {
              "version": 3,
              "selectedLibraryPath": "C:\\Library",
              "preferredImmediateTransitionMode": {{modeJson}},
              "musicVolume": {{musicVolumeJson}},
              "ambienceVolume": {{ambienceVolumeJson}},
              "masterVolume": {{masterVolumeJson}}
            }
            """);

        SettingsLoadResult result = service.Load();

        Assert.AreEqual(AppSettings.CurrentVersion, result.Settings.Version);
        Assert.IsNull(result.Settings.SelectedLibraryPath);
        Assert.AreEqual(ImmediateTransitionMode.HardCut, result.Settings.PreferredImmediateTransitionMode);
        Assert.AreEqual(1.0f, result.Settings.MusicVolume);
        Assert.AreEqual(1.0f, result.Settings.AmbienceVolume);
        Assert.AreEqual(1.0f, result.Settings.MasterVolume);
        Assert.AreEqual(AppSettings.DefaultMediumFadeSeconds, result.Settings.MediumFadeSeconds);
        StringAssert.Contains(result.Warning!, "defaults");
    }

    [TestMethod]
    public void SaveCreatesParentDirectory()
    {
        var service = CreateService(Path.Combine("nested", "settings.json"));

        service.Save(AppSettings.CreateDefault());

        Assert.IsTrue(File.Exists(service.SettingsPath));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("not valid json")]
    public void LoadEmptyOrMalformedFileReturnsDefaultsWithWarning(string content)
    {
        Directory.CreateDirectory(temporaryDirectory);
        var service = CreateService();
        File.WriteAllText(service.SettingsPath, content);

        SettingsLoadResult result = service.Load();

        Assert.AreEqual(AppSettings.CurrentVersion, result.Settings.Version);
        Assert.IsNull(result.Settings.SelectedLibraryPath);
        Assert.AreEqual(
            ImmediateTransitionMode.HardCut,
            result.Settings.PreferredImmediateTransitionMode);
        StringAssert.Contains(result.Warning!, "defaults");
    }

    [TestMethod]
    [DataRow("\"Unknown\"")]
    [DataRow("99")]
    public void LoadInvalidModeReturnsDefaultsWithWarning(string modeJson)
    {
        Directory.CreateDirectory(temporaryDirectory);
        var service = CreateService();
        File.WriteAllText(
            service.SettingsPath,
            $$"""
            {
              "version": 2,
              "selectedLibraryPath": "C:\\Library",
              "preferredImmediateTransitionMode": {{modeJson}}
            }
            """);

        SettingsLoadResult result = service.Load();

        Assert.AreEqual(AppSettings.CurrentVersion, result.Settings.Version);
        Assert.IsNull(result.Settings.SelectedLibraryPath);
        Assert.AreEqual(
            ImmediateTransitionMode.HardCut,
            result.Settings.PreferredImmediateTransitionMode);
        StringAssert.Contains(result.Warning!, "defaults");
    }

    [TestMethod]
    [DataRow("-0.01")]
    [DataRow("1.01")]
    [DataRow("\"NaN\"")]
    [DataRow("\"Infinity\"")]
    public void LoadInvalidVolumeRecoversOnlyInvalidVolumeWithWarning(string volumeJson)
    {
        Directory.CreateDirectory(temporaryDirectory);
        var service = CreateService();
        File.WriteAllText(
            service.SettingsPath,
            $$"""
            {
              "version": 5,
              "selectedLibraryPath": "C:\\Library",
              "preferredImmediateTransitionMode": "Crossfade",
              "musicVolume": {{volumeJson}},
              "ambienceVolume": 0.5,
              "masterVolume": 1
            }
            """);

        SettingsLoadResult result = service.Load();

        Assert.AreEqual(AppSettings.CurrentVersion, result.Settings.Version);
        Assert.AreEqual(@"C:\Library", result.Settings.SelectedLibraryPath);
        Assert.AreEqual(ImmediateTransitionMode.Crossfade, result.Settings.PreferredImmediateTransitionMode);
        Assert.AreEqual(1.0f, result.Settings.MusicVolume);
        Assert.AreEqual(0.5f, result.Settings.AmbienceVolume);
        Assert.AreEqual(1.0f, result.Settings.MasterVolume);
        StringAssert.Contains(result.Warning!, "musicVolume");
    }

    [TestMethod]
    public void LoadUnknownFutureVersionReturnsDefaultsWithWarning()
    {
        Directory.CreateDirectory(temporaryDirectory);
        var service = CreateService();
        File.WriteAllText(
            service.SettingsPath,
            """
            {
              "version": 99,
              "selectedLibraryPath": "C:\\OldLibrary"
            }
            """);

        SettingsLoadResult result = service.Load();

        Assert.AreEqual(AppSettings.CurrentVersion, result.Settings.Version);
        Assert.IsNull(result.Settings.SelectedLibraryPath);
        Assert.AreEqual(
            ImmediateTransitionMode.HardCut,
            result.Settings.PreferredImmediateTransitionMode);
        StringAssert.Contains(result.Warning!, "not supported");
    }

    [TestMethod]
    public void SaveOverwritesExistingSettings()
    {
        var service = CreateService();
        string firstPath = Path.Combine(temporaryDirectory, "First");
        string updatedPath = Path.Combine(temporaryDirectory, "Updated");

        service.Save(new AppSettings
        {
            SelectedLibraryPath = firstPath,
            PreferredImmediateTransitionMode = ImmediateTransitionMode.HardCut,
        });
        service.Save(new AppSettings
        {
            SelectedLibraryPath = updatedPath,
            PreferredImmediateTransitionMode = ImmediateTransitionMode.Crossfade,
        });

        SettingsLoadResult result = service.Load();
        Assert.IsNull(result.Warning);
        Assert.AreEqual(updatedPath, result.Settings.SelectedLibraryPath);
        Assert.AreEqual(
            ImmediateTransitionMode.Crossfade,
            result.Settings.PreferredImmediateTransitionMode);
    }

    [TestMethod]
    public void SaveRejectsInvalidMode()
    {
        var service = CreateService();

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            service.Save(new AppSettings
            {
                SelectedLibraryPath = "C:\\Library",
                PreferredImmediateTransitionMode = (ImmediateTransitionMode)99,
            }));

        Assert.IsFalse(File.Exists(service.SettingsPath));
    }

    [TestMethod]
    [DataRow(-0.01f)]
    [DataRow(1.01f)]
    [DataRow(float.NaN)]
    [DataRow(float.PositiveInfinity)]
    public void SaveRejectsInvalidVolumeWithoutWriting(float volume)
    {
        var service = CreateService();

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            service.Save(new AppSettings
            {
                MusicVolume = volume,
            }));

        Assert.IsFalse(File.Exists(service.SettingsPath));
    }

    [TestMethod]
    public void SaveRejectsInvalidVolumeBeforeReplacingExistingSettings()
    {
        var service = CreateService();
        service.Save(new AppSettings { SelectedLibraryPath = "C:\\Original" });
        string originalJson = File.ReadAllText(service.SettingsPath);

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            service.Save(new AppSettings
            {
                SelectedLibraryPath = "C:\\Replacement",
                MasterVolume = float.NegativeInfinity,
            }));

        Assert.AreEqual(originalJson, File.ReadAllText(service.SettingsPath));
    }

    [TestMethod]
    public void SavePreservesValidFractionalTimings()
    {
        var service = CreateService();
        var settings = new AppSettings
        {
            SelectedLibraryPath = @"C:\Library",
            FastFadeSeconds = 0.25f,
            MediumFadeSeconds = 3.5f,
            SlowFadeSeconds = 12.75f,
            CrossfadeStaggerSeconds = 3.25f,
        };

        service.Save(settings);

        AppSettings result = service.Load().Settings;
        Assert.AreEqual(0.25f, result.FastFadeSeconds);
        Assert.AreEqual(3.5f, result.MediumFadeSeconds);
        Assert.AreEqual(12.75f, result.SlowFadeSeconds);
        Assert.AreEqual(3.25f, result.CrossfadeStaggerSeconds);
    }

    [TestMethod]
    [DataRow("FastFadeSeconds", -1.0f)]
    [DataRow("MediumFadeSeconds", 0.0f)]
    [DataRow("SlowFadeSeconds", float.PositiveInfinity)]
    public void SaveRejectsInvalidTimingWithoutWriting(string propertyName, float value)
    {
        var service = CreateService();
        AppSettings settings = new()
        {
            FastFadeSeconds = propertyName == nameof(AppSettings.FastFadeSeconds) ? value : 2.0f,
            MediumFadeSeconds = propertyName == nameof(AppSettings.MediumFadeSeconds) ? value : 5.0f,
            SlowFadeSeconds = propertyName == nameof(AppSettings.SlowFadeSeconds) ? value : 10.0f,
        };

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => service.Save(settings));
        Assert.IsFalse(File.Exists(service.SettingsPath));
    }

    [TestMethod]
    [DataRow(-1.0f)]
    [DataRow(float.NaN)]
    [DataRow(float.PositiveInfinity)]
    public void SaveRejectsInvalidStaggerWithoutWriting(float stagger)
    {
        var service = CreateService();

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => service.Save(new AppSettings
        {
            MediumFadeSeconds = 2.0f,
            CrossfadeStaggerSeconds = stagger,
        }));

        Assert.IsFalse(File.Exists(service.SettingsPath));
    }

    [TestMethod]
    public void SaveRejectsStaggerExceedingMediumWithoutReplacingExistingSettings()
    {
        var service = CreateService();
        service.Save(new AppSettings { SelectedLibraryPath = @"C:\Original" });
        string originalJson = File.ReadAllText(service.SettingsPath);

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => service.Save(new AppSettings
        {
            SelectedLibraryPath = @"C:\Replacement",
            MediumFadeSeconds = 3.5f,
            CrossfadeStaggerSeconds = 4.25f,
        }));

        Assert.AreEqual(originalJson, File.ReadAllText(service.SettingsPath));
    }

    private SettingsService CreateService(string relativePath = "settings.json") =>
        new(Path.Combine(temporaryDirectory, relativePath));
}

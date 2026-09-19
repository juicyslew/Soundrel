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
    public void CreateDefaultReturnsVersionThreeHardCutSettingsWithFullVolumes()
    {
        AppSettings settings = AppSettings.CreateDefault();

        Assert.AreEqual(3, settings.Version);
        Assert.IsNull(settings.SelectedLibraryPath);
        Assert.AreEqual(
            ImmediateTransitionMode.HardCut,
            settings.PreferredImmediateTransitionMode);
        Assert.AreEqual(1.0f, settings.MusicVolume);
        Assert.AreEqual(1.0f, settings.AmbienceVolume);
        Assert.AreEqual(1.0f, settings.MasterVolume);
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
        Assert.AreEqual(3, document.RootElement.GetProperty("version").GetInt32());
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
    [DataRow("NaN")]
    [DataRow("Infinity")]
    public void LoadInvalidVolumeReturnsDefaultsWithWarning(string volumeJson)
    {
        Directory.CreateDirectory(temporaryDirectory);
        var service = CreateService();
        File.WriteAllText(
            service.SettingsPath,
            $$"""
            {
              "version": 3,
              "selectedLibraryPath": "C:\\Library",
              "preferredImmediateTransitionMode": "Crossfade",
              "musicVolume": {{volumeJson}},
              "ambienceVolume": 0.5,
              "masterVolume": 1
            }
            """);

        SettingsLoadResult result = service.Load();

        Assert.AreEqual(AppSettings.CurrentVersion, result.Settings.Version);
        Assert.IsNull(result.Settings.SelectedLibraryPath);
        Assert.AreEqual(ImmediateTransitionMode.HardCut, result.Settings.PreferredImmediateTransitionMode);
        Assert.AreEqual(1.0f, result.Settings.MusicVolume);
        Assert.AreEqual(1.0f, result.Settings.AmbienceVolume);
        Assert.AreEqual(1.0f, result.Settings.MasterVolume);
        StringAssert.Contains(result.Warning!, "defaults");
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

    private SettingsService CreateService(string relativePath = "settings.json") =>
        new(Path.Combine(temporaryDirectory, relativePath));
}

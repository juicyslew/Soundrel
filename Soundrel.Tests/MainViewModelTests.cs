using System.Text.Json;
using Soundrel.Models;
using Soundrel.Services;
using Soundrel.ViewModels;

namespace Soundrel.Tests;

[TestClass]
public sealed class MainViewModelTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    private TemporaryWorkspace workspace = null!;

    [TestInitialize]
    public void Initialize()
    {
        workspace = new TemporaryWorkspace();
    }

    [TestCleanup]
    public void Cleanup()
    {
        workspace.Dispose();
    }

    [TestMethod]
    public async Task InitializeAsync_RestoresAndScansPersistedLibrary()
    {
        string libraryPath = workspace.CreateDirectory("Library");
        workspace.CreateFile("Library", "Music", "Tavern", "lute.mp3");
        var settings = workspace.CreateSettingsService();
        settings.Save(new AppSettings { SelectedLibraryPath = libraryPath });
        var viewModel = new MainViewModel(new LibraryScanner(), settings);

        await viewModel.InitializeAsync();

        Assert.AreEqual(libraryPath, viewModel.SelectedLibraryPath);
        Assert.HasCount(1, viewModel.LibraryNodes);
        Assert.AreEqual("Tavern", viewModel.LibraryNodes[0].Name);
        Assert.IsTrue(viewModel.LibraryNodes[0].IsPlaylist);
        Assert.IsFalse(viewModel.IsBusy);
        StringAssert.Contains(viewModel.StatusText, "Library loaded");
    }

    [TestMethod]
    public async Task SelectLibraryAsync_PopulatesCatalogAndPersistsPath()
    {
        string libraryPath = workspace.CreateDirectory("ChosenLibrary");
        workspace.CreateFile("ChosenLibrary", "Music", "Travel", "road.wav");
        workspace.CreateFile("ChosenLibrary", "Ambience", "rain.mp3");
        var settings = workspace.CreateSettingsService();
        var viewModel = new MainViewModel(new LibraryScanner(), settings);

        await viewModel.SelectLibraryAsync(libraryPath);

        Assert.AreEqual(libraryPath, viewModel.SelectedLibraryPath);
        Assert.HasCount(1, viewModel.LibraryNodes);
        Assert.HasCount(1, viewModel.AmbienceTracks);
        Assert.AreEqual("rain", viewModel.AmbienceTracks[0].Name);
        Assert.AreEqual(libraryPath, settings.Load().Settings.SelectedLibraryPath);
    }

    [TestMethod]
    public async Task SelectLibraryAsync_BuildsNestedGroupsAndIncludesRootPlaylists()
    {
        string libraryPath = workspace.CreateDirectory("NestedLibrary");
        workspace.CreateFile("NestedLibrary", "Music", "World", "Forest", "Calm", "birds.mp3");
        workspace.CreateFile("NestedLibrary", "Music", "Tavern", "crowd.wav");
        var viewModel = workspace.CreateViewModel();

        await viewModel.SelectLibraryAsync(libraryPath);

        Assert.HasCount(2, viewModel.LibraryNodes);
        LibraryTreeNode world = viewModel.LibraryNodes.Single(node => node.Name == "World");
        LibraryTreeNode tavern = viewModel.LibraryNodes.Single(node => node.Name == "Tavern");
        Assert.IsFalse(world.IsPlaylist);
        Assert.IsTrue(tavern.IsPlaylist);
        LibraryTreeNode forest = AssertSingle(world.Children);
        Assert.AreEqual("Forest", forest.Name);
        Assert.IsFalse(forest.IsPlaylist);
        LibraryTreeNode calm = AssertSingle(forest.Children);
        Assert.AreEqual("Calm", calm.Name);
        Assert.IsTrue(calm.IsPlaylist);
    }

    [TestMethod]
    public async Task SelectNode_PopulatesSelectedPlaylistTracks()
    {
        string libraryPath = workspace.CreateDirectory("TrackLibrary");
        workspace.CreateFile("TrackLibrary", "Music", "Battle", "Charge.MP3");
        workspace.CreateFile("TrackLibrary", "Music", "Battle", "Victory.wav");
        var viewModel = workspace.CreateViewModel();
        await viewModel.SelectLibraryAsync(libraryPath);

        viewModel.SelectNode(viewModel.LibraryNodes.Single());

        Assert.AreEqual("Battle", viewModel.SelectedPlaylistName);
        CollectionAssert.AreEqual(
            new[] { "Charge", "Victory" },
            viewModel.SelectedTracks.Select(track => track.Name).ToArray());
    }

    [TestMethod]
    public async Task SelectNode_GroupAfterPlaylistClearsTrackSelection()
    {
        string libraryPath = workspace.CreateDirectory("SelectionLibrary");
        workspace.CreateFile("SelectionLibrary", "Music", "Battle", "charge.mp3");
        workspace.CreateFile("SelectionLibrary", "Music", "World", "Calm", "rest.wav");
        var viewModel = workspace.CreateViewModel();
        await viewModel.SelectLibraryAsync(libraryPath);
        LibraryTreeNode playlist = viewModel.LibraryNodes.Single(node => node.Name == "Battle");
        LibraryTreeNode group = viewModel.LibraryNodes.Single(node => node.Name == "World");

        viewModel.SelectNode(playlist);
        viewModel.SelectNode(group);

        Assert.AreEqual("No playlist selected", viewModel.SelectedPlaylistName);
        Assert.IsEmpty(viewModel.SelectedTracks);
    }

    [TestMethod]
    public async Task InitializeAsync_MissingSettingsIsNonfatalAndPromptsForLibrary()
    {
        var viewModel = workspace.CreateViewModel();

        await viewModel.InitializeAsync();

        Assert.IsNull(viewModel.SelectedLibraryPath);
        Assert.IsEmpty(viewModel.LibraryNodes);
        StringAssert.Contains(viewModel.StatusText, "not found");
        StringAssert.Contains(viewModel.StatusText, "Select a library folder");
    }

    [TestMethod]
    [DataRow(ImmediateTransitionMode.HardCut)]
    [DataRow(ImmediateTransitionMode.Crossfade)]
    public async Task InitializeAsync_LoadsTransitionModeWithoutSelectedLibrary(
        ImmediateTransitionMode mode)
    {
        SettingsService settings = workspace.CreateSettingsService();
        settings.Save(new AppSettings
        {
            PreferredImmediateTransitionMode = mode,
        });
        var viewModel = new MainViewModel(new LibraryScanner(), settings);

        try
        {
            await viewModel.InitializeAsync();

            Assert.IsNull(viewModel.SelectedLibraryPath);
            Assert.AreEqual(mode, viewModel.PreferredImmediateTransitionMode);
            Assert.AreEqual(mode == ImmediateTransitionMode.HardCut, viewModel.IsHardCutSelected);
            Assert.AreEqual(mode == ImmediateTransitionMode.Crossfade, viewModel.IsCrossfadeSelected);
            Assert.AreEqual(
                mode == ImmediateTransitionMode.HardCut
                    ? "Play Now (Hard Cut)"
                    : "Play Now (Crossfade)",
                viewModel.PlayNowLabel);
        }
        finally
        {
            await viewModel.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task InitializeAsync_LoadsPersistedV3LevelsBeforeScanning()
    {
        string libraryPath = workspace.CreateDirectory("V3Library");
        workspace.CreateFile("V3Library", "Music", "Tavern", "lute.mp3");
        SettingsService settings = workspace.CreateSettingsService();
        settings.Save(new AppSettings
        {
            Version = AppSettings.CurrentVersion,
            SelectedLibraryPath = libraryPath,
            PreferredImmediateTransitionMode = ImmediateTransitionMode.Crossfade,
            MusicVolume = 0.25f,
            AmbienceVolume = 0.5f,
            MasterVolume = 0.75f,
        });
        var viewModel = new MainViewModel(new LibraryScanner(), settings);

        try
        {
            await viewModel.InitializeAsync();

            Assert.AreEqual(ImmediateTransitionMode.Crossfade, viewModel.PreferredImmediateTransitionMode);
            Assert.AreEqual(0.25f, viewModel.MusicVolume);
            Assert.AreEqual(0.5f, viewModel.AmbienceVolume);
            Assert.AreEqual(0.75f, viewModel.MasterVolume);
            Assert.HasCount(1, viewModel.LibraryNodes);
        }
        finally
        {
            await viewModel.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task PathAndModeSavesPreserveAllVolumeLevels()
    {
        string libraryPath = workspace.CreateDirectory("LevelLibrary");
        SettingsService settings = workspace.CreateSettingsService();
        var viewModel = new MainViewModel(new LibraryScanner(), settings);

        try
        {
            await viewModel.SetMusicVolumeAsync(0.2f);
            await viewModel.SetAmbienceVolumeAsync(0.4f);
            await viewModel.SetMasterVolumeAsync(0.6f);
            await viewModel.SelectLibraryAsync(libraryPath);
            await viewModel.SetImmediateTransitionModeAsync(ImmediateTransitionMode.Crossfade);

            AppSettings persisted = settings.Load().Settings;
            Assert.AreEqual(libraryPath, persisted.SelectedLibraryPath);
            Assert.AreEqual(ImmediateTransitionMode.Crossfade, persisted.PreferredImmediateTransitionMode);
            Assert.AreEqual(0.2f, persisted.MusicVolume);
            Assert.AreEqual(0.4f, persisted.AmbienceVolume);
            Assert.AreEqual(0.6f, persisted.MasterVolume);
        }
        finally
        {
            await viewModel.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task SetImmediateTransitionMode_UpdatesBindingsAndPersistsExistingPath()
    {
        string libraryPath = workspace.CreateDirectory("ModeLibrary");
        SettingsService settings = workspace.CreateSettingsService();
        var viewModel = new MainViewModel(new LibraryScanner(), settings);
        var changedProperties = new List<string?>();
        viewModel.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);

        try
        {
            await viewModel.SelectLibraryAsync(libraryPath);
            changedProperties.Clear();

            await viewModel.SetImmediateTransitionModeAsync(ImmediateTransitionMode.Crossfade);

            Assert.AreEqual(
                ImmediateTransitionMode.Crossfade,
                viewModel.PreferredImmediateTransitionMode);
            Assert.IsFalse(viewModel.IsHardCutSelected);
            Assert.IsTrue(viewModel.IsCrossfadeSelected);
            Assert.AreEqual("Play Now (Crossfade)", viewModel.PlayNowLabel);
            CollectionAssert.Contains(
                changedProperties,
                nameof(MainViewModel.PreferredImmediateTransitionMode));
            CollectionAssert.Contains(changedProperties, nameof(MainViewModel.IsHardCutSelected));
            CollectionAssert.Contains(changedProperties, nameof(MainViewModel.IsCrossfadeSelected));
            CollectionAssert.Contains(changedProperties, nameof(MainViewModel.PlayNowLabel));

            AppSettings persisted = settings.Load().Settings;
            Assert.AreEqual(AppSettings.CurrentVersion, persisted.Version);
            Assert.AreEqual(libraryPath, persisted.SelectedLibraryPath);
            Assert.AreEqual(
                ImmediateTransitionMode.Crossfade,
                persisted.PreferredImmediateTransitionMode);
        }
        finally
        {
            await viewModel.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task ConcurrentModeAndLibrarySavesPreserveLatestPathAndModeInInvocationOrder()
    {
        string firstLibraryPath = workspace.CreateDirectory("FirstLibrary");
        string secondLibraryPath = workspace.CreateDirectory("SecondLibrary");
        SettingsService settings = workspace.CreateSettingsService();
        var viewModel = new MainViewModel(new LibraryScanner(), settings);

        try
        {
            Task selectFirst = viewModel.SelectLibraryAsync(firstLibraryPath);
            Task selectCrossfade = viewModel.SetImmediateTransitionModeAsync(
                ImmediateTransitionMode.Crossfade);
            await Task.WhenAll(selectFirst, selectCrossfade).WaitAsync(TestTimeout);

            AppSettings firstResult = settings.Load().Settings;
            Assert.AreEqual(firstLibraryPath, firstResult.SelectedLibraryPath);
            Assert.AreEqual(
                ImmediateTransitionMode.Crossfade,
                firstResult.PreferredImmediateTransitionMode);

            Task selectHardCut = viewModel.SetImmediateTransitionModeAsync(
                ImmediateTransitionMode.HardCut);
            Task selectSecond = viewModel.SelectLibraryAsync(secondLibraryPath);
            await Task.WhenAll(selectHardCut, selectSecond).WaitAsync(TestTimeout);

            AppSettings secondResult = settings.Load().Settings;
            Assert.AreEqual(secondLibraryPath, secondResult.SelectedLibraryPath);
            Assert.AreEqual(
                ImmediateTransitionMode.HardCut,
                secondResult.PreferredImmediateTransitionMode);
        }
        finally
        {
            await viewModel.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task TransitionModeSaveFailureIsVisibleWithoutRevertingSelection()
    {
        string blockedDirectory = workspace.CreateFile("settings-parent");
        var settings = new SettingsService(Path.Combine(blockedDirectory, "settings.json"));
        var viewModel = new MainViewModel(new LibraryScanner(), settings);

        try
        {
            await viewModel.SetImmediateTransitionModeAsync(ImmediateTransitionMode.Crossfade);

            Assert.AreEqual(
                ImmediateTransitionMode.Crossfade,
                viewModel.PreferredImmediateTransitionMode);
            Assert.IsTrue(viewModel.IsCrossfadeSelected);
            StringAssert.Contains(viewModel.StatusText, "could not be saved");
        }
        finally
        {
            await viewModel.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task InitializeAsync_MalformedSettingsIsNonfatalAndVisible()
    {
        SettingsService settings = workspace.CreateSettingsService();
        workspace.CreateFileWithContents("not valid json", "settings.json");
        var viewModel = new MainViewModel(new LibraryScanner(), settings);

        await viewModel.InitializeAsync();

        Assert.IsNull(viewModel.SelectedLibraryPath);
        Assert.IsEmpty(viewModel.LibraryNodes);
        StringAssert.Contains(viewModel.StatusText, "malformed");
        StringAssert.Contains(viewModel.StatusText, "Select a library folder");
    }

    [TestMethod]
    public async Task InitializeAsync_MissingPersistedLibraryReportsScanIssueWithoutThrowing()
    {
        string missingLibraryPath = Path.Combine(workspace.RootPath, "MissingLibrary");
        SettingsService settings = workspace.CreateSettingsService();
        settings.Save(new AppSettings { SelectedLibraryPath = missingLibraryPath });
        var viewModel = new MainViewModel(new LibraryScanner(), settings);

        await viewModel.InitializeAsync();

        Assert.AreEqual(missingLibraryPath, viewModel.SelectedLibraryPath);
        Assert.HasCount(1, viewModel.ScanIssues);
        StringAssert.Contains(viewModel.ScanIssues[0].Message, "does not exist");
        StringAssert.Contains(viewModel.StatusText, "does not exist");
        Assert.IsFalse(viewModel.IsBusy);
    }

    [TestMethod]
    public async Task SelectLibraryAsync_ExposesWrongLayoutIssueAndClearsItForValidLibrary()
    {
        string wrongLayoutPath = workspace.CreateDirectory("WrongLayout");
        workspace.CreateFile("WrongLayout", "Choral", "hymn.mp3");
        workspace.CreateFile("WrongLayout", "Themes", "hero.wav");
        workspace.CreateFile("WrongLayout", "Watery Chill", "drip.mp3");
        string validLibraryPath = workspace.CreateDirectory("ValidLibrary");
        workspace.CreateFile("ValidLibrary", "Music", "Tavern", "lute.mp3");
        var viewModel = workspace.CreateViewModel();
        var changedProperties = new List<string?>();
        viewModel.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);

        await viewModel.SelectLibraryAsync(wrongLayoutPath);

        Assert.IsTrue(viewModel.HasScanIssues);
        Assert.HasCount(1, viewModel.ScanIssues);
        StringAssert.Contains(viewModel.StatusText, Path.Combine(wrongLayoutPath, "Music"));
        StringAssert.Contains(viewModel.StatusText, Path.Combine(wrongLayoutPath, "Ambience"));
        CollectionAssert.Contains(changedProperties, nameof(MainViewModel.HasScanIssues));

        changedProperties.Clear();
        await viewModel.SelectLibraryAsync(validLibraryPath);

        Assert.IsFalse(viewModel.HasScanIssues);
        Assert.IsEmpty(viewModel.ScanIssues);
        StringAssert.Contains(viewModel.StatusText, "Library loaded");
        CollectionAssert.Contains(changedProperties, nameof(MainViewModel.HasScanIssues));
    }

    private static LibraryTreeNode AssertSingle(IReadOnlyList<LibraryTreeNode> nodes)
    {
        Assert.HasCount(1, nodes);
        return nodes[0];
    }

    private sealed class TemporaryWorkspace : IDisposable
    {
        public TemporaryWorkspace()
        {
            RootPath = Path.Combine(Path.GetTempPath(), "Soundrel.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(RootPath);
        }

        public string RootPath { get; }

        public SettingsService CreateSettingsService() =>
            new(Path.Combine(RootPath, "settings.json"));

        public MainViewModel CreateViewModel() =>
            new(new LibraryScanner(), CreateSettingsService());

        public string CreateDirectory(params string[] relativeParts)
        {
            string path = Combine(relativeParts);
            Directory.CreateDirectory(path);
            return Path.GetFullPath(path);
        }

        public string CreateFile(params string[] relativeParts)
        {
            string path = Combine(relativeParts);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, []);
            return Path.GetFullPath(path);
        }

        public string CreateFileWithContents(string contents, params string[] relativeParts)
        {
            string path = Combine(relativeParts);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, contents);
            return Path.GetFullPath(path);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(RootPath, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private string Combine(IEnumerable<string> relativeParts) =>
            relativeParts.Aggregate(RootPath, Path.Combine);
    }
}

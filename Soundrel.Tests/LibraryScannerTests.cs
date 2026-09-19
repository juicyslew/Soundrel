using System.IO;
using Soundrel.Services;

namespace Soundrel.Tests;

[TestClass]
public sealed class LibraryScannerTests
{
    [TestMethod]
    public void Scan_DiscoversDirectPlaylistsAndNestedGroups()
    {
        using var library = new TemporaryLibrary();
        library.CreateFile("Music", "Forest", "Calm", "morning.mp3");
        library.CreateFile("Music", "Forest", "Tense", "danger.wav");
        library.CreateFile("Music", "Tavern", "minstrels.mp3");

        var catalog = new LibraryScanner().Scan(library.RootPath);

        CollectionAssert.AreEqual(new[] { "Forest" }, catalog.Groups.Select(group => group.Name).ToArray());
        CollectionAssert.AreEqual(new[] { "Tavern" }, catalog.Playlists.Select(playlist => playlist.Name).ToArray());
        CollectionAssert.AreEqual(
            new[] { "Calm", "Tense" },
            catalog.Groups[0].Playlists.Select(playlist => playlist.Name).ToArray());
        Assert.AreEqual("morning", catalog.Groups[0].Playlists[0].Tracks[0].Name);
        Assert.AreEqual("minstrels", catalog.Playlists[0].Tracks[0].Name);
        Assert.IsEmpty(catalog.Issues);
    }

    [TestMethod]
    public void Scan_DirectoryWithDirectTracksAndChildPlaylistsPreservesBoth()
    {
        using var library = new TemporaryLibrary();
        library.CreateFile("Music", "Forest", "theme.mp3");
        library.CreateFile("Music", "Forest", "Calm", "morning.wav");

        var catalog = new LibraryScanner().Scan(library.RootPath);

        Assert.HasCount(1, catalog.Groups);
        CollectionAssert.AreEqual(
            new[] { "Calm", "Forest" },
            catalog.Groups[0].Playlists.Select(playlist => playlist.Name).ToArray());
        Assert.AreEqual("morning", catalog.Groups[0].Playlists[0].Tracks.Single().Name);
        Assert.AreEqual("theme", catalog.Groups[0].Playlists[1].Tracks.Single().Name);
        Assert.IsEmpty(catalog.Issues);
    }

    [TestMethod]
    public void Scan_FiltersExtensionsCaseInsensitivelyAndPreservesFullPaths()
    {
        using var library = new TemporaryLibrary();
        var mp3Path = library.CreateFile("Music", "Mixed", "Quiet Song.MP3");
        var wavPath = library.CreateFile("Music", "Mixed", "Storm.WaV");
        library.CreateFile("Music", "Mixed", "notes.txt");
        library.CreateFile("Music", "Mixed", "other.ogg");

        var catalog = new LibraryScanner().Scan(library.RootPath);
        var tracks = catalog.Playlists[0].Tracks;

        CollectionAssert.AreEqual(new[] { "Quiet Song", "Storm" }, tracks.Select(track => track.Name).ToArray());
        CollectionAssert.AreEqual(new[] { mp3Path, wavPath }, tracks.Select(track => track.FilePath).ToArray());
    }

    [TestMethod]
    public void Scan_IncludesOnlyDirectSupportedAmbienceFiles()
    {
        using var library = new TemporaryLibrary();
        library.CreateFile("Ambience", "wind.mp3");
        library.CreateFile("Ambience", "Rain.WAV");
        library.CreateFile("Ambience", "credits.txt");
        library.CreateFile("Ambience", "Nested", "hidden.wav");

        var catalog = new LibraryScanner().Scan(library.RootPath);

        CollectionAssert.AreEqual(
            new[] { "Rain", "wind" },
            catalog.AmbienceTracks.Select(track => track.Name).ToArray());
    }

    [TestMethod]
    public void Scan_OrdersGroupsPlaylistsTracksAndAmbienceDeterministically()
    {
        using var library = new TemporaryLibrary();
        library.CreateDirectory("Music", "zulu-group", "child");
        library.CreateDirectory("Music", "Alpha-group", "child");
        library.CreateFile("Music", "zeta-playlist", "Zulu.wav");
        library.CreateFile("Music", "beta-playlist", "middle.mp3");
        library.CreateFile("Music", "beta-playlist", "Alpha.wav");
        library.CreateFile("Ambience", "wind.wav");
        library.CreateFile("Ambience", "Brook.mp3");

        var scanner = new LibraryScanner();
        var first = scanner.Scan(library.RootPath);
        var second = scanner.Scan(library.RootPath);

        CollectionAssert.AreEqual(
            new[] { "Alpha-group", "zulu-group" },
            first.Groups.Select(group => group.Name).ToArray());
        CollectionAssert.AreEqual(
            new[] { "beta-playlist", "zeta-playlist" },
            first.Playlists.Select(playlist => playlist.Name).ToArray());
        CollectionAssert.AreEqual(
            new[] { "Alpha", "middle" },
            first.Playlists[0].Tracks.Select(track => track.Name).ToArray());
        CollectionAssert.AreEqual(
            new[] { "Brook", "wind" },
            first.AmbienceTracks.Select(track => track.Name).ToArray());
        CollectionAssert.AreEqual(
            first.Groups.Select(group => group.DirectoryPath).ToArray(),
            second.Groups.Select(group => group.DirectoryPath).ToArray());
        CollectionAssert.AreEqual(
            first.Playlists.Select(playlist => playlist.DirectoryPath).ToArray(),
            second.Playlists.Select(playlist => playlist.DirectoryPath).ToArray());
    }

    [TestMethod]
    public void Scan_RepresentsEmptyLeafDirectoryAsPlaylist()
    {
        using var library = new TemporaryLibrary();
        var emptyPath = library.CreateDirectory("Music", "Silence");

        var catalog = new LibraryScanner().Scan(library.RootPath);

        Assert.HasCount(1, catalog.Playlists);
        Assert.AreEqual("Silence", catalog.Playlists[0].Name);
        Assert.AreEqual(emptyPath, catalog.Playlists[0].DirectoryPath);
        Assert.IsEmpty(catalog.Playlists[0].Tracks);
    }

    [TestMethod]
    public void Scan_MissingRootOrOptionalAmbienceReturnsEmptyOrPartialCatalog()
    {
        using var library = new TemporaryLibrary();
        var missingRoot = Path.Combine(library.RootPath, "missing");

        var missingCatalog = new LibraryScanner().Scan(missingRoot);

        Assert.IsEmpty(missingCatalog.Groups);
        Assert.IsEmpty(missingCatalog.Playlists);
        Assert.IsEmpty(missingCatalog.AmbienceTracks);
        Assert.HasCount(1, missingCatalog.Issues);

        library.CreateFile("Music", "Solo", "track.mp3");
        var musicOnlyCatalog = new LibraryScanner().Scan(library.RootPath);

        Assert.HasCount(1, musicOnlyCatalog.Playlists);
        Assert.IsEmpty(musicOnlyCatalog.AmbienceTracks);
        Assert.IsEmpty(musicOnlyCatalog.Issues);
    }

    [TestMethod]
    public void Scan_PlaylistLookingFoldersOutsideExpectedLayoutReportActionableIssue()
    {
        using var library = new TemporaryLibrary();
        library.CreateFile("Choral", "hymn.mp3");
        library.CreateFile("Themes", "hero.wav");
        library.CreateFile("Watery Chill", "drip.mp3");

        var catalog = new LibraryScanner().Scan(library.RootPath);

        Assert.IsEmpty(catalog.Groups);
        Assert.IsEmpty(catalog.Playlists);
        Assert.IsEmpty(catalog.AmbienceTracks);
        Assert.HasCount(1, catalog.Issues);
        StringAssert.Contains(catalog.Issues[0].Message, Path.Combine(library.RootPath, "Music"));
        StringAssert.Contains(catalog.Issues[0].Message, Path.Combine(library.RootPath, "Ambience"));
    }

    [TestMethod]
    public void Scan_EmptyMusicFolderReportsPlaylistSubfolderGuidance()
    {
        using var library = new TemporaryLibrary();
        string musicPath = library.CreateDirectory("Music");

        var catalog = new LibraryScanner().Scan(library.RootPath);

        Assert.IsEmpty(catalog.Groups);
        Assert.IsEmpty(catalog.Playlists);
        Assert.HasCount(1, catalog.Issues);
        StringAssert.Contains(catalog.Issues[0].Message, Path.Combine(musicPath, "Tavern"));
        StringAssert.Contains(catalog.Issues[0].Message, "MP3/WAV");
    }

    [TestMethod]
    public void Scan_AmbienceOnlyLibraryDoesNotReportMissingMusicOrPlaylists()
    {
        using var library = new TemporaryLibrary();
        library.CreateFile("Ambience", "rain.mp3");

        var catalog = new LibraryScanner().Scan(library.RootPath);

        Assert.HasCount(1, catalog.AmbienceTracks);
        Assert.IsEmpty(catalog.Groups);
        Assert.IsEmpty(catalog.Playlists);
        Assert.IsEmpty(catalog.Issues);
    }

    [TestMethod]
    public void Scan_ValidMusicHierarchyDoesNotReportLayoutIssue()
    {
        using var library = new TemporaryLibrary();
        library.CreateFile("Music", "World", "Tavern", "lute.mp3");

        var catalog = new LibraryScanner().Scan(library.RootPath);

        Assert.HasCount(1, catalog.Groups);
        Assert.HasCount(1, catalog.Groups[0].Playlists);
        Assert.IsEmpty(catalog.Issues);
    }

    [TestMethod]
    public void Scan_InvalidPathReportsIssueInsteadOfThrowing()
    {
        var invalidPath = "invalid\0path";

        var catalog = new LibraryScanner().Scan(invalidPath);

        Assert.IsEmpty(catalog.Groups);
        Assert.IsEmpty(catalog.Playlists);
        Assert.IsEmpty(catalog.AmbienceTracks);
        Assert.HasCount(1, catalog.Issues);
        StringAssert.Contains(catalog.Issues[0].Message, "Could not use library path");
    }

    [TestMethod]
    public void Scan_UncRootReportsLocalFolderIssue()
    {
        const string uncPath = @"\\soundrel.invalid\library";

        var catalog = new LibraryScanner().Scan(uncPath);

        Assert.IsEmpty(catalog.Groups);
        Assert.IsEmpty(catalog.Playlists);
        Assert.IsEmpty(catalog.AmbienceTracks);
        Assert.HasCount(1, catalog.Issues);
        Assert.AreEqual(uncPath, catalog.RootPath);
        StringAssert.Contains(catalog.Issues[0].Message, "local folder");
    }

    private sealed class TemporaryLibrary : IDisposable
    {
        public TemporaryLibrary()
        {
            RootPath = Path.Combine(Path.GetTempPath(), $"Soundrel.Tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(RootPath);
        }

        public string RootPath { get; }

        public string CreateDirectory(params string[] relativeParts)
        {
            var path = Combine(relativeParts);
            Directory.CreateDirectory(path);
            return Path.GetFullPath(path);
        }

        public string CreateFile(params string[] relativeParts)
        {
            var path = Combine(relativeParts);
            var directory = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(path, []);
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

        private string Combine(string[] relativeParts) =>
            relativeParts.Aggregate(RootPath, Path.Combine);
    }
}

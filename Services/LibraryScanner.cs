using System.IO;
using System.Security;
using Soundrel.Models;

namespace Soundrel.Services;

public sealed class LibraryScanner
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3",
        ".wav"
    };

    public LibraryCatalog Scan(string? libraryRoot)
    {
        var groups = new List<LibraryGroup>();
        var playlists = new List<LibraryPlaylist>();
        var ambienceTracks = new List<LibraryTrack>();
        var issues = new List<LibraryScanIssue>();

        if (string.IsNullOrWhiteSpace(libraryRoot))
        {
            return CreateCatalog(string.Empty, groups, playlists, ambienceTracks, issues);
        }

        if (IsUncPath(libraryRoot))
        {
            issues.Add(new LibraryScanIssue(libraryRoot, "A local folder is required."));
            return CreateCatalog(libraryRoot, groups, playlists, ambienceTracks, issues);
        }

        string rootPath;
        try
        {
            rootPath = Path.GetFullPath(libraryRoot);
        }
        catch (Exception exception) when (IsFileSystemException(exception))
        {
            issues.Add(new LibraryScanIssue(libraryRoot, $"Could not use library path: {exception.Message}"));
            return CreateCatalog(libraryRoot, groups, playlists, ambienceTracks, issues);
        }

        if (!IsLocalDrive(rootPath, issues))
        {
            return CreateCatalog(rootPath, groups, playlists, ambienceTracks, issues);
        }

        if (!CanScanRootDirectory(rootPath, issues))
        {
            return CreateCatalog(rootPath, groups, playlists, ambienceTracks, issues);
        }

        var musicPath = Path.Combine(rootPath, "Music");
        var ambiencePath = Path.Combine(rootPath, "Ambience");
        int issueCountBeforeFolderInspection = issues.Count;
        bool canScanMusic = CanScanDirectory(musicPath, issues);
        bool canScanAmbience = CanScanDirectory(ambiencePath, issues);

        if (!canScanMusic && !canScanAmbience && issues.Count == issueCountBeforeFolderInspection)
        {
            issues.Add(new LibraryScanIssue(
                rootPath,
                $"No scannable Music or Ambience folder was found. Put music playlists under \"{musicPath}\" and ambience MP3/WAV files under \"{ambiencePath}\"."));
        }

        if (canScanMusic)
        {
            int issueCountBeforeMusicScan = issues.Count;
            ScanMusicRoot(musicPath, groups, playlists, issues);

            if (playlists.Count == 0
                && !groups.Any(ContainsPlaylist)
                && issues.Count == issueCountBeforeMusicScan)
            {
                issues.Add(new LibraryScanIssue(
                    musicPath,
                    $"No music playlists were found. Put MP3/WAV files in a playlist subfolder such as \"{Path.Combine(musicPath, "Tavern")}\"."));
            }
        }

        if (canScanAmbience)
        {
            ambienceTracks.AddRange(ScanTracks(ambiencePath, issues, out _));
        }

        SortGroups(groups);
        SortPlaylists(playlists);
        SortTracks(ambienceTracks);
        SortIssues(issues);

        return CreateCatalog(rootPath, groups, playlists, ambienceTracks, issues);
    }

    private static bool ContainsPlaylist(LibraryGroup group) =>
        group.Playlists.Count > 0 || group.Groups.Any(ContainsPlaylist);

    private static void ScanMusicRoot(
        string musicPath,
        List<LibraryGroup> groups,
        List<LibraryPlaylist> playlists,
        List<LibraryScanIssue> issues)
    {
        if (!TryGetDirectories(musicPath, issues, out var directories))
        {
            return;
        }

        SortPathsByName(directories);

        foreach (var directory in directories)
        {
            var result = ScanMusicDirectory(directory, issues);
            if (result.Group is not null)
            {
                groups.Add(result.Group);
            }
            else if (result.Playlist is not null)
            {
                playlists.Add(result.Playlist);
            }
        }
    }

    private static DirectoryScanResult ScanMusicDirectory(
        string directoryPath,
        List<LibraryScanIssue> issues)
    {
        var tracks = ScanTracks(directoryPath, issues, out var tracksRead);
        var directoriesRead = TryGetDirectories(directoryPath, issues, out var directories);

        if (directoriesRead)
        {
            SortPathsByName(directories);
        }

        var childGroups = new List<LibraryGroup>();
        var childPlaylists = new List<LibraryPlaylist>();

        if (directoriesRead)
        {
            foreach (var directory in directories)
            {
                var child = ScanMusicDirectory(directory, issues);
                if (child.Group is not null)
                {
                    childGroups.Add(child.Group);
                }
                else if (child.Playlist is not null)
                {
                    childPlaylists.Add(child.Playlist);
                }
            }
        }

        var name = Path.GetFileName(directoryPath);
        var hasChildDirectories = directoriesRead && directories.Length > 0;

        if (hasChildDirectories)
        {
            if (tracksRead && tracks.Count > 0)
            {
                childPlaylists.Add(new LibraryPlaylist(name, directoryPath, tracks));
            }

            SortGroups(childGroups);
            SortPlaylists(childPlaylists);

            return new DirectoryScanResult(
                new LibraryGroup(name, directoryPath, childGroups, childPlaylists),
                null);
        }

        if (tracksRead && (tracks.Count > 0 || directoriesRead))
        {
            return new DirectoryScanResult(
                null,
                new LibraryPlaylist(name, directoryPath, tracks));
        }

        return default;
    }

    private static List<LibraryTrack> ScanTracks(
        string directoryPath,
        List<LibraryScanIssue> issues,
        out bool succeeded)
    {
        string[] files;
        try
        {
            files = Directory.GetFiles(directoryPath, "*", SearchOption.TopDirectoryOnly);
            succeeded = true;
        }
        catch (Exception exception) when (IsFileSystemException(exception))
        {
            issues.Add(new LibraryScanIssue(directoryPath, $"Could not enumerate files: {exception.Message}"));
            succeeded = false;
            return [];
        }

        var tracks = new List<LibraryTrack>();
        foreach (var file in files.Where(file => SupportedExtensions.Contains(Path.GetExtension(file))))
        {
            if (!TryGetAttributes(file, "audio file", issues, out var attributes))
            {
                continue;
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                issues.Add(new LibraryScanIssue(file, "Skipped reparse-point audio file."));
                continue;
            }

            tracks.Add(new LibraryTrack(Path.GetFileNameWithoutExtension(file), file));
        }

        SortTracks(tracks);
        return tracks;
    }

    private static bool TryGetDirectories(
        string directoryPath,
        List<LibraryScanIssue> issues,
        out string[] directories)
    {
        try
        {
            var discoveredDirectories = Directory.GetDirectories(directoryPath, "*", SearchOption.TopDirectoryOnly);
            var localDirectories = new List<string>(discoveredDirectories.Length);

            foreach (var directory in discoveredDirectories)
            {
                if (!TryGetAttributes(directory, "folder", issues, out var attributes))
                {
                    continue;
                }

                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    issues.Add(new LibraryScanIssue(directory, "Skipped reparse-point folder."));
                    continue;
                }

                localDirectories.Add(directory);
            }

            directories = localDirectories.ToArray();
            return true;
        }
        catch (Exception exception) when (IsFileSystemException(exception))
        {
            issues.Add(new LibraryScanIssue(directoryPath, $"Could not enumerate folders: {exception.Message}"));
            directories = [];
            return false;
        }
    }

    private static bool CanScanDirectory(string directoryPath, List<LibraryScanIssue> issues)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(directoryPath);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
        catch (Exception exception) when (IsFileSystemException(exception))
        {
            issues.Add(new LibraryScanIssue(directoryPath, $"Could not inspect folder attributes: {exception.Message}"));
            return false;
        }

        if ((attributes & FileAttributes.Directory) == 0)
        {
            return false;
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            issues.Add(new LibraryScanIssue(directoryPath, "Skipped reparse-point folder."));
            return false;
        }

        return true;
    }

    private static bool CanScanRootDirectory(string rootPath, List<LibraryScanIssue> issues)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(rootPath);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            issues.Add(new LibraryScanIssue(rootPath, "Library folder does not exist."));
            return false;
        }
        catch (Exception exception) when (IsFileSystemException(exception))
        {
            issues.Add(new LibraryScanIssue(rootPath, $"Could not inspect library folder attributes: {exception.Message}"));
            return false;
        }

        if ((attributes & FileAttributes.Directory) == 0)
        {
            issues.Add(new LibraryScanIssue(rootPath, "Library folder does not exist."));
            return false;
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            issues.Add(new LibraryScanIssue(rootPath, "Skipped reparse-point folder."));
            return false;
        }

        return true;
    }

    private static bool TryGetAttributes(
        string path,
        string itemDescription,
        List<LibraryScanIssue> issues,
        out FileAttributes attributes)
    {
        try
        {
            attributes = File.GetAttributes(path);
            return true;
        }
        catch (Exception exception) when (IsFileSystemException(exception))
        {
            issues.Add(new LibraryScanIssue(path, $"Could not inspect {itemDescription} attributes: {exception.Message}"));
            attributes = default;
            return false;
        }
    }

    private static bool IsLocalDrive(string rootPath, List<LibraryScanIssue> issues)
    {
        try
        {
            string? driveRoot = Path.GetPathRoot(rootPath);
            if (!string.IsNullOrEmpty(driveRoot) && new DriveInfo(driveRoot).DriveType == DriveType.Network)
            {
                issues.Add(new LibraryScanIssue(rootPath, "A local folder is required."));
                return false;
            }

            return true;
        }
        catch (Exception exception) when (IsFileSystemException(exception))
        {
            issues.Add(new LibraryScanIssue(rootPath, $"Could not inspect library drive: {exception.Message}"));
            return false;
        }
    }

    private static bool IsUncPath(string path)
    {
        string normalizedPath = path.Replace('/', '\\');
        return normalizedPath.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)
            || normalizedPath.StartsWith(@"\\.\UNC\", StringComparison.OrdinalIgnoreCase)
            || (normalizedPath.StartsWith(@"\\", StringComparison.Ordinal)
                && !normalizedPath.StartsWith(@"\\?\", StringComparison.Ordinal)
                && !normalizedPath.StartsWith(@"\\.\", StringComparison.Ordinal));
    }

    private static LibraryCatalog CreateCatalog(
        string rootPath,
        IEnumerable<LibraryGroup> groups,
        IEnumerable<LibraryPlaylist> playlists,
        IEnumerable<LibraryTrack> ambienceTracks,
        IEnumerable<LibraryScanIssue> issues) =>
        new(rootPath, groups, playlists, ambienceTracks, issues);

    private static void SortGroups(List<LibraryGroup> groups) =>
        groups.Sort((left, right) => CompareNameAndPath(
            left.Name,
            left.DirectoryPath,
            right.Name,
            right.DirectoryPath));

    private static void SortPlaylists(List<LibraryPlaylist> playlists) =>
        playlists.Sort((left, right) => CompareNameAndPath(
            left.Name,
            left.DirectoryPath,
            right.Name,
            right.DirectoryPath));

    private static void SortTracks(List<LibraryTrack> tracks) =>
        tracks.Sort((left, right) => CompareNameAndPath(
            left.Name,
            left.FilePath,
            right.Name,
            right.FilePath));

    private static void SortPathsByName(string[] paths) =>
        Array.Sort(paths, (left, right) => CompareNameAndPath(
            Path.GetFileName(left),
            left,
            Path.GetFileName(right),
            right));

    private static void SortIssues(List<LibraryScanIssue> issues) =>
        issues.Sort((left, right) => CompareNameAndPath(
            left.Path,
            left.Message,
            right.Path,
            right.Message));

    private static int CompareNameAndPath(
        string leftName,
        string leftPath,
        string rightName,
        string rightPath)
    {
        var comparison = StringComparer.OrdinalIgnoreCase.Compare(leftName, rightName);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = StringComparer.OrdinalIgnoreCase.Compare(leftPath, rightPath);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = StringComparer.Ordinal.Compare(leftName, rightName);
        return comparison != 0
            ? comparison
            : StringComparer.Ordinal.Compare(leftPath, rightPath);
    }

    private static bool IsFileSystemException(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or SecurityException
            or ArgumentException
            or NotSupportedException;

    private readonly record struct DirectoryScanResult(
        LibraryGroup? Group,
        LibraryPlaylist? Playlist);
}

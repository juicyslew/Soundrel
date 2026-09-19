namespace Soundrel.Models;

public sealed class LibraryCatalog
{
    public LibraryCatalog(
        string rootPath,
        IEnumerable<LibraryGroup> groups,
        IEnumerable<LibraryPlaylist> playlists,
        IEnumerable<LibraryTrack> ambienceTracks,
        IEnumerable<LibraryScanIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(rootPath);
        ArgumentNullException.ThrowIfNull(groups);
        ArgumentNullException.ThrowIfNull(playlists);
        ArgumentNullException.ThrowIfNull(ambienceTracks);
        ArgumentNullException.ThrowIfNull(issues);

        RootPath = rootPath;
        Groups = Array.AsReadOnly(groups.ToArray());
        Playlists = Array.AsReadOnly(playlists.ToArray());
        AmbienceTracks = Array.AsReadOnly(ambienceTracks.ToArray());
        Issues = Array.AsReadOnly(issues.ToArray());
    }

    public string RootPath { get; }

    public IReadOnlyList<LibraryGroup> Groups { get; }

    public IReadOnlyList<LibraryPlaylist> Playlists { get; }

    public IReadOnlyList<LibraryTrack> AmbienceTracks { get; }

    public IReadOnlyList<LibraryScanIssue> Issues { get; }
}

namespace Soundrel.Models;

public sealed class LibraryPlaylist
{
    public LibraryPlaylist(
        string name,
        string directoryPath,
        IEnumerable<LibraryTrack> tracks,
        string? qualifiedDisplayName = null)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(directoryPath);
        ArgumentNullException.ThrowIfNull(tracks);

        Name = name;
        DirectoryPath = directoryPath;
        QualifiedDisplayName = string.IsNullOrWhiteSpace(qualifiedDisplayName)
            ? name
            : qualifiedDisplayName;
        Tracks = Array.AsReadOnly(tracks.ToArray());
    }

    public string Name { get; }

    public string DirectoryPath { get; }

    public string QualifiedDisplayName { get; }

    public string DisplayName => QualifiedDisplayName;

    public string QualifiedName => QualifiedDisplayName;

    public IReadOnlyList<LibraryTrack> Tracks { get; }
}

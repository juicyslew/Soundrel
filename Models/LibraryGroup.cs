namespace Soundrel.Models;

public sealed class LibraryGroup
{
    public LibraryGroup(
        string name,
        string directoryPath,
        IEnumerable<LibraryGroup> groups,
        IEnumerable<LibraryPlaylist> playlists)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(directoryPath);
        ArgumentNullException.ThrowIfNull(groups);
        ArgumentNullException.ThrowIfNull(playlists);

        Name = name;
        DirectoryPath = directoryPath;
        Groups = Array.AsReadOnly(groups.ToArray());
        Playlists = Array.AsReadOnly(playlists.ToArray());
    }

    public string Name { get; }

    public string DirectoryPath { get; }

    public IReadOnlyList<LibraryGroup> Groups { get; }

    public IReadOnlyList<LibraryPlaylist> Playlists { get; }
}

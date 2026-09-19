namespace Soundrel.Models;

public sealed class LibraryPlaylist
{
    public LibraryPlaylist(string name, string directoryPath, IEnumerable<LibraryTrack> tracks)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(directoryPath);
        ArgumentNullException.ThrowIfNull(tracks);

        Name = name;
        DirectoryPath = directoryPath;
        Tracks = Array.AsReadOnly(tracks.ToArray());
    }

    public string Name { get; }

    public string DirectoryPath { get; }

    public IReadOnlyList<LibraryTrack> Tracks { get; }
}

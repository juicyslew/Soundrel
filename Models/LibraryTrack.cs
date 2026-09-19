namespace Soundrel.Models;

public sealed class LibraryTrack
{
    public LibraryTrack(string name, string filePath)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(filePath);

        Name = name;
        FilePath = filePath;
    }

    public string Name { get; }

    public string FilePath { get; }
}

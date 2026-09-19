using Soundrel.Models;

namespace Soundrel.ViewModels;

public sealed class LibraryTreeNode
{
    private LibraryTreeNode(
        string name,
        LibraryPlaylist? playlist,
        IReadOnlyList<LibraryTreeNode> children)
    {
        Name = name;
        Playlist = playlist;
        Children = children;
    }

    public string Name { get; }

    public LibraryPlaylist? Playlist { get; }

    public IReadOnlyList<LibraryTreeNode> Children { get; }

    public bool IsPlaylist => Playlist is not null;

    internal static LibraryTreeNode FromGroup(LibraryGroup group)
    {
        var children = group.Groups
            .Select(FromGroup)
            .Concat(group.Playlists.Select(FromPlaylist))
            .ToArray();

        return new LibraryTreeNode(group.Name, null, children);
    }

    internal static LibraryTreeNode FromPlaylist(LibraryPlaylist playlist) =>
        new(playlist.Name, playlist, Array.Empty<LibraryTreeNode>());
}

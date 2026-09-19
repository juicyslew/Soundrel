namespace Soundrel.Models;

public sealed class LibraryScanIssue
{
    public LibraryScanIssue(string path, string message)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(message);

        Path = path;
        Message = message;
    }

    public string Path { get; }

    public string Message { get; }
}

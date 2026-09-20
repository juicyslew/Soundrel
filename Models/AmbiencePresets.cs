using System.IO;

namespace Soundrel.Models;

/// <summary>
/// A persisted ambience source target identified relative to a library's
/// Ambience directory.  This is deliberately separate from rendered playback
/// gains and lifecycle state.
/// </summary>
public sealed record AmbiencePresetTrack(string RelativePath, float SourceVolume);

/// <summary>
/// A named collection of ambience source targets.
/// </summary>
public sealed record AmbiencePreset(
    string Name,
    IReadOnlyList<AmbiencePresetTrack> Tracks);

/// <summary>
/// Presets belonging to one normalized library root.
/// </summary>
public sealed record LibraryAmbiencePresets(
    string LibraryRootPath,
    IReadOnlyList<AmbiencePreset> Presets);

public static class AmbiencePresetPath
{
    public static bool TryNormalizeLibraryRootPath(
        string? path,
        out string normalizedPath)
    {
        normalizedPath = string.Empty;
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path))
        {
            return false;
        }

        string pathValue = path;
        try
        {
            string fullPath = Path.GetFullPath(pathValue);
            string? rootPath = Path.GetPathRoot(fullPath);
            normalizedPath = rootPath is not null &&
                string.Equals(fullPath, rootPath, StringComparison.OrdinalIgnoreCase)
                ? fullPath
                : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            return normalizedPath.Length > 0;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    public static bool TryNormalizeRelativeTrackPath(
        string? path,
        string libraryRootPath,
        out string normalizedPath)
    {
        normalizedPath = string.Empty;
        if (string.IsNullOrWhiteSpace(path) ||
            string.IsNullOrWhiteSpace(libraryRootPath) ||
            Path.IsPathRooted(path) ||
            path.StartsWith("/", StringComparison.Ordinal) ||
            path.StartsWith("\\", StringComparison.Ordinal))
        {
            return false;
        }

        string pathValue = path;
        string[] segments = pathValue.Split(['/', '\\'], StringSplitOptions.None);
        List<string> normalizedSegments = [];
        foreach (string segment in segments)
        {
            if (segment.Length == 0 || segment == ".")
            {
                continue;
            }

            if (segment == ".." || segment.Contains(':', StringComparison.Ordinal))
            {
                return false;
            }

            normalizedSegments.Add(segment);
        }

        if (normalizedSegments.Count == 0)
        {
            return false;
        }

        normalizedPath = string.Join('/', normalizedSegments);
        try
        {
            string ambienceRoot = Path.GetFullPath(Path.Combine(libraryRootPath, "Ambience"));
            string candidate = Path.GetFullPath(Path.Combine(ambienceRoot, normalizedPath));
            string ambiencePrefix = ambienceRoot.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

            return string.Equals(candidate, ambienceRoot, StringComparison.OrdinalIgnoreCase) ||
                candidate.StartsWith(ambiencePrefix, StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }
}

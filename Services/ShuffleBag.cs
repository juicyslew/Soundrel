using Soundrel.Models;

namespace Soundrel.Services;

public sealed class ShuffleBag
{
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

    private readonly Random _random;
    private readonly List<string> _remainingPaths = [];
    private readonly HashSet<string> _selectedPaths = new(PathComparer);
    private bool _hasStartedCycle;

    public ShuffleBag()
        : this(Random.Shared)
    {
    }

    public ShuffleBag(Random random)
    {
        ArgumentNullException.ThrowIfNull(random);
        _random = random;
    }

    public LibraryTrack? TakeNext(
        IReadOnlyList<LibraryTrack> candidates,
        LibraryTrack? justPlayed = null)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        var currentTracks = new Dictionary<string, LibraryTrack>(PathComparer);
        foreach (var track in candidates)
        {
            currentTracks.TryAdd(track.FilePath, track);
        }

        if (currentTracks.Count == 0)
        {
            _remainingPaths.Clear();
            _selectedPaths.Clear();
            _hasStartedCycle = false;
            return null;
        }

        _remainingPaths.RemoveAll(path => !currentTracks.ContainsKey(path));
        _selectedPaths.IntersectWith(currentTracks.Keys);

        var isRefill = !_hasStartedCycle;
        _hasStartedCycle = true;

        foreach (var path in currentTracks.Keys)
        {
            if (!_selectedPaths.Contains(path) && !_remainingPaths.Contains(path, PathComparer))
            {
                _remainingPaths.Add(path);
            }
        }

        if (_remainingPaths.Count == 0)
        {
            _selectedPaths.Clear();
            _remainingPaths.AddRange(currentTracks.Keys);
            isRefill = true;
        }

        var selectedIndex = _random.Next(_remainingPaths.Count);
        if (isRefill &&
            _remainingPaths.Count > 1 &&
            justPlayed is not null &&
            PathComparer.Equals(_remainingPaths[selectedIndex], justPlayed.FilePath))
        {
            selectedIndex = (selectedIndex + 1) % _remainingPaths.Count;
        }

        var selectedPath = _remainingPaths[selectedIndex];
        _remainingPaths.RemoveAt(selectedIndex);
        _selectedPaths.Add(selectedPath);
        return currentTracks[selectedPath];
    }
}

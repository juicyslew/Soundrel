using Soundrel.Models;
using Soundrel.Services;

namespace Soundrel.Tests;

[TestClass]
public sealed class ShuffleBagTests
{
    [TestMethod]
    public void TakeNext_SelectsEveryTrackBeforeRepeating()
    {
        var tracks = Tracks("one", "two", "three", "four");
        var bag = new ShuffleBag(new SequenceRandom(0));

        var firstCycle = Enumerable.Range(0, tracks.Count)
            .Select(_ => bag.TakeNext(tracks)!.FilePath)
            .ToArray();
        var next = bag.TakeNext(tracks)!;

        Assert.HasCount(tracks.Count, firstCycle.Distinct(StringComparer.OrdinalIgnoreCase));
        Assert.IsTrue(tracks.Contains(next));
    }

    [TestMethod]
    public void TakeNext_RefillDoesNotImmediatelyRepeatTheJustPlayedTrack()
    {
        var tracks = Tracks("one", "two");
        var bag = new ShuffleBag(new SequenceRandom(0, 0, 1));

        _ = bag.TakeNext(tracks);
        var lastOfCycle = bag.TakeNext(tracks)!;
        var firstOfNextCycle = bag.TakeNext(tracks, lastOfCycle)!;

        Assert.AreNotEqual(lastOfCycle.FilePath, firstOfNextCycle.FilePath, ignoreCase: true);
    }

    [TestMethod]
    public void TakeNext_OneTrackPlaylistMayRepeat()
    {
        var track = Tracks("only").Single();
        var bag = new ShuffleBag(new SequenceRandom(0));

        var first = bag.TakeNext([track]);
        var second = bag.TakeNext([track], first);

        Assert.AreSame(track, first);
        Assert.AreSame(track, second);
    }

    [TestMethod]
    public void TakeNext_TrackListChangesRemoveOldTracksAndSelectNewTracksBeforeRepeats()
    {
        var original = Tracks("one", "two");
        var replacementOne = new LibraryTrack("replacement", original[0].FilePath);
        var added = new LibraryTrack("three", @"C:\Library\three.mp3");
        var bag = new ShuffleBag(new SequenceRandom(0));

        Assert.AreSame(original[0], bag.TakeNext(original));
        var next = bag.TakeNext([replacementOne, added]);
        var afterRefill = bag.TakeNext([replacementOne, added], next);

        Assert.AreSame(added, next);
        Assert.AreSame(replacementOne, afterRefill);
    }

    [TestMethod]
    public void TakeNext_EmptyThenRepopulatedPlaylistStartsFreshCycle()
    {
        var tracks = Tracks("one", "two");
        var bag = new ShuffleBag(new SequenceRandom(0));

        var justPlayed = bag.TakeNext(tracks)!;
        Assert.IsNull(bag.TakeNext([]));
        var next = bag.TakeNext(tracks, justPlayed);

        Assert.AreNotEqual(justPlayed.FilePath, next!.FilePath, ignoreCase: true);
    }

    private static IReadOnlyList<LibraryTrack> Tracks(params string[] names) =>
        names.Select(name => new LibraryTrack(name, $@"C:\Library\{name}.mp3")).ToArray();

    private sealed class SequenceRandom(params int[] values) : Random
    {
        private int _index;

        public override int Next(int maxValue)
        {
            var value = values.Length == 0 ? 0 : values[_index++ % values.Length];
            return value % maxValue;
        }
    }
}

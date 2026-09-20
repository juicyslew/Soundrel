namespace Soundrel.Models;

/// A single source target in an ambience preset. The path is normalized before
/// it is sent to the audio engine.
public sealed record AmbiencePresetTarget(string FilePath, float SourceGain);

public sealed record AmbiencePresetApplicationFailure(
    string FilePath,
    string Message,
    Exception? Exception = null);

public sealed record AmbiencePresetApplicationResult(
    int SucceededCount,
    IReadOnlyList<AmbiencePresetApplicationFailure> Failures)
{
    public int AppliedCount => SucceededCount;

    public IReadOnlyList<AmbiencePresetApplicationFailure> FailedTargets => Failures;

    public int FailureCount => Failures.Count;

    public bool Succeeded => Failures.Count == 0;

    public bool HasFailures => Failures.Count > 0;
}

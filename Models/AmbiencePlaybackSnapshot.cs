namespace Soundrel.Models;

public enum AmbiencePlaybackState
{
    FadingIn,
    Playing,
    FadingOut,
}

public sealed record AmbiencePlaybackSnapshot(
    string FilePath,
    TimeSpan Position,
    TimeSpan Duration,
    float SourceGain,
    float LifecycleGain,
    AmbiencePlaybackState State)
{
    public float Gain => SourceGain;

    public float FadeGain => LifecycleGain;
}

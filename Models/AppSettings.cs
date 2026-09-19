namespace Soundrel.Models;

public sealed class AppSettings
{
    public const int CurrentVersion = 3;

    public int Version { get; init; }

    public string? SelectedLibraryPath { get; init; }

    public ImmediateTransitionMode PreferredImmediateTransitionMode { get; init; } =
        ImmediateTransitionMode.HardCut;

    public float MusicVolume { get; init; } = 1.0f;

    public float AmbienceVolume { get; init; } = 1.0f;

    public float MasterVolume { get; init; } = 1.0f;

    public static AppSettings CreateDefault() => new()
    {
        Version = CurrentVersion,
        SelectedLibraryPath = null,
        PreferredImmediateTransitionMode = ImmediateTransitionMode.HardCut,
        MusicVolume = 1.0f,
        AmbienceVolume = 1.0f,
        MasterVolume = 1.0f,
    };
}

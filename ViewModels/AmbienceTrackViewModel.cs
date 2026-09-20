using Soundrel.Models;

namespace Soundrel.ViewModels;

/// <summary>
/// Session state for one ambience file in the scanned catalogue.
/// </summary>
public sealed class AmbienceTrackViewModel : ViewModelBase
{
    private LibraryTrack track;
    private float gain = 1f;
    private float lifecycleGain;
    private Soundrel.Models.AmbiencePlaybackState? playbackState;
    private TimeSpan position;
    private TimeSpan duration;

    public AmbienceTrackViewModel(LibraryTrack track)
    {
        ArgumentNullException.ThrowIfNull(track);
        this.track = track;
    }

    public LibraryTrack Track => track;

    public string Name => Track.Name;

    public string FilePath => Track.FilePath;

    public float Gain
    {
        get => gain;
        set
        {
            ValidateGain(value, nameof(value));
            if (SetProperty(ref gain, value))
            {
                OnPropertyChanged(nameof(SourceGain));
                OnPropertyChanged(nameof(GainDisplay));
            }
        }
    }

    // SourceGain is a convenient name for bindings and controller snapshots.
    public float SourceGain
    {
        get => Gain;
        set => Gain = value;
    }

    /// <summary>
    /// The normalized rendered play/stop envelope gain. This is intentionally
    /// separate from <see cref="Gain"/>, which remains the requested source
    /// volume target.
    /// </summary>
    public float LifecycleGain
    {
        get => lifecycleGain;
        private set => SetProperty(
            ref lifecycleGain,
            float.IsFinite(value) ? Math.Clamp(value, 0f, 1f) : 0f);
    }

    public Soundrel.Models.AmbiencePlaybackState? PlaybackState
    {
        get => playbackState;
        private set
        {
            if (SetProperty(ref playbackState, value))
            {
                OnPropertyChanged(nameof(IsActive));
                OnPropertyChanged(nameof(CanPlay));
                OnPropertyChanged(nameof(CanStop));
                OnPropertyChanged(nameof(PlaybackStateLabel));
                OnPropertyChanged(nameof(State));
                OnPropertyChanged(nameof(AmbiencePlaybackState));
                OnPropertyChanged(nameof(StateLabel));
                OnPropertyChanged(nameof(AmbiencePlaybackStateLabel));
                OnPropertyChanged(nameof(PlaybackStateDisplay));
                OnPropertyChanged(nameof(ToggleLabel));
                OnPropertyChanged(nameof(PlayStopLabel));
            }
        }
    }

    public Soundrel.Models.AmbiencePlaybackState? State => PlaybackState;

    public Soundrel.Models.AmbiencePlaybackState? AmbiencePlaybackState => PlaybackState;

    public string PlaybackStateLabel => PlaybackState switch
    {
        Soundrel.Models.AmbiencePlaybackState.FadingIn => "Fading In",
        Soundrel.Models.AmbiencePlaybackState.Playing => "Playing",
        Soundrel.Models.AmbiencePlaybackState.FadingOut => "Fading Out",
        _ => "Stopped",
    };

    public string StateLabel => PlaybackStateLabel;

    public string AmbiencePlaybackStateLabel => PlaybackStateLabel;

    public string PlaybackStateDisplay => PlaybackStateLabel;

    public string ToggleLabel => CanStop ? "Stop" : "Play";

    public string PlayStopLabel => ToggleLabel;

    public string GainDisplay => $"{Math.Clamp((int)Math.Round(Gain * 100), 0, 100)}%";

    public TimeSpan Position
    {
        get => position;
        private set => SetProperty(ref position, value);
    }

    public TimeSpan Duration
    {
        get => duration;
        private set => SetProperty(ref duration, value);
    }

    public bool IsActive => PlaybackState is not null;

    public bool CanPlay => PlaybackState is null or Soundrel.Models.AmbiencePlaybackState.FadingOut;

    public bool CanStop => PlaybackState is
        Soundrel.Models.AmbiencePlaybackState.FadingIn or
        Soundrel.Models.AmbiencePlaybackState.Playing;

    public bool CanToggle => true;

    internal void UpdateTrack(LibraryTrack value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (ReferenceEquals(track, value))
        {
            return;
        }

        track = value;
        OnPropertyChanged(nameof(Track));
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(FilePath));
    }

    internal void ApplyLogicalSnapshot(AmbiencePlaybackSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Gain = snapshot.SourceGain;
        ApplyRenderedProgress(snapshot);
    }

    internal void ApplyRenderedProgress(AmbiencePlaybackSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Position = Max(snapshot.Position, TimeSpan.Zero);
        Duration = Max(snapshot.Duration, TimeSpan.Zero);
        LifecycleGain = snapshot.LifecycleGain;
        PlaybackState = snapshot.State;
    }

    internal void MarkStopped()
    {
        LifecycleGain = 0f;
        PlaybackState = null;
        Position = TimeSpan.Zero;
        Duration = TimeSpan.Zero;
    }

    private static void ValidateGain(float value, string parameterName)
    {
        if (!float.IsFinite(value) || value is < 0f or > 1f)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "Gain must be finite and between zero and one.");
        }
    }

    private static TimeSpan Max(TimeSpan first, TimeSpan second) =>
        first >= second ? first : second;
}

using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using Soundrel.Models;
using Soundrel.ViewModels;

namespace Soundrel;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer progressTimer;
    private bool initialized;
    private bool progressPollInFlight;
    private bool isClosing;
    private bool closeAfterDispose;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel();
        progressTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(350),
        };
        progressTimer.Tick += ProgressTimer_Tick;
    }

    private MainViewModel ViewModel => (MainViewModel)DataContext;

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (initialized)
        {
            return;
        }

        initialized = true;
        try
        {
            await ViewModel.InitializeAsync();
        }
        catch (Exception exception)
        {
            ViewModel.ReportError($"Soundrel could not initialize: {exception.Message}");
        }

        if (!isClosing)
        {
            progressTimer.Start();
        }
    }

    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsBusy)
        {
            return;
        }

        try
        {
            var dialog = new OpenFolderDialog
            {
                Title = "Select Soundrel Library",
                Multiselect = false,
            };

            if (Directory.Exists(ViewModel.SelectedLibraryPath))
            {
                dialog.InitialDirectory = ViewModel.SelectedLibraryPath;
            }

            if (dialog.ShowDialog(this) == true)
            {
                await ViewModel.SelectLibraryAsync(dialog.FolderName);
            }
        }
        catch (Exception exception)
        {
            ViewModel.ReportError($"The library folder could not be selected: {exception.Message}");
        }
    }

    private async void Rescan_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await ViewModel.RescanAsync();
        }
        catch (Exception exception)
        {
            ViewModel.ReportError($"The library could not be rescanned: {exception.Message}");
        }
    }

    private void LibraryTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        ViewModel.SelectNode(e.NewValue as LibraryTreeNode);
    }

    private async void PlayNow_Click(object sender, RoutedEventArgs e) =>
        await RunPlaybackCommandAsync(ViewModel.PlayNowAsync);

    private async void HardCut_Click(object sender, RoutedEventArgs e) =>
        await RunPlaybackCommandAsync(() =>
            ViewModel.SetImmediateTransitionModeAsync(ImmediateTransitionMode.HardCut));

    private async void Crossfade_Click(object sender, RoutedEventArgs e) =>
        await RunPlaybackCommandAsync(() =>
            ViewModel.SetImmediateTransitionModeAsync(ImmediateTransitionMode.Crossfade));

    private async void AfterCurrent_Click(object sender, RoutedEventArgs e) =>
        await RunPlaybackCommandAsync(ViewModel.AfterCurrentAsync);

    private async void QueueTrack_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: LibraryTrack track })
        {
            await RunPlaybackCommandAsync(() => ViewModel.QueueTrackAsync(track));
        }
    }

    private async void AmbiencePlay_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: AmbienceTrackViewModel track })
        {
            await RunPlaybackCommandAsync(() => ViewModel.PlayAmbienceAsync(track));
        }
    }

    private async void AmbienceStop_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: AmbienceTrackViewModel track })
        {
            await RunPlaybackCommandAsync(() => ViewModel.StopAmbienceAsync(track));
        }
    }

    private async void AmbienceGainSlider_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is Slider { DataContext: AmbienceTrackViewModel track } slider)
        {
            await RunPlaybackCommandAsync(() =>
                ViewModel.SetAmbienceSourceGainAsync(track, (float)slider.Value));
        }
    }

    private async void AmbienceGainSlider_KeyUp(object sender, KeyEventArgs e)
    {
        if (sender is Slider { DataContext: AmbienceTrackViewModel track } slider &&
            IsSliderAdjustmentKey(e.Key))
        {
            await RunPlaybackCommandAsync(() =>
                ViewModel.SetAmbienceSourceGainAsync(track, (float)slider.Value));
        }
    }

    private async void MusicVolumeSlider_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is Slider slider)
        {
            await RunPlaybackCommandAsync(() => ViewModel.SetMusicVolumeAsync((float)slider.Value));
        }
    }

    private async void MusicVolumeSlider_KeyUp(object sender, KeyEventArgs e)
    {
        if (sender is Slider slider && IsSliderAdjustmentKey(e.Key))
        {
            await RunPlaybackCommandAsync(() => ViewModel.SetMusicVolumeAsync((float)slider.Value));
        }
    }

    private async void AmbienceVolumeSlider_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is Slider slider)
        {
            await RunPlaybackCommandAsync(() => ViewModel.SetAmbienceVolumeAsync((float)slider.Value));
        }
    }

    private async void AmbienceVolumeSlider_KeyUp(object sender, KeyEventArgs e)
    {
        if (sender is Slider slider && IsSliderAdjustmentKey(e.Key))
        {
            await RunPlaybackCommandAsync(() => ViewModel.SetAmbienceVolumeAsync((float)slider.Value));
        }
    }

    private async void MasterVolumeSlider_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is Slider slider)
        {
            await RunPlaybackCommandAsync(() => ViewModel.SetMasterVolumeAsync((float)slider.Value));
        }
    }

    private async void MasterVolumeSlider_KeyUp(object sender, KeyEventArgs e)
    {
        if (sender is Slider slider && IsSliderAdjustmentKey(e.Key))
        {
            await RunPlaybackCommandAsync(() => ViewModel.SetMasterVolumeAsync((float)slider.Value));
        }
    }

    private async void PauseResume_Click(object sender, RoutedEventArgs e) =>
        await RunPlaybackCommandAsync(ViewModel.PauseResumeAsync);

    private async void Skip_Click(object sender, RoutedEventArgs e) =>
        await RunPlaybackCommandAsync(ViewModel.SkipAsync);

    private async void ClearQueue_Click(object sender, RoutedEventArgs e) =>
        await RunPlaybackCommandAsync(ViewModel.ClearQueueAsync);

    private async void StopAll_Click(object sender, RoutedEventArgs e) =>
        await RunPlaybackCommandAsync(ViewModel.StopAllAsync);

    private async void QuickFadeIn_Click(object sender, RoutedEventArgs e) =>
        await RunPlaybackCommandAsync(ViewModel.QuickFadeInAsync);

    private async void QuickFadeOut_Click(object sender, RoutedEventArgs e) =>
        await RunPlaybackCommandAsync(ViewModel.QuickFadeOutAsync);

    private async void SlowFadeIn_Click(object sender, RoutedEventArgs e) =>
        await RunPlaybackCommandAsync(ViewModel.SlowFadeInAsync);

    private async void SlowFadeOut_Click(object sender, RoutedEventArgs e) =>
        await RunPlaybackCommandAsync(ViewModel.SlowFadeOutAsync);

    private async void ProgressTimer_Tick(object? sender, EventArgs e)
    {
        if (progressPollInFlight || isClosing)
        {
            return;
        }

        progressPollInFlight = true;
        try
        {
            await ViewModel.UpdatePlaybackProgressAsync();
        }
        finally
        {
            progressPollInFlight = false;
        }
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        progressTimer.Stop();
        if (closeAfterDispose)
        {
            return;
        }

        e.Cancel = true;
        if (isClosing)
        {
            return;
        }

        isClosing = true;
        try
        {
            await ViewModel.DisposeAsync();
        }
        catch (Exception exception)
        {
            ViewModel.ReportError($"Soundrel could not shut down cleanly: {exception.Message}");
        }
        finally
        {
            progressTimer.Tick -= ProgressTimer_Tick;
            closeAfterDispose = true;
            _ = Dispatcher.BeginInvoke(Close);
        }
    }

    private async Task RunPlaybackCommandAsync(Func<Task> command)
    {
        try
        {
            await command();
        }
        catch (Exception exception)
        {
            ViewModel.ReportError($"The playback command could not be completed: {exception.Message}");
        }
    }

    private static bool IsSliderAdjustmentKey(Key key) => key is
        Key.Left or Key.Right or Key.Up or Key.Down or
        Key.PageUp or Key.PageDown or Key.Home or Key.End;
}

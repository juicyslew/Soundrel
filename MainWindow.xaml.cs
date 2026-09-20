using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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
    private bool presetMutationInProgress;

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

    private void LibraryNodeHeader_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: LibraryTreeNode { IsPlaylist: false } })
        {
            return;
        }

        TreeViewItem? item = FindVisualParent<TreeViewItem>(sender as DependencyObject);
        if (item is null)
        {
            return;
        }

        item.IsExpanded = !item.IsExpanded;
        e.Handled = true;
    }

    private async void PlayNow_Click(object sender, RoutedEventArgs e) =>
        await RunPlaybackCommandAsync(ViewModel.PlayNowAsync);

    private async void TransitionModeToggle_Click(object sender, RoutedEventArgs e) =>
        await RunPlaybackCommandAsync(ViewModel.ToggleImmediateTransitionModeAsync);

    private async void AfterCurrent_Click(object sender, RoutedEventArgs e) =>
        await RunPlaybackCommandAsync(ViewModel.AfterCurrentAsync);

    private async void QueueTrack_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: LibraryTrack track })
        {
            await RunPlaybackCommandAsync(() => ViewModel.QueueTrackAsync(track));
        }
    }

    private async void TrackPlayNow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: LibraryTrack track })
        {
            await RunPlaybackCommandAsync(() => ViewModel.PlayNowAsync(track));
        }
    }

    private async void AmbienceToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: AmbienceTrackViewModel track })
        {
            await RunPlaybackCommandAsync(() => ViewModel.ToggleAmbienceAsync(track));
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

    private async void AmbiencePresetComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (presetMutationInProgress ||
            e.AddedItems.OfType<AmbiencePreset>().FirstOrDefault() is not AmbiencePreset addedPreset)
        {
            return;
        }

        AmbiencePreset selectedPreset = sender is ComboBox { SelectedItem: AmbiencePreset comboPreset }
            ? comboPreset
            : addedPreset;
        if (!ReferenceEquals(ViewModel.SelectedAmbiencePreset, selectedPreset))
        {
            ViewModel.SelectedAmbiencePreset = selectedPreset;
        }

        await ApplyAmbiencePresetAsync();
    }

    private async void ApplyAmbiencePreset_Click(object sender, RoutedEventArgs e) =>
        await ApplyAmbiencePresetAsync();

    private async Task ApplyAmbiencePresetAsync()
    {
        if (AmbiencePresetComboBox.SelectedItem is AmbiencePreset selectedPreset &&
            !ReferenceEquals(ViewModel.SelectedAmbiencePreset, selectedPreset))
        {
            ViewModel.SelectedAmbiencePreset = selectedPreset;
        }

        try
        {
            await ViewModel.ApplyAmbiencePresetAsync();
        }
        catch (Exception exception)
        {
            ViewModel.ReportError($"The ambience preset could not be applied: {exception.Message}");
        }
    }

    private async void SaveAmbiencePreset_Click(object sender, RoutedEventArgs e)
    {
        if (!TryBeginPresetMutation())
        {
            return;
        }

        try
        {
            await ViewModel.SaveAmbiencePresetAsync();
        }
        catch (Exception exception)
        {
            ViewModel.ReportError($"The ambience preset could not be saved: {exception.Message}");
        }
        finally
        {
            EndPresetMutation();
        }
    }

    private async void DeleteAmbiencePreset_Click(object sender, RoutedEventArgs e)
    {
        AmbiencePreset? preset = ViewModel.SelectedAmbiencePreset;
        if (preset is null)
        {
            return;
        }

        MessageBoxResult result = MessageBox.Show(
            this,
            $"Delete ambience preset '{preset.Name}'?",
            "Delete Ambience Preset",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        if (!TryBeginPresetMutation())
        {
            return;
        }

        try
        {
            await ViewModel.DeleteAmbiencePresetAsync();
        }
        catch (Exception exception)
        {
            ViewModel.ReportError($"The ambience preset could not be deleted: {exception.Message}");
        }
        finally
        {
            EndPresetMutation();
        }
    }

    private bool TryBeginPresetMutation()
    {
        if (presetMutationInProgress)
        {
            return false;
        }

        presetMutationInProgress = true;
        PresetHeaderControls.IsEnabled = false;
        return true;
    }

    private void EndPresetMutation()
    {
        presetMutationInProgress = false;
        PresetHeaderControls.IsEnabled = true;
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

    private async void MusicFadeToggle_Click(object sender, RoutedEventArgs e) =>
        await RunPlaybackCommandAsync(ViewModel.ToggleMusicFadeAsync);

    private async void MasterFadeToggle_Click(object sender, RoutedEventArgs e) =>
        await RunPlaybackCommandAsync(ViewModel.ToggleMasterFadeAsync);

    private async void AmbienceFadeToggle_Click(object sender, RoutedEventArgs e) =>
        await RunPlaybackCommandAsync(ViewModel.ToggleAmbienceFadeAsync);

    private async void FadeSpeedToggle_Click(object sender, RoutedEventArgs e) =>
        await RunPlaybackCommandAsync(ViewModel.ToggleFadeSpeedAsync);

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

    private static T? FindVisualParent<T>(DependencyObject? child)
        where T : DependencyObject
    {
        while (child is not null)
        {
            if (child is T parent)
            {
                return parent;
            }

            child = VisualTreeHelper.GetParent(child);
        }

        return null;
    }
}

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Tunqio.Core.Playback;
using Windows.System;

namespace Tunqio.App.Shell;

/// <summary>
/// The transport panel (E2-S2). Everything it decides is in <see cref="TransportViewModel"/>; this is the bindings,
/// the pointer gestures the scrubber needs, and the accelerators.
/// </summary>
/// <remarks>
/// The scrubber is the only part with logic here, and it is pointer plumbing rather than behaviour: a
/// <c>Slider</c> raises <c>ValueChanged</c> for the user dragging and for the binding being updated by a snapshot,
/// and cannot tell the two apart. So the pointer marks the drag, the view model refuses to move the position
/// unless a drag is in progress, and the seek happens when the pointer capture goes (AC-71).
/// </remarks>
public sealed partial class TransportControls : UserControl
{
    /// <summary>The arrow keys move the thumb; the seek is committed when the key comes back up.</summary>
    private bool _keyboardScrub;

    public TransportControls() => InitializeComponent();

    /// <summary>Set by the shell once the panel is in the tree; null before that, which XAML tolerates.</summary>
    public TransportViewModel? ViewModel
    {
        get => (TransportViewModel?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    public static readonly DependencyProperty ViewModelProperty = DependencyProperty.Register(
        nameof(ViewModel), typeof(TransportViewModel), typeof(TransportControls), new PropertyMetadata(null));

    // ---- glyph and value helpers the bindings call ------------------------------------------------------------------

    /// <summary>Segoe Fluent: play, pause.</summary>
    public static string PlayPauseGlyph(bool isPlaying) => isPlaying ? "" : "";

    /// <summary>Segoe Fluent: mute, volume.</summary>
    public static string MuteGlyph(bool isMuted) => isMuted ? "" : "";

    /// <summary>The repeat button is pressed for both repeat states; the glyph says which.</summary>
    public static bool IsRepeatOn(RepeatMode repeat) => repeat != RepeatMode.Off;

    /// <summary>The volume slider is a percentage; the engine takes 0..1.</summary>
    public static double VolumePercent(float volume) => Math.Round(volume * 100);

    // ---- transport ---------------------------------------------------------------------------------------------------

    private void OnPlayPause(object sender, RoutedEventArgs e) => Run(vm => vm.PlayPauseAsync());

    private void OnNext(object sender, RoutedEventArgs e) => Run(vm => vm.NextAsync());

    private void OnPrevious(object sender, RoutedEventArgs e) => Run(vm => vm.PreviousAsync());

    private void OnShuffle(object sender, RoutedEventArgs e) => Run(vm => vm.ToggleShuffleAsync());

    private void OnRepeat(object sender, RoutedEventArgs e) => Run(vm => vm.CycleRepeatAsync());

    private void OnMute(object sender, RoutedEventArgs e) => ViewModel?.ToggleMute();

    private void OnToggleTime(object sender, RoutedEventArgs e) => ViewModel?.ToggleTimeDisplay();

    private void OnVolumeChanged(object sender, RangeBaseValueChangedEventArgs e) =>
        ViewModel?.SetVolume((float)(e.NewValue / 100));

    // ---- the scrubber ------------------------------------------------------------------------------------------------

    private void OnScrubberPressed(object sender, PointerRoutedEventArgs e) => ViewModel?.BeginScrub();

    private void OnScrubberReleased(object sender, PointerRoutedEventArgs e) => Run(vm => vm.CommitScrubAsync());

    private void OnScrubberValueChanged(object sender, RangeBaseValueChangedEventArgs e) => ViewModel?.ScrubTo(e.NewValue);

    /// <summary>
    /// Keyboard scrubbing: the arrows move the thumb, and the seek is committed when the user stops rather than on
    /// every key repeat — holding Right would otherwise be a seek storm.
    /// </summary>
    private void OnScrubberKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (ViewModel is null || (e.Key != VirtualKey.Left && e.Key != VirtualKey.Right))
        {
            return;
        }

        if (!_keyboardScrub)
        {
            _keyboardScrub = true;
            ViewModel.BeginScrub();
            ((Slider)sender).KeyUp += OnScrubberKeyUp;
        }
    }

    private void OnScrubberKeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Left && e.Key != VirtualKey.Right)
        {
            return;
        }

        _keyboardScrub = false;
        ((Slider)sender).KeyUp -= OnScrubberKeyUp;
        Run(vm => vm.CommitScrubAsync());
    }

    /// <summary>
    /// Every handler here is <c>void</c> over a task, which is the shape XAML gives; this is the one place that
    /// swallowing is allowed, and the exception is logged rather than lost (docs/solution-structure.md, "Error
    /// handling policy": every async void event handler goes through a helper that logs).
    /// </summary>
    private void Run(Func<TransportViewModel, Task> action)
    {
        if (ViewModel is not { } vm)
        {
            return;
        }

        _ = SafeAsync(action, vm);

        static async Task SafeAsync(Func<TransportViewModel, Task> action, TransportViewModel vm)
        {
            try
            {
                await action(vm).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                Serilog.Log.Error(e, "A transport command failed");
            }
        }
    }
}

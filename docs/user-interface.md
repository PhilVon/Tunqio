# User Interface Architecture

> **Status: foundation document, partially superseded.** The ReactiveUI + Redux + command-history stack is replaced by CommunityToolkit.Mvvm with a single PlaybackSession store (ADR-007); undo/redo is scoped to Curation-mode playlist edits. RGB lighting is out of 1.0 scope (ADR-008). Layout, modes and reactive-theming mechanics remain valid and are detailed further in ui-screens-and-flows.md. See [decisions.md](decisions.md) for the record and [README.md](README.md) for the current reading order.

The user interface employs WinUI 3 framework with a mode-based navigation paradigm and audio-reactive design elements, creating adaptive layouts that scale complexity based on user expertise and context.

## Framework Selection: WinUI 3 Rationale

### Technical Advantages Over WPF

**Native Windows 11 Integration**: WinUI 3 provides automatic adherence to Windows 11 design principles including Fluent Design elements, rounded corners, and backdrop materials without custom styling overhead.

**Improved Performance Characteristics**:
- **Hardware Composition**: Direct integration with Windows Composition API eliminates airspace issues
- **High-DPI Scaling**: Automatic per-monitor DPI scaling across mixed monitor configurations
- **Touch Input**: Enhanced touch and pen input handling for hybrid laptop/tablet scenarios
- **Memory Efficiency**: 20-30% lower memory usage compared to equivalent WPF applications

**Reduced D3D Integration Complexity**: Built-in support for Win2D and composition effects eliminates the complex D3DImage/DXGI surface sharing required with WPF.

```csharp
// WinUI 3 simplified graphics integration
public sealed partial class VisualizationPanel : UserControl
{
    private Win2DCanvasControl canvasControl;

    public VisualizationPanel()
    {
        this.InitializeComponent();

        // Direct hardware-accelerated canvas without D3DImage complexity
        canvasControl = new Win2DCanvasControl();
        canvasControl.Draw += OnCanvasDraw;
        ContentGrid.Children.Add(canvasControl);
    }

    private void OnCanvasDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        // Direct access to D2D/D3D rendering without interop layers
        var session = args.DrawingSession;
        DrawAudioVisualization(session);
    }
}
```

## Adaptive Layout Architecture

### Three-Panel Design System

The interface uses a responsive three-panel layout that adapts to window size and user context:

```xml
<Grid x:Name="MainLayoutGrid">
    <!-- T-182: the controls are a bar under Now Playing, not a third column. As a 15% column they measured
         128 px at a 1000 px window for a transport that needs 242, and Shuffle was clipped to nothing. -->
    <Grid.ColumnDefinitions>
        <ColumnDefinition Width="3*" MinWidth="400" />  <!-- Now Playing and the transport bar: 75% -->
        <ColumnDefinition Width="1*" MinWidth="200"/>   <!-- Sidebar, full height: 25% -->
    </Grid.ColumnDefinitions>
    <Grid.RowDefinitions>
        <RowDefinition Height="*" />                    <!-- Now Playing; the sidebar spans both rows -->
        <RowDefinition Height="Auto" />                 <!-- Controls: natural height -->
    </Grid.RowDefinitions>

    <!-- Primary Panel: Now Playing Focus -->
    <Border x:Name="NowPlayingPanel" Grid.Column="0"
            Background="{x:Bind AudioReactiveBackground, Mode=OneWay}">
        <Grid>
            <Grid.RowDefinitions>
                <RowDefinition Height="2*"/>  <!-- Album Art + Visualization -->
                <RowDefinition Height="1*"/>  <!-- Track Metadata -->
            </Grid.RowDefinitions>

            <controls:AlbumArtVisualization Grid.Row="0"
                                          AudioData="{x:Bind ViewModel.RealtimeAudio, Mode=OneWay}"/>

            <controls:TrackMetadataPanel Grid.Row="1"
                                       Track="{x:Bind ViewModel.CurrentTrack, Mode=OneWay}"/>
        </Grid>
    </Border>

    <!-- Secondary Panel: Contextual Sidebar -->
    <controls:ContextualSidebar x:Name="SidebarPanel" Grid.Column="1"
                               Mode="{x:Bind ViewModel.CurrentMode, Mode=OneWay}"/>

    <!-- Utility Panel: Persistent Controls -->
    <controls:PlaybackControls x:Name="ControlsPanel" Grid.Column="2"
                              ViewModel="{x:Bind ViewModel.PlaybackController}"/>
</Grid>
```

### Window Size Adaptation

```csharp
public class ResponsiveLayoutManager
{
    private const double COMPACT_THRESHOLD = 800;
    private const double MEDIUM_THRESHOLD = 1200;

    public void UpdateLayout(double windowWidth)
    {
        if (windowWidth < COMPACT_THRESHOLD)
        {
            // Compact layout: Stack panels vertically
            MainLayoutGrid.ColumnDefinitions.Clear();
            MainLayoutGrid.RowDefinitions.Clear();

            MainLayoutGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star) });
            MainLayoutGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            Grid.SetColumn(NowPlayingPanel, 0); Grid.SetRow(NowPlayingPanel, 0);
            Grid.SetColumn(SidebarPanel, 0); Grid.SetRow(SidebarPanel, 1);
            Grid.SetColumn(ControlsPanel, 0); Grid.SetRow(ControlsPanel, 1);
        }
        else if (windowWidth < MEDIUM_THRESHOLD)
        {
            // Medium layout: Reduce sidebar width (two columns; the controls stay a bar under Now Playing, T-182)
            MainLayoutGrid.ColumnDefinitions[0].Width = new GridLength(4, GridUnitType.Star);
            MainLayoutGrid.ColumnDefinitions[1].Width = new GridLength(1, GridUnitType.Star);
        }
        else
        {
            // Full layout: Standard proportions
            RestoreFullLayout();
        }
    }
}
```

## Mode-Based Navigation System

### Navigation Modes

The interface operates in three distinct modes that fundamentally change the user experience:

#### Discovery Mode

**Purpose**: Visual browsing and music exploration with minimal commitment
**UI Characteristics**: Grid-based layouts, preview-on-hover, infinite scroll

```csharp
public class DiscoveryModeController : IModeController
{
    public void ActivateMode()
    {
        SidebarPanel.Content = new LibraryBrowserControl();
        NowPlayingPanel.ShowPreviewMode = true;

        // Enable hover previews with 500ms delay
        LibraryGrid.ItemHoverBehavior = new PreviewHoverBehavior
        {
            DelayMs = 500,
            PreviewDuration = 15000  // 15 second previews
        };
    }

    private async void OnItemHover(object sender, LibraryItemEventArgs e)
    {
        // Start preview playback without interrupting main playback
        await audioEngine.StartPreview(e.Track, fadeInMs: 200);

        // Show floating preview overlay
        var previewOverlay = new TrackPreviewOverlay
        {
            Track = e.Track,
            Position = GetHoverPosition(e.PointerPosition)
        };
        ShowTemporaryOverlay(previewOverlay);
    }
}
```

#### Focus Mode

**Purpose**: Immersive listening with minimal UI distraction
**UI Characteristics**: Full-screen album art, hidden controls, ambient lighting

```csharp
public class FocusModeController : IModeController
{
    public void ActivateMode()
    {
        // Hide secondary UI elements
        SidebarPanel.Visibility = Visibility.Collapsed;
        ControlsPanel.SetHoverVisibility(true);  // Only show on hover

        // Expand now playing to full width
        Grid.SetColumnSpan(NowPlayingPanel, 3);

        // Enable immersive visualization
        var focusVisualization = new ImmersiveModeVisualization();
        NowPlayingPanel.Content = focusVisualization;

        // Activate ambient lighting if supported
        if (await RGBLightingDetector.IsAvailable())
        {
            rgbController.EnableAmbientMode(NowPlayingPanel.AudioReactiveBackground);
        }
    }

    // Subtle hover zones for control access
    private void SetupHoverZones()
    {
        var bottomHoverZone = new Rectangle
        {
            Height = 100,
            Fill = Brushes.Transparent,
            VerticalAlignment = VerticalAlignment.Bottom
        };

        bottomHoverZone.PointerEntered += (s, e) =>
        {
            ControlsPanel.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));
        };
    }
}
```

#### Curation Mode

**Purpose**: Library organization and playlist management
**UI Characteristics**: Dual-pane editing, batch operations, detailed metadata

```csharp
public class CurationModeController : IModeController
{
    public void ActivateMode()
    {
        // Replace sidebar with dual-pane editor
        var curationPanel = new DualPaneCurationControl();
        SidebarPanel.Content = curationPanel;

        // Enable drag-and-drop reordering
        PlaylistEditor.AllowDrop = true;
        PlaylistEditor.DragItemsStarting += OnDragItemsStarting;
        PlaylistEditor.DragItemsCompleted += OnDragItemsCompleted;

        // Show advanced metadata editor
        NowPlayingPanel.ShowExtendedMetadata = true;
    }

    private void OnDragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        // Enable multi-select batch operations
        e.Data.SetText(JsonSerializer.Serialize(e.Items.Cast<Track>()));
        e.Data.RequestedOperation = DataPackageOperation.Move;
    }
}
```

### Mode Transition Management

```csharp
public class ModeTransitionManager
{
    private readonly Dictionary<NavigationMode, IModeController> modeControllers;
    private NavigationMode currentMode;

    public async Task TransitionToMode(NavigationMode newMode)
    {
        var exitAnimation = CreateExitAnimation(currentMode);
        var enterAnimation = CreateEnterAnimation(newMode);

        // Parallel exit and setup
        var exitTask = modeControllers[currentMode].ExitMode(exitAnimation);
        var setupTask = modeControllers[newMode].PrepareMode();

        await Task.WhenAll(exitTask, setupTask);

        // Activate new mode with entrance animation
        modeControllers[newMode].ActivateMode();
        await PlayAnimation(enterAnimation);

        currentMode = newMode;
    }

    private Storyboard CreateEnterAnimation(NavigationMode mode)
    {
        return mode switch
        {
            NavigationMode.Discovery => new SlideFromBottomAnimation(),
            NavigationMode.Focus => new FadeInWithZoomAnimation(),
            NavigationMode.Curation => new SplitPaneAnimation(),
            _ => new FadeInAnimation()
        };
    }
}
```

## State Management Architecture

### Redux-Inspired Pattern Implementation

```csharp
public interface IAppState
{
    PlaybackState Playback { get; }
    LibraryState Library { get; }
    UIState UserInterface { get; }
    VisualizationState Visualization { get; }
}

public class AppStateManager : INotifyPropertyChanged
{
    private readonly ISubject<IAction> actionStream = new Subject<IAction>();
    private readonly BehaviorSubject<IAppState> stateStream;

    public AppStateManager()
    {
        // Create state reducer pipeline
        stateStream = actionStream
            .Scan(new AppState(), (state, action) => ReduceState(state, action))
            .Replay(1)
            .RefCount()
            .ToBehaviorSubject();

        // Subscribe to state changes for UI updates
        stateStream.Subscribe(UpdateUI);
    }

    public void Dispatch(IAction action)
    {
        actionStream.OnNext(action);
    }

    private IAppState ReduceState(IAppState currentState, IAction action)
    {
        return action switch
        {
            PlayTrackAction playAction => currentState with
            {
                Playback = PlaybackReducer.Reduce(currentState.Playback, playAction)
            },
            ChangeVolumeAction volumeAction => currentState with
            {
                Playback = currentState.Playback with { Volume = volumeAction.Volume }
            },
            SwitchModeAction modeAction => currentState with
            {
                UserInterface = currentState.UserInterface with { Mode = modeAction.Mode }
            },
            _ => currentState
        };
    }
}
```

### ReactiveUI Command Pattern

```csharp
public class PlaybackControlViewModel : ReactiveObject
{
    private readonly AppStateManager stateManager;
    private readonly AudioEngine audioEngine;

    // Commands with undo/redo support
    public ReactiveCommand<Unit, Unit> PlayCommand { get; }
    public ReactiveCommand<Unit, Unit> PauseCommand { get; }
    public ReactiveCommand<Track, Unit> PlayTrackCommand { get; }
    public ReactiveCommand<Unit, Unit> UndoCommand { get; }
    public ReactiveCommand<Unit, Unit> RedoCommand { get; }

    public PlaybackControlViewModel(AppStateManager stateManager, AudioEngine audioEngine)
    {
        this.stateManager = stateManager;
        this.audioEngine = audioEngine;

        // Create commands with execution conditions
        PlayCommand = ReactiveCommand.CreateFromTask(
            ExecutePlay,
            this.WhenAnyValue(x => x.CanPlay));

        PauseCommand = ReactiveCommand.CreateFromTask(
            ExecutePause,
            this.WhenAnyValue(x => x.IsPlaying));

        // Commands automatically add to undo stack
        PlayTrackCommand = ReactiveCommand.CreateFromTask<Track>(
            ExecutePlayTrack,
            outputScheduler: RxApp.MainThreadScheduler)
            .AddToUndoStack(commandHistory);
    }

    private async Task ExecutePlayTrack(Track track)
    {
        var action = new PlayTrackAction(track);
        stateManager.Dispatch(action);

        await audioEngine.LoadAndPlay(track);
    }

    // Automatic undo/redo functionality
    private readonly CommandHistory commandHistory = new CommandHistory();

    private async Task ExecuteUndo()
    {
        if (commandHistory.CanUndo)
        {
            var undoAction = commandHistory.Undo();
            stateManager.Dispatch(undoAction);
        }
    }
}
```

## Progressive Disclosure Design

### Complexity Scaling Based on User Expertise

The interface adapts its complexity based on user behavior patterns and explicit preference settings:

```csharp
public class ProgressiveDisclosureManager
{
    private readonly IUserProfileService userProfile;
    private ExpertiseLevel currentLevel;

    public enum ExpertiseLevel
    {
        Casual,     // Basic three-button controls
        Moderate,   // Additional playback options
        Advanced,   // Full feature access
        Audiophile  // Professional controls
    }

    public void UpdateControlVisibility()
    {
        switch (currentLevel)
        {
            case ExpertiseLevel.Casual:
                ShowControls(new[] { "PlayPause", "Previous", "Next", "Volume" });
                HideControls(new[] { "Crossfade", "EQ", "OutputDevice", "BufferSize" });
                break;

            case ExpertiseLevel.Moderate:
                ShowControls(new[] { "PlayPause", "Previous", "Next", "Volume",
                                   "Shuffle", "Repeat", "Seek" });
                HideControls(new[] { "Crossfade", "OutputDevice", "BufferSize" });
                break;

            case ExpertiseLevel.Advanced:
                ShowAllBasicControls();
                ShowContextualControls(new[] { "EQ", "Crossfade", "PlaybackSpeed" });
                break;

            case ExpertiseLevel.Audiophile:
                ShowAllControls();
                EnableProfessionalFeatures();
                break;
        }
    }

    // Automatic expertise detection based on usage patterns
    public void AnalyzeUserBehavior(UserAction action)
    {
        var usage = userProfile.GetUsageMetrics();

        if (usage.AdvancedFeatureUsage > 0.3 && usage.SessionDuration > 60)
        {
            SuggestExpertiseLevelIncrease();
        }
    }
}
```

### Contextual Expansion Panels

```xml
<!-- Expandable controls that appear based on context -->
<StackPanel x:Name="BasicControls">
    <Button Content="Play" Command="{Binding PlayCommand}"/>
    <Button Content="Pause" Command="{Binding PauseCommand}"/>
    <Slider x:Name="VolumeSlider" Value="{Binding Volume}"/>
</StackPanel>

<Expander x:Name="AdvancedControls"
          Header="Advanced Options"
          IsExpanded="{Binding ShowAdvancedControls}"
          ExpandDirection="Down">
    <StackPanel>
        <Slider x:Name="CrossfadeSlider"
                Header="Crossfade Duration"
                Value="{Binding CrossfadeDuration}"/>

        <ComboBox x:Name="OutputDeviceSelector"
                  Header="Output Device"
                  ItemsSource="{Binding AvailableOutputDevices}"/>

        <controls:EqualizerControl DataContext="{Binding EQViewModel}"/>
    </StackPanel>
</Expander>
```

## Audio-Reactive UI Elements

### Dynamic Background Theming

```csharp
public class AudioReactiveThemeManager : INotifyPropertyChanged
{
    private readonly AudioColorMapper colorMapper;
    private readonly DispatcherTimer updateTimer;
    private SolidColorBrush backgroundBrush;

    public SolidColorBrush BackgroundBrush
    {
        get => backgroundBrush;
        set => SetProperty(ref backgroundBrush, value);
    }

    public AudioReactiveThemeManager()
    {
        colorMapper = new AudioColorMapper();

        // 60 FPS update timer for smooth color transitions
        updateTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(16.67)
        };
        updateTimer.Tick += UpdateColors;
        updateTimer.Start();
    }

    private void UpdateColors(object sender, EventArgs e)
    {
        if (audioEngine.HasRealtimeData)
        {
            var audioData = audioEngine.GetCurrentSpectrumData();
            var newColor = colorMapper.MapAudioToColor(audioData);

            // Smooth color interpolation to prevent jarring changes
            var currentColor = ((SolidColorBrush)BackgroundBrush).Color;
            var interpolatedColor = ColorHelper.Lerp(currentColor, newColor, 0.1f);

            BackgroundBrush = new SolidColorBrush(interpolatedColor);
        }
    }
}
```

### Performance-Optimized Gradient Animations

```csharp
public class GradientAnimationController
{
    private readonly CompositionGradientBrush gradientBrush;
    private readonly Compositor compositor;

    public GradientAnimationController(UIElement targetElement)
    {
        compositor = Window.Current.Compositor;

        // Create composition gradient brush for hardware acceleration
        gradientBrush = compositor.CreateLinearGradientBrush();
        gradientBrush.StartPoint = Vector2.Zero;
        gradientBrush.EndPoint = Vector2.One;

        // Apply to target element
        var visual = ElementCompositionPreview.GetElementVisual(targetElement);
        visual.Brush = gradientBrush;
    }

    public void UpdateGradient(Color primaryColor, Color secondaryColor)
    {
        // Animate gradient stop colors using composition animations
        var primaryColorAnimation = compositor.CreateColorKeyFrameAnimation();
        primaryColorAnimation.InsertKeyFrame(1.0f, primaryColor);
        primaryColorAnimation.Duration = TimeSpan.FromMilliseconds(500);

        var secondaryColorAnimation = compositor.CreateColorKeyFrameAnimation();
        secondaryColorAnimation.InsertKeyFrame(1.0f, secondaryColor);
        secondaryColorAnimation.Duration = TimeSpan.FromMilliseconds(500);

        gradientBrush.ColorStops[0].StartAnimation("Color", primaryColorAnimation);
        gradientBrush.ColorStops[1].StartAnimation("Color", secondaryColorAnimation);
    }
}
```

This user interface architecture provides a modern, responsive, and adaptive experience that scales with user expertise while maintaining consistent performance through audio-reactive visual elements.
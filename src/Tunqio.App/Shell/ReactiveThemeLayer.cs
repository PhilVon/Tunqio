using System.Numerics;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Tunqio.Core.Visualization;

namespace Tunqio.App.Shell;

/// <summary>
/// The shell's background gradient (E4-S6): a Composition linear gradient painted behind everything, whose two
/// stops are the reactive palette's primary and secondary and whose middle stop is its accent.
/// </summary>
/// <remarks>
/// <para>
/// <b>Composition rather than XAML brushes.</b> A <c>LinearGradientBrush</c> in the tree would re-tessellate on
/// the UI thread thirty times a second; a <see cref="CompositionLinearGradientBrush"/> on a sprite visual is
/// owned by the compositor, and setting a stop's colour is a property write the render thread picks up. That is
/// also why the colours are set directly rather than animated toward: the smoothing has already happened, in
/// <see cref="ReactiveThemeEngine"/>, where it can be tested; a 500 ms key-frame animation on top of an EMA
/// would be two smoothers in series and neither of them the configured one.
/// </para>
/// <para>
/// <b>The layer is a child visual of an empty element</b> behind the shell grid, so nothing in the tree has to
/// be restyled and <see cref="Clear"/> is one <c>IsVisible</c> away from the window looking exactly as it did
/// before this feature existed.
/// </para>
/// </remarks>
public sealed class ReactiveThemeLayer : IReactiveThemeSink, IDisposable
{
    private readonly FrameworkElement _host;
    private readonly DispatcherQueue _dispatcher;
    private readonly SpriteVisual _visual;
    private readonly CompositionLinearGradientBrush _brush;
    private readonly CompositionColorGradientStop _start;
    private readonly CompositionColorGradientStop _middle;
    private readonly CompositionColorGradientStop _end;
    private bool _disposed;

    /// <summary>Attaches a gradient layer to <paramref name="host"/>, which should be empty and behind the shell.</summary>
    public ReactiveThemeLayer(FrameworkElement host)
    {
        ArgumentNullException.ThrowIfNull(host);
        _host = host;
        _dispatcher = host.DispatcherQueue;

        Compositor compositor = ElementCompositionPreview.GetElementVisual(host).Compositor;
        _brush = compositor.CreateLinearGradientBrush();
        _brush.StartPoint = Vector2.Zero;
        _brush.EndPoint = Vector2.One;
        _start = compositor.CreateColorGradientStop(0.0f, Colors.Transparent);
        _middle = compositor.CreateColorGradientStop(0.55f, Colors.Transparent);
        _end = compositor.CreateColorGradientStop(1.0f, Colors.Transparent);
        _brush.ColorStops.Add(_start);
        _brush.ColorStops.Add(_middle);
        _brush.ColorStops.Add(_end);

        _visual = compositor.CreateSpriteVisual();
        _visual.Brush = _brush;
        _visual.IsVisible = false;
        _visual.Size = new Vector2((float)host.ActualWidth, (float)host.ActualHeight);
        ElementCompositionPreview.SetElementChildVisual(host, _visual);
        host.SizeChanged += OnHostSizeChanged;
    }

    /// <summary>True while the gradient is painted; false when <see cref="Clear"/> has put the theme back.</summary>
    public bool IsPainted { get; private set; }

    /// <summary>The colours as last painted, for the diagnostics overlay and the spike.</summary>
    public ReactiveThemePalette? Painted { get; private set; }

    /// <inheritdoc />
    public void Apply(ReactiveThemePalette palette)
    {
        ArgumentNullException.ThrowIfNull(palette);
        OnUiThread(() =>
        {
            if (_disposed)
            {
                return;
            }

            _start.Color = ToColor(palette.Primary);
            _middle.Color = ToColor(palette.Accent);
            _end.Color = ToColor(palette.Background);
            _visual.IsVisible = true;
            IsPainted = true;
            Painted = palette;
        });
    }

    /// <inheritdoc />
    public void Clear() => OnUiThread(() =>
    {
        if (_disposed)
        {
            return;
        }

        _visual.IsVisible = false;
        IsPainted = false;
        Painted = null;
    });

    /// <summary>Detaches the layer; the window is left exactly as it would be without this feature.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _host.SizeChanged -= OnHostSizeChanged;
        OnUiThread(() =>
        {
            ElementCompositionPreview.SetElementChildVisual(_host, null);
            _visual.Dispose();
            _brush.Dispose();
        });
    }

    private static Windows.UI.Color ToColor(Srgb c) => Windows.UI.Color.FromArgb(0xFF, c.R, c.G, c.B);

    private void OnHostSizeChanged(object sender, SizeChangedEventArgs e) =>
        _visual.Size = new Vector2((float)e.NewSize.Width, (float)e.NewSize.Height);

    // The controller ticks on a timer thread; a composition visual belongs to the thread its element does.
    private void OnUiThread(Action action)
    {
        if (_dispatcher.HasThreadAccess)
        {
            action();
        }
        else
        {
            _dispatcher.TryEnqueue(() => action());
        }
    }
}

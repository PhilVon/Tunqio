using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Tunqio.Core.Library;
using Windows.Foundation;
using Windows.System;

namespace Tunqio.App.Controls;

/// <summary>
/// Five stars (E6-S7): a track's rating as the user sees and sets it, in Now Playing, in every row of the Tracks
/// table and in album detail. <see cref="Value"/> is 0..5 stars, 0 being "not rated"; <see cref="ValueChanged"/>
/// is raised only for a change the user made (pointer, keyboard or UI Automation), never for one bound in, so the
/// host can write the rating without echoing its own update back.
/// </summary>
/// <remarks>
/// <para>
/// Built by hand rather than from WinUI's <c>RatingControl</c> for the Tracks table's sake: a row is 36 px and
/// the rating column 88 px, the table virtualises a hundred thousand rows, and the stock control is a templated,
/// animated composition of five glyph elements sized for a form. This is one horizontal run of five glyphs that
/// fits the cell, with the hit-testing done by position, so a realised row costs five icons and nothing else.
/// </para>
/// <para>
/// <b>Keyboard.</b> Left and Right move a star at a time (Left below one star clears), 1–5 set that many, 0,
/// Delete and Backspace clear. Up and Down are left alone: in a list they move between rows and at the shell
/// they are the volume. <b>Pointer.</b> A click sets that many stars; clicking the star that is already the
/// rating clears it, which is what every star control the user has met does.
/// </para>
/// <para>
/// <b>Accessibility.</b> The control is one element to a screen reader, a slider named "Rating, 3 of 5 stars" (the
/// label is <see cref="Label"/>'s), and it answers the RangeValue pattern: 0..5 in steps of one, settable. It is
/// deliberately <em>not</em> part of the row's own name (docs/ui-screens-and-flows.md, "Accessibility contract":
/// rows announce four columns and the rating is detail), and <c>tools/check-ratings.ps1</c> sets it through
/// RangeValue rather than by keystroke.
/// </para>
/// </remarks>
public sealed partial class RatingControl : UserControl
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(int), typeof(RatingControl), new PropertyMetadata(0, (d, e) => ((RatingControl)d).OnValueChanged((int)e.OldValue, (int)e.NewValue)));

    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(RatingControl), new PropertyMetadata("Rating", (d, _) => ((RatingControl)d).UpdateName()));

    private const string OutlineGlyph = ""; // FavoriteStar
    private const string FilledGlyph = "";  // FavoriteStarFill
    private const double StarSize = 16;
    private const double StarGap = 1;

    private readonly FontIcon[] _stars = new FontIcon[Ratings.MaxStars];
    private int _hover;

    public RatingControl()
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = StarGap,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), // the gaps must hit-test too
        };
        for (int i = 0; i < _stars.Length; i++)
        {
            _stars[i] = new FontIcon { Glyph = OutlineGlyph, FontSize = 14, Width = StarSize, Height = StarSize };
            row.Children.Add(_stars[i]);
        }

        Content = row;
        IsTabStop = true;
        UseSystemFocusVisuals = true;
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Center;
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerExited += OnPointerExited;
        PointerCanceled += OnPointerExited;
        Render();
        UpdateName();
    }

    /// <summary>Raised when the user set a rating; the argument is the new star count, 0 for cleared.</summary>
    public event EventHandler<int>? ValueChanged;

    /// <summary>Stars, 0..5; 0 is not rated. Set by the host from the row; see <see cref="ValueChanged"/> for the user's changes.</summary>
    public int Value
    {
        get => (int)GetValue(ValueProperty);
        set => SetValue(ValueProperty, Math.Clamp(value, 0, Ratings.MaxStars));
    }

    /// <summary>What Narrator calls the control, ahead of the stars: "Rating" unless a host has two of them to tell apart.</summary>
    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    /// <summary>The automation name as it stands: "Rating, 3 of 5 stars" or "Rating, not rated".</summary>
    public string AutomationName => Label + ", " + Ratings.Describe(Value);

    /// <summary>The user set <paramref name="stars"/> (0 clears): what a click, a key and a UIA SetValue all come to.</summary>
    internal void SetByUser(int stars)
    {
        int next = Math.Clamp(stars, 0, Ratings.MaxStars);
        if (next == Value)
        {
            return;
        }

        Value = next;
        ValueChanged?.Invoke(this, next);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new RatingControlAutomationPeer(this);

    protected override void OnKeyDown(KeyRoutedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        int? next = e.Key switch
        {
            VirtualKey.Right => Math.Min(Value + 1, Ratings.MaxStars),
            VirtualKey.Left => Math.Max(Value - 1, 0),
            VirtualKey.Delete or VirtualKey.Back or VirtualKey.Number0 or VirtualKey.NumberPad0 => 0,
            >= VirtualKey.Number1 and <= VirtualKey.Number5 => e.Key - VirtualKey.Number0,
            >= VirtualKey.NumberPad1 and <= VirtualKey.NumberPad5 => e.Key - VirtualKey.NumberPad0,
            _ => null,
        };
        if (next is { } stars)
        {
            SetByUser(stars);
            e.Handled = true;
        }

        base.OnKeyDown(e);
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        int star = StarAt(e.GetCurrentPoint(this).Position);
        // The star that is already the rating clears it; any other sets it.
        SetByUser(star == Value ? 0 : star);
        _hover = 0;
        Render();
        e.Handled = true;
        Focus(FocusState.Pointer);
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        int star = StarAt(e.GetCurrentPoint(this).Position);
        if (star != _hover)
        {
            _hover = star;
            Render();
        }
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (_hover != 0)
        {
            _hover = 0;
            Render();
        }
    }

    /// <summary>Which star (1..5) is under <paramref name="position"/>; the run is five equal cells.</summary>
    private static int StarAt(Point position)
    {
        double cell = StarSize + StarGap;
        return Math.Clamp((int)Math.Floor(position.X / cell) + 1, 1, Ratings.MaxStars);
    }

    private void OnValueChanged(int oldValue, int newValue)
    {
        Render();
        UpdateName();
        if (FrameworkElementAutomationPeer.FromElement(this) is { } peer)
        {
            peer.RaisePropertyChangedEvent(RangeValuePatternIdentifiers.ValueProperty, (double)oldValue, (double)newValue);
        }
    }

    /// <summary>Filled up to the rating; while the pointer rests on a star, the run up to it previews at half strength.</summary>
    private void Render()
    {
        int value = Value;
        for (int i = 0; i < _stars.Length; i++)
        {
            int star = i + 1;
            bool filled = star <= value;
            bool previewed = !filled && star <= _hover;
            _stars[i].Glyph = filled || previewed ? FilledGlyph : OutlineGlyph;
            _stars[i].Opacity = filled ? 1 : previewed ? 0.55 : 0.4;
        }
    }

    private void UpdateName()
    {
        string name = AutomationName;
        string? previous = AutomationProperties.GetName(this);
        if (!string.Equals(previous, name, StringComparison.Ordinal))
        {
            AutomationProperties.SetName(this, name);
            if (FrameworkElementAutomationPeer.FromElement(this) is { } peer)
            {
                peer.RaisePropertyChangedEvent(AutomationElementIdentifiers.NameProperty, previous ?? string.Empty, name);
            }
        }
    }
}

/// <summary>
/// The star control to UI Automation: a slider over 0..5 that can be set, which is how a screen reader user and
/// the UIA harness rate a track without a keystroke.
/// </summary>
public sealed partial class RatingControlAutomationPeer : FrameworkElementAutomationPeer, IRangeValueProvider
{
    private readonly RatingControl _owner;

    public RatingControlAutomationPeer(RatingControl owner)
        : base(owner)
    {
        _owner = owner;
    }

    public bool IsReadOnly => !_owner.IsEnabled;

    public double LargeChange => 1;

    public double Maximum => Ratings.MaxStars;

    public double Minimum => 0;

    public double SmallChange => 1;

    public double Value => _owner.Value;

    public void SetValue(double value)
    {
        if (double.IsNaN(value) || value < 0 || value > Ratings.MaxStars)
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "a rating is 0 to 5 stars");
        }

        _owner.SetByUser((int)Math.Round(value));
    }

    protected override object GetPatternCore(PatternInterface patternInterface) =>
        patternInterface == PatternInterface.RangeValue ? this : base.GetPatternCore(patternInterface);

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Slider;

    protected override string GetClassNameCore() => nameof(RatingControl);

    protected override string GetNameCore()
    {
        string name = base.GetNameCore();
        return string.IsNullOrEmpty(name) ? _owner.AutomationName : name;
    }

    protected override bool IsControlElementCore() => true;

    protected override bool IsContentElementCore() => true;
}

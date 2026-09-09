using Microsoft.UI.Xaml;
using Tunqio.App.Controls;
using Tunqio.Core;

namespace Tunqio.App;

/// <summary>The window <see cref="LibrarySpikeRunner"/> drives; it only hosts the two controls.</summary>
public sealed partial class LibrarySpikeWindow : Window
{
    public LibrarySpikeWindow()
    {
        InitializeComponent();
        Title = Identity.WindowTitle(null, null) + " — library spike";
    }

    public TracksList TracksList => Tracks;

    public AlbumsGrid AlbumsGrid => Albums;

    public void SetStatus(string text) => StatusText.Text = text;
}

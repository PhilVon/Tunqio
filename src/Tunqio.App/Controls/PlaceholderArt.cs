using System.Text;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Tunqio.App.Controls;

/// <summary>
/// The stand-in for missing album art (docs/library-and-data.md, "Art decode failure": a placeholder derived
/// from the album title's hash colour). The hue is a stable FNV-1a hash of the title so an album keeps its
/// colour across sessions; brushes are shared per hue bucket and must only be used on the UI thread.
/// </summary>
public static class PlaceholderArt
{
    private const int Buckets = 36;
    private static readonly SolidColorBrush?[] Brushes = new SolidColorBrush?[Buckets];

    public static SolidColorBrush Brush(string? title)
    {
        int bucket = Bucket(title);
        return Brushes[bucket] ??= new SolidColorBrush(Hsl(bucket * 360.0 / Buckets, 0.42, 0.34));
    }

    /// <summary>
    /// The colour <see cref="Brush"/> paints, without the brush. A <see cref="SolidColorBrush"/> cannot be
    /// constructed outside the XAML runtime, so the determinism this class exists for — the same title gives the
    /// same colour, this session and the next — is only assertable in the form the colour is in here.
    /// </summary>
    public static Color ColorFor(string? title) => Hsl(Bucket(title) * 360.0 / Buckets, 0.42, 0.34);

    /// <summary>Which of the <see cref="Buckets"/> hues a title falls in.</summary>
    private static int Bucket(string? title) => (int)(Hash(title ?? string.Empty) % Buckets);

    /// <summary>Up to two initials for the tile ("City Lights Compilation" gives "CL").</summary>
    public static string Initials(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return "♪";
        }

        var sb = new StringBuilder(2);
        foreach (string word in title.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (char.IsLetterOrDigit(word[0]))
            {
                sb.Append(char.ToUpperInvariant(word[0]));
                if (sb.Length == 2)
                {
                    break;
                }
            }
        }

        return sb.Length > 0 ? sb.ToString() : "♪";
    }

    private static uint Hash(string s)
    {
        uint hash = 2166136261;
        foreach (char c in s)
        {
            hash = (hash ^ char.ToUpperInvariant(c)) * 16777619;
        }

        return hash;
    }

    private static Color Hsl(double h, double s, double l)
    {
        double c = (1 - Math.Abs(2 * l - 1)) * s;
        double x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        double m = l - c / 2;
        (double r, double g, double b) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        // A struct initialiser rather than ColorHelper: this runs in the test host too, where there is no XAML
        // runtime to activate a WinRT helper against.
        return new Color
        {
            A = 255,
            R = (byte)Math.Round((r + m) * 255),
            G = (byte)Math.Round((g + m) * 255),
            B = (byte)Math.Round((b + m) * 255),
        };
    }
}

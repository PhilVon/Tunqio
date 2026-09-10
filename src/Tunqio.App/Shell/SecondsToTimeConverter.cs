using Microsoft.UI.Xaml.Data;

namespace Tunqio.App.Shell;

/// <summary>
/// The scrubber's thumb tooltip: the slider's value is a number of seconds, and what the user wants to read while
/// dragging is a time (E2-S2, "progress with hover tooltip and drag seek"). Shares
/// <see cref="Controls.Format.Duration"/> with the track lists so the same instant is written the same way
/// wherever it appears.
/// </summary>
public sealed partial class SecondsToTimeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        Controls.Format.Duration((int)(Math.Max(0, System.Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture)) * 1000));

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException("The scrubber's tooltip is display only.");
}

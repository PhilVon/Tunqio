using System.Globalization;
using System.Text.Json;
using Tunqio.Core.Library;

namespace Tunqio.Library.Art;

/// <summary>
/// <c>palette.json</c>: <c>{"version":1,"colors":[{"hex":"#rrggbb","population":0.41,"luminance":0.12},...]}</c>,
/// five entries, most populous first. Read in-process through <see cref="IArtCache.LoadPaletteAsync"/> (E4-S6
/// theming) and by anything else straight from the file.
/// </summary>
internal static class PaletteFile
{
    public const string Name = "palette.json";
    public const int Version = 1;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static byte[] Write(ArtPalette palette)
    {
        var document = new Document(Version, palette.Colors.Select(c => new Entry(c.Hex, Round(c.Population), Round(c.Luminance))).ToList());
        return JsonSerializer.SerializeToUtf8Bytes(document, Options);
    }

    /// <summary><c>null</c> for a file that is not a palette (a truncated write, a future version).</summary>
    public static ArtPalette? Read(ReadOnlySpan<byte> json)
    {
        Document? document;
        try
        {
            document = JsonSerializer.Deserialize<Document>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }

        if (document is null || document.Version != Version || document.Colors is null || document.Colors.Count == 0)
        {
            return null;
        }

        var colours = new List<PaletteColor>(document.Colors.Count);
        foreach (Entry entry in document.Colors)
        {
            if (entry.Hex is not { Length: 7 } hex || hex[0] != '#'
                || !byte.TryParse(hex.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte r)
                || !byte.TryParse(hex.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte g)
                || !byte.TryParse(hex.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b))
            {
                return null;
            }

            colours.Add(new PaletteColor(r, g, b, entry.Population, entry.Luminance));
        }

        return new ArtPalette(colours);
    }

    private static double Round(double value) => Math.Round(value, 4);

    private sealed record Document(int Version, List<Entry>? Colors);

    private sealed record Entry(string? Hex, double Population, double Luminance);
}

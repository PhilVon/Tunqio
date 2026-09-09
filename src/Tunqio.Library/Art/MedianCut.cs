using Tunqio.Core.Library;

namespace Tunqio.Library.Art;

/// <summary>
/// Median-cut colour quantisation (docs/library-and-data.md, ExtractArt): the image's pixels are binned at
/// 5 bits per channel, the box holding every occupied bin is split at the population median along its widest
/// channel, the most populous splittable box goes next, and so on until there are <see cref="ArtPalette.Size"/>
/// boxes; each box becomes the population-weighted mean of its bins. Pure and allocation-light: a 300 px tile
/// costs a 32 K histogram and a few short lists.
/// </summary>
internal static class MedianCut
{
    private const int Bits = 5;
    private const int Levels = 1 << Bits;
    private const int Shift = 8 - Bits;

    /// <param name="bgra">Pixels in BGRA order, row-major, no padding.</param>
    public static ArtPalette Extract(ReadOnlySpan<byte> bgra, int width, int height, int colours = ArtPalette.Size)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(colours, 1);
        int pixels = width * height;
        ArgumentOutOfRangeException.ThrowIfLessThan(bgra.Length, pixels * 4, nameof(bgra));

        var histogram = new int[Levels * Levels * Levels];
        for (int i = 0; i < pixels; i++)
        {
            int p = i * 4;
            histogram[Index(bgra[p + 2] >> Shift, bgra[p + 1] >> Shift, bgra[p] >> Shift)]++;
        }

        var bins = new List<Bin>();
        for (int index = 0; index < histogram.Length; index++)
        {
            if (histogram[index] > 0)
            {
                bins.Add(new Bin(index, histogram[index]));
            }
        }

        var boxes = new List<Box>();
        if (bins.Count > 0)
        {
            boxes.Add(Box.Over(bins, 0, bins.Count));
            while (boxes.Count < colours)
            {
                int widest = -1;
                for (int i = 0; i < boxes.Count; i++)
                {
                    if (boxes[i].Length > 1 && (widest < 0 || boxes[i].Count > boxes[widest].Count))
                    {
                        widest = i;
                    }
                }

                if (widest < 0)
                {
                    break;
                }

                (Box lower, Box upper) = boxes[widest].Split(bins);
                boxes[widest] = lower;
                boxes.Add(upper);
            }
        }

        double total = Math.Max(1, pixels);
        List<PaletteColor> result = boxes
            .Select(box => box.Mean(bins, total))
            .OrderByDescending(c => c.Population)
            .ThenBy(c => c.Luminance)
            .ToList();

        if (result.Count == 0)
        {
            result.Add(new PaletteColor(0, 0, 0, 0, 0));
        }

        PaletteColor dominant = result[0];
        for (int shade = 1; result.Count < colours; shade++)
        {
            double factor = Math.Pow(0.7, shade);
            byte r = (byte)Math.Round(dominant.R * factor);
            byte g = (byte)Math.Round(dominant.G * factor);
            byte b = (byte)Math.Round(dominant.B * factor);
            result.Add(new PaletteColor(r, g, b, 0, PaletteColor.RelativeLuminance(r, g, b)));
        }

        return new ArtPalette(result);
    }

    private static int Index(int r, int g, int b) => (r << (2 * Bits)) | (g << Bits) | b;

    private static (int R, int G, int B) Channels(int index) => (index >> (2 * Bits), (index >> Bits) & (Levels - 1), index & (Levels - 1));

    private readonly record struct Bin(int Index, int Count);

    /// <summary>A contiguous slice of the bin list (the slice is re-sorted in place per split, so a box stays a range).</summary>
    private readonly record struct Box(int Start, int Length, long Count)
    {
        public static Box Over(List<Bin> bins, int start, int length)
        {
            long count = 0;
            for (int i = start; i < start + length; i++)
            {
                count += bins[i].Count;
            }

            return new Box(start, length, count);
        }

        public (Box Lower, Box Upper) Split(List<Bin> bins)
        {
            int minR = Levels, minG = Levels, minB = Levels, maxR = -1, maxG = -1, maxB = -1;
            for (int i = Start; i < Start + Length; i++)
            {
                (int r, int g, int b) = Channels(bins[i].Index);
                minR = Math.Min(minR, r);
                maxR = Math.Max(maxR, r);
                minG = Math.Min(minG, g);
                maxG = Math.Max(maxG, g);
                minB = Math.Min(minB, b);
                maxB = Math.Max(maxB, b);
            }

            int rangeR = maxR - minR, rangeG = maxG - minG, rangeB = maxB - minB;
            Comparison<Bin> order = rangeR >= rangeG && rangeR >= rangeB
                ? (x, y) => Channels(x.Index).R.CompareTo(Channels(y.Index).R)
                : rangeG >= rangeB
                    ? (x, y) => Channels(x.Index).G.CompareTo(Channels(y.Index).G)
                    : (x, y) => Channels(x.Index).B.CompareTo(Channels(y.Index).B);
            bins.Sort(Start, Length, Comparer<Bin>.Create(order));

            long half = Count / 2;
            long running = 0;
            int cut = Start;
            while (cut < Start + Length - 1)
            {
                running += bins[cut].Count;
                cut++;
                if (running >= half)
                {
                    break;
                }
            }

            return (Over(bins, Start, cut - Start), Over(bins, cut, Start + Length - cut));
        }

        public PaletteColor Mean(List<Bin> bins, double total)
        {
            double r = 0, g = 0, b = 0;
            for (int i = Start; i < Start + Length; i++)
            {
                (int br, int bg, int bb) = Channels(bins[i].Index);
                double weight = bins[i].Count;
                r += Centre(br) * weight;
                g += Centre(bg) * weight;
                b += Centre(bb) * weight;
            }

            byte mr = (byte)Math.Clamp(Math.Round(r / Count), 0, 255);
            byte mg = (byte)Math.Clamp(Math.Round(g / Count), 0, 255);
            byte mb = (byte)Math.Clamp(Math.Round(b / Count), 0, 255);
            return new PaletteColor(mr, mg, mb, Count / total, PaletteColor.RelativeLuminance(mr, mg, mb));
        }

        private static int Centre(int level) => (level << Shift) + (1 << (Shift - 1));
    }
}

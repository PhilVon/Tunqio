using System.Globalization;
using TagLib;
using TagLib.Id3v2;
using Tunqio.Core.Library;
using TagFile = TagLib.File;

namespace Tunqio.Library.Tags;

/// <summary>
/// Where a rating lives in each container, and on what scale (E6-S7; docs/library-and-data.md, "Durability").
/// There is no field the formats agree on, so this is a table of conventions, chosen so that the taggers people
/// already have read what Tunqio wrote:
/// <list type="bullet">
/// <item>ID3v2 (MP3, and the ID3 chunk of WAV and AIFF): a <c>POPM</c> frame, bytes 1 / 64 / 128 / 196 / 255 for
/// one to five stars under the user "Windows Media Player 9 Series", which is the frame Explorer's Rating column
/// and Windows Media Player read and write. Reading accepts any user's frame and the usual byte bands.</item>
/// <item>Vorbis comments (FLAC, Ogg Vorbis, Opus) and APEv2 (WavPack): a <c>RATING</c> field holding 0..100
/// (MediaMonkey, foobar2000). Reading accepts 1..5 and 0.0..1.0 too, since Quod Libet and friends use those.</item>
/// <item>MP4 (AAC, ALAC): the <c>rate</c> atom holding 0..100, which is what Kid3 and MediaMonkey read.</item>
/// <item>ASF (WMA): <c>WM/SharedUserRating</c>, 1 / 25 / 50 / 75 / 99, Windows Media Player's own scale.</item>
/// </list>
/// Every reader here folds the file's value to whole stars and answers on the library's 0..100 scale as
/// <see cref="Ratings.FromStars"/> would, so a value read back after a write compares equal to the value asked
/// for (the writer verifies every write by reading it back), and a foreign file's 37 is shown as the two stars it
/// is rather than as a number no star control can draw.
/// </summary>
internal static class TagRatings
{
    /// <summary>The POPM user Explorer and Windows Media Player look for; a frame under any other user still reads.</summary>
    internal const string PopmUser = "Windows Media Player 9 Series";

    private const string XiphField = "RATING";
    private const string ApeItem = "RATING";
    private const string AsfDescriptor = "WM/SharedUserRating";
    private static readonly ByteVector Mp4Atom = "rate";

    /// <summary>The rating the file carries, on the 0..100 scale in whole stars, or null when it has none.</summary>
    public static int? Read(TagFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        // The container's own tag first, then any other the file happens to carry (a FLAC with a stray ID3v2).
        foreach (TagTypes type in Order(file))
        {
            if (file.GetTag(type, create: false) is { } tag && ReadFrom(tag) is { } rating)
            {
                return rating;
            }
        }

        return null;
    }

    /// <summary>
    /// Writes <paramref name="rating"/> (0..100; null or 0 clears) into the container's own tag, creating the tag
    /// when the file has none of that kind. Returns false when the format has no tag this can be put in, which the
    /// caller's verify then reports as a write the file did not keep.
    /// </summary>
    public static bool Write(TagFile file, int? rating)
    {
        ArgumentNullException.ThrowIfNull(file);
        int stars = Ratings.Stars(rating);
        if (Native(file) is not { } type || file.GetTag(type, create: true) is not { } tag)
        {
            return false;
        }

        switch (tag)
        {
            case TagLib.Id3v2.Tag id3:
                id3.RemoveFrames("POPM");
                if (stars > 0)
                {
                    id3.AddFrame(new PopularimeterFrame(PopmUser) { Rating = PopmByte(stars) });
                }

                return true;
            case TagLib.Ogg.XiphComment xiph:
                if (stars > 0)
                {
                    xiph.SetField(XiphField, Hundred(stars));
                }
                else
                {
                    xiph.RemoveField(XiphField);
                }

                return true;
            case TagLib.Mpeg4.AppleTag apple:
                if (stars > 0)
                {
                    apple.SetText(Mp4Atom, Hundred(stars));
                }
                else
                {
                    apple.ClearData(Mp4Atom);
                }

                return true;
            case TagLib.Asf.Tag asf:
                if (stars > 0)
                {
                    asf.SetDescriptorString(AsfValue(stars).ToString(CultureInfo.InvariantCulture), AsfDescriptor);
                }
                else
                {
                    asf.RemoveDescriptors(AsfDescriptor);
                }

                return true;
            case TagLib.Ape.Tag ape:
                if (stars > 0)
                {
                    ape.SetValue(ApeItem, Hundred(stars));
                }
                else
                {
                    ape.RemoveItem(ApeItem);
                }

                return true;
            default:
                return false;
        }
    }

    // ---- reading each kind of tag ----------------------------------------------------------------------------------

    private static int? ReadFrom(TagLib.Tag tag) => tag switch
    {
        TagLib.Id3v2.Tag id3 => Stars(id3.GetFrames<PopularimeterFrame>().Select(f => PopmStars(f.Rating)).FirstOrDefault(s => s > 0)),
        TagLib.Ogg.XiphComment xiph => Parse(xiph.GetFirstField(XiphField)),
        TagLib.Mpeg4.AppleTag apple => Parse(apple.GetText(Mp4Atom).FirstOrDefault()),
        TagLib.Asf.Tag asf => Stars(AsfStars(asf.GetDescriptorString(AsfDescriptor))),
        TagLib.Ape.Tag ape => Parse(ape.GetItem(ApeItem)?.ToString()),
        _ => null,
    };

    /// <summary>A number on any of the scales taggers use — 0..100, 1..5, or 0.0..1.0 — as whole stars on 0..100.</summary>
    private static int? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)
            || !double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            || double.IsNaN(value) || value <= 0)
        {
            return null;
        }

        int stars = value switch
        {
            <= 1 when text.Contains('.', StringComparison.Ordinal) => (int)Math.Round(value * Ratings.MaxStars),
            <= Ratings.MaxStars => (int)Math.Round(value),
            _ => Ratings.Stars((int)Math.Round(Math.Min(value, 100))),
        };
        return Stars(stars);
    }

    private static int? Stars(int stars) => Ratings.FromStars(stars);

    private static string Hundred(int stars) => (stars * Ratings.PointsPerStar).ToString(CultureInfo.InvariantCulture);

    // ---- the ID3v2 and ASF scales ----------------------------------------------------------------------------------

    internal static byte PopmByte(int stars) => stars switch
    {
        <= 0 => 0,
        1 => 1,
        2 => 64,
        3 => 128,
        4 => 196,
        _ => 255,
    };

    internal static int PopmStars(byte value) => value switch
    {
        0 => 0,
        < 32 => 1,
        < 96 => 2,
        < 160 => 3,
        < 224 => 4,
        _ => 5,
    };

    private static int AsfValue(int stars) => stars switch
    {
        <= 0 => 0,
        1 => 1,
        2 => 25,
        3 => 50,
        4 => 75,
        _ => 99,
    };

    private static int AsfStars(string? text)
    {
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) || value <= 0)
        {
            return 0;
        }

        return value switch
        {
            <= 1 => 1,
            <= 25 => 2,
            <= 50 => 3,
            <= 75 => 4,
            _ => 5,
        };
    }

    // ---- which tag is the container's own --------------------------------------------------------------------------

    /// <summary>The tag kind a format keeps its metadata in, or null for one this does not know how to rate.</summary>
    private static TagTypes? Native(TagFile file) => file switch
    {
        TagLib.Flac.File or TagLib.Ogg.File => TagTypes.Xiph,
        TagLib.Mpeg4.File => TagTypes.Apple,
        TagLib.Asf.File => TagTypes.Asf,
        TagLib.WavPack.File or TagLib.Ape.File or TagLib.MusePack.File => TagTypes.Ape,
        TagLib.Mpeg.AudioFile or TagLib.Riff.File or TagLib.Aiff.File => TagTypes.Id3v2,
        _ => null,
    };

    private static IEnumerable<TagTypes> Order(TagFile file)
    {
        TagTypes[] all = [TagTypes.Xiph, TagTypes.Apple, TagTypes.Asf, TagTypes.Ape, TagTypes.Id3v2];
        if (Native(file) is { } native)
        {
            yield return native;
        }

        foreach (TagTypes type in all)
        {
            if (type != Native(file))
            {
                yield return type;
            }
        }
    }
}

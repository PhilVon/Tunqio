using Tunqio.Library.Art;

namespace Tunqio.Library.Tests.Art;

/// <summary>The folder-image fallback's choice: cover, then folder, then front, then any .jpg; case-insensitive; stable.</summary>
public sealed class FolderArtTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tunqio-folderart-" + Guid.NewGuid().ToString("N"));

    public FolderArtTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Touch(string name)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, [1, 2, 3]);
        return path;
    }

    [Fact]
    public void Cover_beats_folder_beats_front_beats_any_jpg()
    {
        string any = Touch("scan001.jpg");
        FolderArt.Find(_dir).Should().Be(any);
        string front = Touch("front.png");
        FolderArt.Find(_dir).Should().Be(front);
        string folder = Touch("Folder.JPG");
        FolderArt.Find(_dir).Should().Be(folder, "names are compared case-insensitively");
        string cover = Touch("cover.jpeg");
        FolderArt.Find(_dir).Should().Be(cover);
    }

    [Fact]
    public void Only_images_count_and_an_unnamed_png_is_not_taken()
    {
        Touch("cover.txt");
        Touch("notes.png");
        Touch("01 - Track.flac");
        FolderArt.Find(_dir).Should().BeNull();
        string jpg = Touch("back.jpg");
        FolderArt.Find(_dir).Should().Be(jpg, "any .jpg is the last resort");
    }

    [Fact]
    public void Ties_at_the_same_rank_resolve_to_the_ordinal_first_path()
    {
        Touch("cover.png");
        string first = Touch("cover.jpg");
        FolderArt.Find(_dir).Should().Be(first);
    }

    [Fact]
    public void A_missing_directory_gives_nothing()
    {
        FolderArt.Find(Path.Combine(_dir, "nope")).Should().BeNull();
    }
}

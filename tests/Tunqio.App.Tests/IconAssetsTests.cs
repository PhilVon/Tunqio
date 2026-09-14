using System.Buffers.Binary;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Tunqio.App.Shell;
using Tunqio.App.Tray;

namespace Tunqio.App.Tests;

/// <summary>
/// T-191: every icon asset tools/IconGen renders from assets/brand/tunqio-icon.svg is committed at the pixel size
/// assets/brand/icon-assets.json declares, and the manifest, the project, the window, the tray and the About page name files
/// that list holds. The files are parsed here, not trusted by name: an .ico's directory and each PNG entry inside it, and each
/// PNG's IHDR.
/// </summary>
public sealed class IconAssetsTests
{
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];

    private static readonly Spec Assets = Spec.Load();

    /// <summary>Light taskbar, then dark.</summary>
    private static readonly bool[] TaskbarThemes = [true, false];

    [Fact]
    public void The_source_is_the_committed_svg()
    {
        string source = RepoPaths.File(Assets.Source.Split('/'));
        System.IO.File.Exists(source).Should().BeTrue();
        XDocument svg = XDocument.Load(source);
        svg.Root!.Name.LocalName.Should().Be("svg");
        svg.Root.Attribute("viewBox")!.Value.Should().Be("0 0 512 512");
    }

    [Fact]
    public void Each_ico_holds_a_png_entry_at_every_declared_size()
    {
        var problems = new List<string>();
        foreach ((string path, int[] sizes) in Assets.Icons)
        {
            byte[] bytes = System.IO.File.ReadAllBytes(Assets.File(path));
            int[] found = ReadIco(bytes, path, problems);
            if (!found.SequenceEqual(sizes))
            {
                problems.Add($"{path}: entries [{string.Join(",", found)}], declared [{string.Join(",", sizes)}]");
            }
        }

        problems.Should().BeEmpty();
    }

    [Fact]
    public void The_app_icon_has_every_size_windows_asks_for()
    {
        Assets.Icons.Single(i => i.Path == AppIcon.RelativePath).Sizes.Should().Contain([16, 20, 24, 32, 40, 48, 64, 256]);
    }

    [Fact]
    public void Each_png_has_its_declared_pixel_size()
    {
        var problems = new List<string>();
        foreach ((string path, int width, int height) in Assets.Images)
        {
            (int w, int h) = ReadPngSize(System.IO.File.ReadAllBytes(Assets.File(path)));
            if (w != width || h != height)
            {
                problems.Add($"{path}: {w}x{h}, declared {width}x{height}");
            }
        }

        Assets.Images.Should().NotBeEmpty();
        problems.Should().BeEmpty();
    }

    [Fact]
    public void Every_file_in_the_assets_folder_is_one_the_generator_writes()
    {
        string folder = Assets.File("Assets");
        var onDisk = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Select(f => "Assets/" + Path.GetRelativePath(folder, f).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToList();
        var declared = Assets.Icons.Select(i => i.Path).Concat(Assets.Images.Select(i => i.Path)).Order(StringComparer.Ordinal).ToList();

        onDisk.Should().Equal(declared, "a file nobody generates is a placeholder left behind, and a declared one missing is not committed");
    }

    [Fact]
    public void Every_tile_has_the_five_scales_and_the_taskbar_icon_its_target_sizes_plain_and_unplated()
    {
        string[] scales = ["100", "125", "150", "200", "400"];
        foreach (string tile in new[] { "Square44x44Logo", "Square150x150Logo", "Wide310x150Logo", "StoreLogo", "SplashScreen" })
        {
            foreach (string scale in scales)
            {
                Assets.Images.Should().Contain(i => i.Path == $"Assets/{tile}.scale-{scale}.png");
            }
        }

        foreach (int size in new[] { 16, 24, 32, 48, 256 })
        {
            Assets.Images.Should().Contain(i => i.Path == $"Assets/Square44x44Logo.targetsize-{size}.png" && i.Width == size);
            Assets.Images.Should().Contain(i => i.Path == $"Assets/Square44x44Logo.targetsize-{size}_altform-unplated.png" && i.Width == size);
        }
    }

    [Fact]
    public void Every_image_the_manifest_names_resolves_to_generated_files()
    {
        XDocument manifest = XDocument.Load(Assets.File("Package.appxmanifest"));
        var named = new List<string>();
        foreach (XElement element in manifest.Descendants())
        {
            if (element.Name.LocalName is "Logo")
            {
                named.Add(element.Value.Trim());
            }

            foreach (XAttribute attribute in element.Attributes())
            {
                if (attribute.Name.LocalName is "Square150x150Logo" or "Square44x44Logo" or "Wide310x150Logo"
                    || (element.Name.LocalName == "SplashScreen" && attribute.Name.LocalName == "Image"))
                {
                    named.Add(attribute.Value);
                }
            }
        }

        named.Should().Contain(@"Assets\FileAssociation.png", "the tunqio-audio association has its own logo (T-74 left it on the app icon)");
        named.Should().HaveCount(6, "the store logo, the three tiles, the splash screen and the file association logo");
        foreach (string reference in named)
        {
            string path = reference.Replace('\\', '/');
            string stem = Regex.Escape(path[..^".png".Length]);
            Assets.Images.Should().Contain(
                i => i.Path == path || Regex.IsMatch(i.Path, "^" + stem + @"\.(scale|targetsize)-[^.]+\.png$"),
                $"the manifest names {reference}");
        }
    }

    [Fact]
    public void The_project_gives_the_executable_the_app_icon()
    {
        XDocument project = XDocument.Load(Assets.File("Tunqio.App.csproj"));
        string? icon = project.Descendants("ApplicationIcon").SingleOrDefault()?.Value;
        icon.Should().Be(AppIcon.RelativePath.Replace('/', '\\'));
    }

    [Fact]
    public void The_tray_variants_are_the_files_the_tray_looks_for()
    {
        var themed = TaskbarThemes
            .SelectMany(light => TrayIconFiles.Candidates(16, light))
            .Where(c => c.Contains("-light", StringComparison.Ordinal) || c.Contains("-dark", StringComparison.Ordinal))
            .Distinct()
            .ToList();

        themed.Should().HaveCount(4);
        foreach (string candidate in themed)
        {
            Assets.Icons.Should().Contain(i => i.Path == candidate);
        }
    }

    [Fact]
    public void The_about_page_logo_is_a_generated_file()
    {
        string page = System.IO.File.ReadAllText(Assets.File("Shell", "AboutSettingsPage.xaml"));
        Match source = Regex.Match(page, "Source=\"ms-appx:///([^\"]+)\"");
        source.Success.Should().BeTrue();
        Assets.Images.Should().Contain(i => i.Path == source.Groups[1].Value);
        page.Should().Contain("AutomationProperties.Name=\"Tunqio logo\"");
    }

    [Fact]
    public void The_window_icon_path_is_under_the_base_directory()
    {
        AppIcon.PathUnder(@"C:\app").Should().Be(@"C:\app\Assets\Tunqio.ico");
    }

    private static int[] ReadIco(byte[] bytes, string path, List<string> problems)
    {
        if (bytes.Length < 6 || BinaryPrimitives.ReadUInt16LittleEndian(bytes) != 0 || BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(2)) != 1)
        {
            problems.Add($"{path}: not an icon file");
            return [];
        }

        int count = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4));
        var sizes = new int[count];
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> entry = bytes.AsSpan(6 + (16 * i), 16);
            int width = entry[0] == 0 ? 256 : entry[0];
            int height = entry[1] == 0 ? 256 : entry[1];
            int bits = BinaryPrimitives.ReadUInt16LittleEndian(entry[6..]);
            int length = BinaryPrimitives.ReadInt32LittleEndian(entry[8..]);
            int offset = BinaryPrimitives.ReadInt32LittleEndian(entry[12..]);
            sizes[i] = width;
            if (offset + length > bytes.Length)
            {
                problems.Add($"{path}: entry {i} runs past the end of the file");
                continue;
            }

            (int w, int h) = ReadPngSize(bytes.AsSpan(offset, length).ToArray());
            if (w != width || h != height || bits != 32)
            {
                problems.Add($"{path}: entry {i} says {width}x{height} {bits} bpp, its PNG is {w}x{h}");
            }
        }

        return sizes;
    }

    private static (int Width, int Height) ReadPngSize(byte[] bytes)
    {
        if (bytes.Length < 24 || !bytes.AsSpan(0, 8).SequenceEqual(PngSignature) || !bytes.AsSpan(12, 4).SequenceEqual("IHDR"u8))
        {
            return (-1, -1);
        }

        return (BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16)), BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20)));
    }

    /// <summary>assets/brand/icon-assets.json, the list tools/IconGen writes from.</summary>
    private sealed record Spec(string Source, string AppRoot, IReadOnlyList<(string Path, int[] Sizes)> Icons, IReadOnlyList<(string Path, int Width, int Height)> Images)
    {
        public string File(params string[] segments) => RepoPaths.File([.. AppRoot.Split('/'), .. segments.SelectMany(s => s.Split('/'))]);

        public static Spec Load()
        {
            using JsonDocument json = JsonDocument.Parse(System.IO.File.ReadAllText(RepoPaths.File("assets", "brand", "icon-assets.json")));
            JsonElement root = json.RootElement;
            return new Spec(
                root.GetProperty("source").GetString()!,
                root.GetProperty("appRoot").GetString()!,
                root.GetProperty("icons").EnumerateArray()
                    .Select(i => (i.GetProperty("path").GetString()!, i.GetProperty("sizes").EnumerateArray().Select(s => s.GetInt32()).ToArray()))
                    .ToList(),
                root.GetProperty("images").EnumerateArray()
                    .Select(i => (i.GetProperty("path").GetString()!, i.GetProperty("width").GetInt32(), i.GetProperty("height").GetInt32()))
                    .ToList());
        }
    }
}

using Tunqio.FixtureGen;

if (args.Length == 0)
{
    return Usage();
}

string? Option(string name)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

switch (args[0])
{
    case "files":
        {
            string output = Option("-out") ?? Path.Combine("tests", "fixtures", "library");
            FfmpegEncoder? ffmpeg = FfmpegEncoder.Find(Option("-ffmpeg"));
            Console.WriteLine(ffmpeg is null
                ? "ffmpeg not found: only WAV and AIFF fixtures will be written"
                : $"ffmpeg: {ffmpeg.Path}");
            var builder = new FixtureLibraryBuilder(ffmpeg, Console.Out);
            FixtureManifest manifest = builder.Build(Path.GetFullPath(output));
            Console.WriteLine($"{manifest.Files.Count} files, formats: {string.Join(", ", manifest.Formats)}" +
                              (manifest.SkippedFormats.Count > 0 ? $"; skipped: {string.Join(", ", manifest.SkippedFormats)}" : string.Empty));
            Console.WriteLine($"tree hash {FixtureLibraryBuilder.TreeHash(Path.GetFullPath(output))}");
            return 0;
        }

    case "db":
        {
            string output = Option("-out") ?? Path.Combine("tests", "fixtures", "library-100k.db");
            int tracks = int.Parse(Option("-tracks") ?? "100000", System.Globalization.CultureInfo.InvariantCulture);
            Library100kBuilder.Build(Path.GetFullPath(output), tracks, FixtureLibraryBuilder.Seed, Console.Out);
            return 0;
        }

    case "tree":
        {
            string output = Option("-out") ?? Path.Combine("artifacts", "scan-tree");
            int tracks = int.Parse(Option("-tracks") ?? "10000", System.Globalization.CultureInfo.InvariantCulture);
            IReadOnlyList<string> paths = ScanTreeBuilder.Build(Path.GetFullPath(output), tracks, embedArt: args.Contains("-art"));
            Console.WriteLine($"{paths.Count} files under {Path.GetFullPath(output)}");
            return 0;
        }

    default:
        return Usage();
}

static int Usage()
{
    Console.Error.WriteLine("""
        Tunqio.FixtureGen
          files [-out DIR] [-ffmpeg PATH]   generate the fixture library (default tests/fixtures/library)
          db    [-out FILE] [-tracks N]     generate the synthetic 100k-track database (default tests/fixtures/library-100k.db)
          tree  [-out DIR] [-tracks N] [-art]  generate a large tagged-WAV folder tree for scanner timing (default artifacts/scan-tree, 10000 files; -art embeds one cover per album)
        """);
    return 2;
}

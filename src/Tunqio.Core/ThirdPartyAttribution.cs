namespace Tunqio.Core;

/// <summary>
/// Attribution lines the About page must show (E0-S3, E6-S5). <c>THIRD-PARTY-NOTICES.md</c> carries the same
/// text; <c>Tunqio.Core.Tests</c> keeps the two in step. Full licence texts are shipped next to the executable
/// under <c>licenses/</c> (the BASS ones are extracted there by <c>tools/fetch-native.ps1</c>).
/// </summary>
public static class ThirdPartyAttribution
{
    /// <summary>
    /// Required by the BASS licence for non-commercial use: credit BASS and link to un4seen.
    /// </summary>
    public const string Bass =
        "Audio playback uses the BASS audio library and its add-ons (BASSmix, BASSWASAPI, BASSFLAC, BASSOPUS, BASSWV, BASS_APE) " +
        "by Un4seen Developments, www.un4seen.com, under the free non-commercial licence.";

    /// <summary>Name of the folder next to the executable that holds the licence texts.</summary>
    public const string LicensesFolderName = "licenses";

    /// <summary>Display names of the components credited in the About page, in the order shown.</summary>
    public static IReadOnlyList<string> Components { get; } =
    [
        "BASS, BASSmix, BASSWASAPI, BASSFLAC, BASSOPUS, BASSWV (Un4seen Developments)",
        "BASS_APE (Sebastian Andersson; Monkey's Audio by Matthew T. Ashland)",
        "pffft (Julien Pommier, FFTPACK licence)",
        "nlohmann/json (MIT)",
        "Windows App SDK and WinUI 3 (Microsoft)",
        "CommunityToolkit.Mvvm (MIT)",
        "Microsoft.Data.Sqlite and SQLite (MIT / public domain)",
        "TagLibSharp (LGPL 2.1)",
        "System.Reactive (MIT)",
        "Serilog (Apache 2.0)",
    ];
}

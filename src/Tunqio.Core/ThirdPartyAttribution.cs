namespace Tunqio.Core;

/// <summary>
/// Attribution lines the About page must show (E0-S3, E6-S5). <c>THIRD-PARTY-NOTICES.md</c> carries the same
/// text; <c>Tunqio.Core.Tests</c> keeps the two in step. Full licence texts are shipped next to the executable
/// under <c>licenses/</c> (the BASS ones are extracted there by <c>tools/fetch-native.ps1</c>). The components the
/// page credits are not listed here: they are the notices file's rows, read by <see cref="ThirdPartyNotices"/> (T-198).
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
}

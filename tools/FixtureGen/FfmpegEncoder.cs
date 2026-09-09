using System.Diagnostics;

namespace Tunqio.FixtureGen;

/// <summary>
/// Encodes a WAV into the lossy and non-PCM fixture formats with ffmpeg in bit-exact mode (no encoder version
/// strings, no timestamps, fixed Ogg serials) so repeated runs produce identical bytes.
/// </summary>
public sealed class FfmpegEncoder
{
    private readonly string _ffmpeg;

    private FfmpegEncoder(string path) => _ffmpeg = path;

    public string Path => _ffmpeg;

    /// <summary>Finds ffmpeg from an explicit path or PATH; null when unavailable.</summary>
    public static FfmpegEncoder? Find(string? explicitPath)
    {
        if (!string.IsNullOrEmpty(explicitPath))
        {
            return File.Exists(explicitPath) ? new FfmpegEncoder(explicitPath) : null;
        }

        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(System.IO.Path.PathSeparator))
        {
            if (dir.Length == 0)
            {
                continue;
            }

            string candidate = System.IO.Path.Combine(dir, "ffmpeg.exe");
            if (File.Exists(candidate))
            {
                return new FfmpegEncoder(candidate);
            }
        }

        return null;
    }

    /// <summary>ffmpeg codec arguments per fixture format.</summary>
    public static string CodecArguments(string format) => format switch
    {
        "flac" => "-c:a flac -compression_level 5",
        "mp3" => "-c:a libmp3lame -b:a 192k -id3v2_version 3",
        "m4a" => "-c:a aac -b:a 160k",
        "alac" => "-c:a alac",
        "ogg" => "-c:a libvorbis -q:a 4",
        "opus" => "-c:a libopus -b:a 96k",
        "wma" => "-c:a wmav2 -b:a 128k",
        "wv" => "-c:a wavpack",
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "no ffmpeg recipe"),
    };

    /// <summary>File extension for a fixture format.</summary>
    public static string Extension(string format) => format == "alac" ? "m4a" : format;

    public void Encode(string wavPath, string format, string outputPath)
    {
        var psi = new ProcessStartInfo(_ffmpeg)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        foreach (string arg in new[] { "-nostdin", "-hide_banner", "-loglevel", "error", "-y", "-i", wavPath, "-map_metadata", "-1", "-map_chapters", "-1", "-fflags", "+bitexact", "-flags:a", "+bitexact" })
        {
            psi.ArgumentList.Add(arg);
        }

        foreach (string arg in CodecArguments(format).Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            psi.ArgumentList.Add(arg);
        }

        psi.ArgumentList.Add(outputPath);

        using Process process = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg did not start");
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"ffmpeg failed ({process.ExitCode}) encoding {format}: {stderr}");
        }
    }
}

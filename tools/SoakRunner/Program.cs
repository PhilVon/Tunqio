using System.Globalization;
using System.Text.Json;
using Tunqio.SoakRunner;

SoakOptions options;
try
{
    options = SoakOptions.Parse(args, RepoPaths.Root);
}
catch (SoakUsageException e)
{
    Console.Error.WriteLine(e.Message);
    return 2;
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    Console.WriteLine("stopping after the current sample…");
    cancellation.Cancel();
};

SoakReport report;
try
{
    // The same named mutex mpcore.tests takes around its device cases (T-173): an exclusive-mode soak that
    // started beside the native suite took the device from under a case and turned a sweep red with four
    // assertions that described the audio engine. Taking turns is the fix; a run that cannot get the lock says so
    // and goes ahead, because a soak is usually the only thing on the machine.
    using var lease = DeviceLease.Take(TimeSpan.FromMinutes(3));
    if (!lease.Held)
    {
        Console.WriteLine("another process held the WASAPI output device for the whole three minutes this run waited; soaking anyway, so a device error below is contention and not a defect");
    }

    await using var soak = new Soak(options, Console.Out);
    report = await soak.RunAsync(cancellation.Token).ConfigureAwait(false);
}
catch (SoakUsageException e)
{
    Console.Error.WriteLine(e.Message);
    return 2;
}

string json = JsonSerializer.Serialize(report, SoakReport.JsonOptions);
if (options.ReportPath is { } path)
{
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, json);
    Console.WriteLine("report written to " + path);
}

Console.WriteLine();
Console.WriteLine(json);
Console.WriteLine();
Console.WriteLine(report.Verdict);
return report.Passed ? 0 : 1;

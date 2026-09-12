using System.Text.Json;
using Tunqio.LatencyRunner;

LatencyOptions options;
try
{
    options = LatencyOptions.Parse(args, RepoPaths.Root);
}
catch (LatencyUsageException e)
{
    Console.Error.WriteLine(e.Message);
    return 2;
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    Console.WriteLine("stopping after the current phase…");
    cancellation.Cancel();
};

LatencyReport report;
try
{
    using var harness = new LatencyHarness(options, Console.Out);
    report = await harness.RunAsync(cancellation.Token).ConfigureAwait(false);
}
catch (LatencyUsageException e)
{
    Console.Error.WriteLine(e.Message);
    return 2;
}

string json = JsonSerializer.Serialize(report, LatencyReport.JsonOptions);
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

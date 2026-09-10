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

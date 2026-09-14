using System.Globalization;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Display;

namespace Tunqio.App.Crash;

/// <summary>
/// The last <see cref="Capacity"/> log lines of this session, formatted as the log file formats them (E8-S5). A Serilog sink
/// beside the file sink: a crash report takes its lines from here rather than reading the shared daily file back, because
/// that file interleaves every process on the data root and reading it means opening and scanning a file from inside a
/// crashing process. Formatting happens as each event is logged, so taking the lines at a crash is one copy under a lock.
/// </summary>
public sealed class CrashLogBuffer : ILogEventSink
{
    /// <summary>How many lines a crash report carries.</summary>
    public const int Capacity = 200;

    private readonly string[] _lines = new string[Capacity];
    private readonly MessageTemplateTextFormatter _formatter;
    private readonly object _gate = new();
    private int _next;
    private int _count;

    /// <param name="outputTemplate">The log file's output template, so a report's lines read like the file's.</param>
    public CrashLogBuffer(string outputTemplate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputTemplate);
        _formatter = new MessageTemplateTextFormatter(outputTemplate, CultureInfo.InvariantCulture);
    }

    public void Emit(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        _formatter.Format(logEvent, writer);
        Add(writer.ToString());
    }

    /// <summary>Adds text, one entry per physical line (an exception's stack is several), dropping the oldest past the capacity.</summary>
    public void Add(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string[] lines = text.TrimEnd('\r', '\n').Split('\n');
        lock (_gate)
        {
            foreach (string line in lines)
            {
                _lines[_next] = line.TrimEnd('\r');
                _next = (_next + 1) % Capacity;
                _count = Math.Min(_count + 1, Capacity);
            }
        }
    }

    /// <summary>The lines held, oldest first.</summary>
    public IReadOnlyList<string> Snapshot()
    {
        lock (_gate)
        {
            var copy = new string[_count];
            int start = (_next - _count + Capacity) % Capacity;
            for (int i = 0; i < _count; i++)
            {
                copy[i] = _lines[(start + i) % Capacity];
            }

            return copy;
        }
    }
}

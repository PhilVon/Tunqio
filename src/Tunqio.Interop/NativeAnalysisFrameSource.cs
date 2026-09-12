using System.Reactive.Subjects;
using System.Runtime.CompilerServices;
using Tunqio.Core.Visualization;

namespace Tunqio.Interop;

/// <summary>
/// <see cref="IAnalysisFrameSource"/> over <see cref="NativeEngine"/> (E4-S1).
///
/// <para>
/// <see cref="TryGetLatest"/> is one <c>memcpy</c> out of the native triple buffer into a caller-owned struct,
/// then the arrays the DTO is made of. It does not lock and does not wait for the analysis thread: what it
/// copies is whatever frame was complete when it asked. That is the whole cost of the binding, and it is what
/// the &lt; 5 µs claim in <c>Tunqio.Benchmarks</c> is measured against.
/// </para>
/// <para>
/// <see cref="Frames"/> is a 30 Hz poll on a timer of its own rather than a native callback: the analysis thread
/// runs at Pro Audio priority and must not be made to call into managed code, and a UI that wants to follow the
/// music does not want 94 frames a second anyway. Only a frame with a sequence the source has not seen is
/// pushed, so a paused engine produces silence on this stream rather than the same frame over and over.
/// </para>
/// </summary>
public sealed class NativeAnalysisFrameSource : IAnalysisFrameSource, IDisposable
{
    /// <summary>Poll interval for <see cref="Frames"/>: 30 Hz, per docs/solution-structure.md.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(1000.0 / 30.0);

    private readonly NativeEngine _engine;
    private readonly Subject<AnalysisFrame> _frames = new();
    private readonly Timer _timer;
    private readonly object _pollGate = new();
    private uint _lastPushed;
    private int _disposed;

    public NativeAnalysisFrameSource(NativeEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        _engine = engine;
        _timer = new Timer(Poll, null, PollInterval, PollInterval);
    }

    public IObservable<AnalysisFrame> Frames => _frames;

    /// <summary>Frames pushed to <see cref="Frames"/> since construction. Diagnostics and tests.</summary>
    public long Pushed { get; private set; }

    public bool TryGetLatest(out AnalysisFrame frame)
    {
        // A buffer per call rather than a shared one: this is callable from any thread at any time, and a shared
        // staging struct would need a lock that the 30 Hz poll would then contend for. 6 KB on the stack costs
        // less than the contention would, and nothing escapes it but the arrays below.
        Unsafe.SkipInit(out MpAnalysisFrameBuffer buffer); // the native copy fills every byte of it, or is refused
        if (!_engine.TryGetLatestAnalysis(ref buffer))
        {
            frame = default;
            return false;
        }

        frame = Convert(in buffer);
        return true;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _timer.Dispose();
        // Under the same gate the poll takes, so a tick already in flight has finished before the subject goes.
        lock (_pollGate)
        {
            _frames.OnCompleted();
            _frames.Dispose();
        }
    }

    /// <summary>
    /// Copies the fixed buffers out into arrays the caller owns. The spans on
    /// <see cref="MpAnalysisFrameBuffer"/> point into a struct that is about to go out of scope, so a frame that
    /// outlives the call has to be made of its own memory - and a record struct handing out a view of someone
    /// else's buffer would be a bug waiting for its first consumer.
    /// </summary>
    private static AnalysisFrame Convert(in MpAnalysisFrameBuffer buffer) =>
        new(buffer.Sequence,
            buffer.MixerBytePosition,
            buffer.QpcTicks,
            buffer.Spectrum.ToArray(),
            buffer.Waveform.ToArray(),
            buffer.Rms,
            buffer.Peak,
            buffer.SpectralCentroidHz,
            buffer.HarmonicRatio,
            buffer.Bands.ToArray(),
            buffer.Onset,
            buffer.Discontinuities);

    private void Poll(object? state)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        lock (_pollGate)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            try
            {
                if (!TryGetLatest(out AnalysisFrame frame) || frame.Sequence == _lastPushed)
                {
                    return;
                }

                _lastPushed = frame.Sequence;
                Pushed++;
                _frames.OnNext(frame);
            }
            catch (ObjectDisposedException)
            {
                // The engine went away between the disposed check and the call; nothing left to publish.
            }
        }
    }
}

namespace Tunqio.App.Activation;

/// <summary>
/// Where redirected activations wait for the window. <c>AppInstance.Activated</c> is subscribed in <c>Main</c>, before the
/// XAML app exists, so an activation redirected while the first instance is still starting would otherwise have nowhere to
/// go. Everything posted before <see cref="Attach"/> is delivered, in order, when it is called; everything after, at once.
/// Thread-safe: activations arrive on a Windows thread.
/// </summary>
public sealed class ActivationInbox
{
    private readonly object _gate = new();
    private readonly List<IReadOnlyList<string>> _pending = [];
    private Action<IReadOnlyList<string>>? _handler;

    /// <summary>How many activations are waiting for a handler.</summary>
    public int PendingCount
    {
        get
        {
            lock (_gate)
            {
                return _pending.Count;
            }
        }
    }

    /// <summary>Delivers one activation's tokens, or holds them until a handler is attached.</summary>
    public void Post(IReadOnlyList<string> tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        Action<IReadOnlyList<string>>? handler;
        lock (_gate)
        {
            handler = _handler;
            if (handler is null)
            {
                _pending.Add(tokens);
                return;
            }
        }

        handler(tokens);
    }

    /// <summary>Sets the handler and delivers what was waiting.</summary>
    public void Attach(Action<IReadOnlyList<string>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        IReadOnlyList<string>[] waiting;
        lock (_gate)
        {
            _handler = handler;
            waiting = [.. _pending];
            _pending.Clear();
        }

        foreach (IReadOnlyList<string> tokens in waiting)
        {
            handler(tokens);
        }
    }
}

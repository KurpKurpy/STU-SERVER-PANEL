using System.Threading.Channels;

namespace Panel.Features.Notifications;

// Phase 6b Ruling 4: hooks only enqueue, after their own save. A full queue drops the event (counted) rather than slow the caller down.
// ponytail: in memory, so events queued when the panel stops are lost; an outbox table if that ever matters.
public sealed class Notifier(ILogger<Notifier>? log = null, int capacity = 1000)
{
    private readonly Channel<NotificationEvent> _queue = Channel.CreateBounded<NotificationEvent>(
        new BoundedChannelOptions(capacity) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
    private long _dropped;

    public long Dropped => Interlocked.Read(ref _dropped);

    internal ChannelReader<NotificationEvent> Reader => _queue.Reader;

    public bool Enqueue(NotificationEvent e)
    {
        if (_queue.Writer.TryWrite(e)) return true;
        if (Interlocked.Increment(ref _dropped) % 100 == 1) log?.LogWarning("The notification queue is full; {Dropped} notifications dropped so far", Dropped);
        return false;
    }
}

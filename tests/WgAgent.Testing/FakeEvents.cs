using System.Runtime.CompilerServices;
using System.Threading.Channels;
using WgAgent.Platform;

namespace WgAgent.Testing;

/// <summary>
/// An in-memory <see cref="ILinkEvents"/> subscription. A test pushes an event
/// and asserts that the runner reconciled, without a netlink socket.
/// </summary>
public sealed class FakeEvents : ILinkEvents
{
    private readonly Channel<LinkEvent> _ch = Channel.CreateUnbounded<LinkEvent>();

    /// <summary>
    /// When set, subscription throws it. It covers the case a runner has to
    /// survive: a subscription that cannot be established leaves the periodic
    /// timer as the only trigger rather than stopping the agent — REQ-RCN-021.
    /// </summary>
    public Exception? SubscribeError { get; set; }

    public async IAsyncEnumerable<LinkEvent> SubscribeAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (SubscribeError is not null) throw SubscribeError;
        await foreach (var e in _ch.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            yield return e;
    }

    /// <summary>Delivers one event.</summary>
    public void Send(string name, bool deleted) => _ch.Writer.TryWrite(new LinkEvent(name, deleted));

    /// <summary>Closes the stream, so <see cref="SubscribeAsync"/> completes.</summary>
    public void Complete() => _ch.Writer.TryComplete();
}

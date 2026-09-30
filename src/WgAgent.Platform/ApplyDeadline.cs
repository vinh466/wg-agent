namespace WgAgent.Platform;

/// <summary>
/// The moment by which the change being applied must be done (REQ-API-022). The service sets it
/// for one write, under the store lock, and every program the adapters start shares it.
/// </summary>
public sealed class ApplyDeadline
{
    private DateTimeOffset? _until;

    public void Begin(TimeSpan timeout) => _until = DateTimeOffset.UtcNow + timeout;
    public void End() => _until = null;

    /// <summary>The time left, no more than <paramref name="cap"/>; zero once the deadline has passed.</summary>
    public TimeSpan Remaining(TimeSpan cap)
    {
        if (_until is not { } until) return cap;
        var left = until - DateTimeOffset.UtcNow;
        return left <= TimeSpan.Zero ? TimeSpan.Zero : left < cap ? left : cap;
    }
}

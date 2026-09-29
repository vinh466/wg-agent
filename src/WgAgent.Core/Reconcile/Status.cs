namespace WgAgent.Core.Reconcile;

// SPEC-03. The algorithm of REQ-RCN-022, written against the ports of
// WgAgent.Platform, so the whole of it runs against the in-memory platform
// without privilege. Step 10 — the nftables table — is deferred under B-04 and
// is absent here; step 9 is present, because B-04 keeps REQ-FWD-020.

/// <summary>The closed set of REQ-RES-032.</summary>
public enum ConditionState
{
    Ready,
    Progressing,
    Degraded,
}

/// <summary>The oper-state values of REQ-RES-019.</summary>
public enum OperState
{
    Up,
    Down,
    Absent,
}

/// <summary>The reason codes this module produces, all members of REQ-API-041.</summary>
public static class ReconcileReasons
{
    /// <summary>
    /// What REQ-RCN-040 requires a failed pass to carry. Step 2 of REQ-RCN-022
    /// uses it too: a link of the wrong type stops reconciliation, and the
    /// closed set of REQ-API-041 holds no narrower code, so the specifics go in
    /// the message.
    /// </summary>
    public const string ReconcileFailed = "RECONCILE_FAILED";

    /// <summary>
    /// Desired state that cannot be applied as written, as distinct from an
    /// application that failed.
    /// </summary>
    public const string StoreCorrupt = "STORE_CORRUPT";

    /// <summary>
    /// What step 9 carries when the forwarding sysctl cannot be written.
    /// REQ-FWD-025 would surface this at startup instead, and is deferred under
    /// B-04, so reconcile is where it appears.
    /// </summary>
    public const string SysctlWriteDenied = "SYSCTL_WRITE_DENIED";
}

/// <summary>REQ-RES-032: exactly one state, with a reason and a message.</summary>
public sealed record Condition(ConditionState State, string Reason = "", string Message = "");

/// <summary>One entry of REQ-RES-033.</summary>
public sealed record Warning(string Reason, string Message);

/// <summary>
/// What step 11 of REQ-RCN-022 writes.
/// </summary>
/// <remarks>
/// REQ-RCN-050 keeps status out of the store, so the engine holds it in memory
/// and the API reads it from there. A restart therefore starts with no status
/// and produces one on the startup pass of REQ-RCN-020.
/// </remarks>
public sealed class ReconcileStatus
{
    public required string Name { get; init; }
    public Model.Ownership Ownership { get; set; } = Model.Ownership.Managed;
    public OperState OperState { get; set; } = OperState.Absent;
    public int PeerCount { get; set; }
    public Condition Condition { get; set; } = new(ConditionState.Progressing);
    public List<Warning> Warnings { get; } = [];
    public DateTimeOffset UpdatedAt { get; set; }

    internal void Ready() => Condition = new Condition(ConditionState.Ready);

    internal void Degraded(string reason, string message)
        => Condition = new Condition(ConditionState.Degraded, reason, message);

    internal void Warn(string reason, string message) => Warnings.Add(new Warning(reason, message));
}

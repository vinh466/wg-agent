namespace WgAgent.Core.Model;

/// <summary>
/// The vocabulary of REQ-RES-017. Every value is decided from desired state and
/// the deletion record, both of which the store holds — creation history is not
/// among them, because the agent has no durable memory of it.
/// </summary>
public enum Ownership
{
    /// <summary>Desired state describes the interface.</summary>
    Managed,

    /// <summary>No record of the interface at all.</summary>
    Foreign,

    /// <summary>A deletion record names it, but its link is still present.</summary>
    Orphaned,
}

public static class OwnershipRule
{
    /// <summary>
    /// Decides REQ-RES-017's three values from the two facts the store holds.
    /// Order matters: desired state wins, then the deletion record.
    /// </summary>
    /// <remarks>
    /// It takes the two facts rather than the store that answers them, so the
    /// rule is one decision testable on its own and both the diagnostics report
    /// and the reconcile classification of REQ-RCN-036 reach the same verdict.
    /// </remarks>
    public static Ownership Of(bool describes, bool deletionRecord)
    {
        if (describes) return Ownership.Managed;
        if (deletionRecord) return Ownership.Orphaned;
        return Ownership.Foreign;
    }
}

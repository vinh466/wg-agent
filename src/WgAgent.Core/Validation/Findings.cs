namespace WgAgent.Core.Validation;

// SPEC-07. Every rule lives in this module and nowhere else, which is what stops
// the same check drifting apart in two call sites. A rule is a pure function of
// the spec, the peers, the host facts and desired state, so the whole module
// runs against the in-memory platform without privilege.
//
// REQ-VAL-001 blocks the write on an error; REQ-VAL-002 stores a warning in
// status.warnings and lets the write through.

/// <summary>
/// The operation being validated. REQ-VAL-015 applies to a create alone, and
/// REQ-VAL-013 and REQ-VAL-014 exclude the interface the spec names, so the
/// rules cannot be evaluated without knowing which is which.
/// </summary>
public enum Op
{
    /// <summary>A new interface. REQ-VAL-015 refuses a name whose link exists.</summary>
    Create,

    /// <summary>Changes a stored spec. The link exists by definition.</summary>
    Update,

    /// <summary>
    /// Reads an existing link into desired state under REQ-RCN-060. The link
    /// existing is the premise, not a fault.
    /// </summary>
    Adopt,
}

/// <summary>
/// One rule's output. The reason code comes from the closed set of REQ-API-041,
/// which REQ-VAL-002 and REQ-RES-033 both rely on: a caller branches on the code
/// rather than parsing the message.
/// </summary>
public sealed record Finding(string Reason, string Field, string Message);

/// <summary>The two severities SPEC-07 defines.</summary>
public sealed class ValidationResult
{
    private readonly List<Finding> _errors = [];
    private readonly List<Finding> _warnings = [];

    public IReadOnlyList<Finding> Errors => _errors;
    public IReadOnlyList<Finding> Warnings => _warnings;

    /// <summary>Whether REQ-VAL-001 refuses the write.</summary>
    public bool Blocked => _errors.Count > 0;

    /// <summary>
    /// The first error, or null. Its reason code is the one the API returns, so
    /// the order rules run in — fixed by <see cref="Validator"/> — is what makes
    /// that reproducible rather than left to iteration order.
    /// </summary>
    public Finding? FirstError => _errors.Count > 0 ? _errors[0] : null;

    internal void Errf(string reason, string field, string message)
        => _errors.Add(new Finding(reason, field, message));

    internal void Warnf(string reason, string field, string message)
        => _warnings.Add(new Finding(reason, field, message));

    /// <summary>
    /// Throws the first blocking finding as an exception, or does nothing.
    /// REQ-VAL-001: the reason code the API reports is the first error's.
    /// </summary>
    public void ThrowIfBlocked()
    {
        if (Blocked) throw new ValidationException(_errors[0], _errors);
    }
}

/// <summary>Carries a blocking finding out of the module — REQ-VAL-001.</summary>
public sealed class ValidationException(Finding finding, IReadOnlyList<Finding> all)
    : Exception(Format(finding))
{
    public Finding Finding { get; } = finding;
    public IReadOnlyList<Finding> All { get; } = all;

    /// <summary>The reason code the API reports.</summary>
    public string Reason => Finding.Reason;

    private static string Format(Finding f)
        => f.Field.Length == 0
            ? $"{f.Reason}: {f.Message}"
            : $"{f.Reason}: {f.Field}: {f.Message}";
}

/// <summary>
/// The reason codes, from the closed set of REQ-API-041. Each names the
/// requirement that produces it, so a code and its rule cannot drift apart.
/// </summary>
public static class ReasonCodes
{
    public const string NameInvalid = "INTERFACE_NAME_INVALID";              // REQ-VAL-010
    public const string PublicKeyInvalid = "PUBLIC_KEY_INVALID";            // REQ-VAL-011
    public const string AllowedIPsDuplicate = "ALLOWED_IPS_DUPLICATE";      // REQ-VAL-012
    public const string ListenPortInUse = "LISTEN_PORT_IN_USE";            // REQ-VAL-013
    public const string AddressConflict = "ADDRESS_CONFLICT";              // REQ-VAL-014
    public const string InterfaceExists = "INTERFACE_EXISTS";              // REQ-VAL-015
    public const string AddressesRequired = "ADDRESSES_REQUIRED";          // REQ-VAL-016
    public const string AllowedIPsRequired = "ALLOWED_IPS_REQUIRED";        // REQ-VAL-017
    public const string IPv6NotSupported = "IPV6_NOT_SUPPORTED";           // REQ-VAL-020
    public const string NeedsUplink = "FORWARD_POLICY_NEEDS_UPLINK";        // REQ-VAL-021
    public const string PeerInterfaceNotFound = "PEER_INTERFACE_NOT_FOUND"; // REQ-VAL-022
    public const string OneSided = "INTER_INTERFACE_ONE_SIDED";            // REQ-VAL-023
    public const string AllowedIPsOverlap = "ALLOWED_IPS_OVERLAP";          // REQ-VAL-030
    public const string AllowedIPsOutOfSubnet = "ALLOWED_IPS_OUT_OF_SUBNET"; // REQ-VAL-031
    public const string MtuOutOfRange = "MTU_OUT_OF_RANGE";                // REQ-VAL-032
    public const string EndpointNotIP = "ENDPOINT_NOT_IP";                 // REQ-VAL-033
    public const string PeerInterfacesIgnored = "ALLOWED_PEER_INTERFACES_IGNORED"; // REQ-VAL-034
    public const string ExternalWithoutNat = "EXTERNAL_WITHOUT_NAT";        // REQ-VAL-035
}

namespace WgAgent.Core;

/// <summary>
/// The reason codes of SPEC-04 section 7.1 that this build produces. The set is closed
/// (REQ-API-041); a code joins here with the requirement that produces it.
/// </summary>
public static class ReasonCodes
{
    public const string InterfaceNameInvalid = "INTERFACE_NAME_INVALID";
    public const string InterfaceExists = "INTERFACE_EXISTS";
    public const string KeyInvalid = "KEY_INVALID";
    public const string PublicKeyInvalid = "PUBLIC_KEY_INVALID";
    public const string AddressesRequired = "ADDRESSES_REQUIRED";
    public const string AllowedIpsRequired = "ALLOWED_IPS_REQUIRED";
    public const string AllowedIpsDuplicate = "ALLOWED_IPS_DUPLICATE";
    public const string AllowedIpsNotCanonical = "ALLOWED_IPS_NOT_CANONICAL";
    public const string AllowedIpsDefaultRoute = "ALLOWED_IPS_DEFAULT_ROUTE";
    public const string Ipv6NotSupported = "IPV6_NOT_SUPPORTED";
    public const string CidrInvalid = "CIDR_INVALID";
    public const string ListenPortInvalid = "LISTEN_PORT_INVALID";
    public const string ListenPortInUse = "LISTEN_PORT_IN_USE";
    public const string AddressConflict = "ADDRESS_CONFLICT";
    public const string KeepaliveInvalid = "KEEPALIVE_INVALID";
    public const string MtuInvalid = "MTU_INVALID";
    public const string EndpointInvalid = "ENDPOINT_INVALID";
    public const string PeerIsInterface = "PEER_IS_INTERFACE";
    public const string ClientAddressMissing = "CLIENT_ADDRESS_MISSING";
    public const string SubnetFull = "SUBNET_FULL";
    public const string HookInvalid = "HOOK_INVALID";
    public const string StoreCorrupt = "STORE_CORRUPT";
    public const string StoreSchemaTooNew = "STORE_SCHEMA_TOO_NEW";
    public const string StoreBusy = "STORE_BUSY";
    public const string ApplyFailed = "APPLY_FAILED";
    public const string InterfaceNotManaged = "INTERFACE_NOT_MANAGED";
    public const string PeerNotFound = "PEER_NOT_FOUND";
    public const string PeerExists = "PEER_EXISTS";
    public const string EndpointRequired = "ENDPOINT_REQUIRED";
    public const string FieldUnknown = "FIELD_UNKNOWN";
    public const string RequestMalformed = "REQUEST_MALFORMED";
    public const string FieldImmutable = "FIELD_IMMUTABLE";
    public const string TokenInvalid = "TOKEN_INVALID";

    // Startup codes: they end the process before it serves, so they reach a log, not a response.
    public const string MissingCapNetAdmin = "MISSING_CAP_NET_ADMIN";

    // Warnings, REQ-VAL-002.
    public const string AllowedIpsOverlap = "ALLOWED_IPS_OVERLAP";
    public const string AllowedIpsOutOfSubnet = "ALLOWED_IPS_OUT_OF_SUBNET";
    public const string MtuOutOfRange = "MTU_OUT_OF_RANGE";
    public const string EndpointNotIp = "ENDPOINT_NOT_IP";
}

using Microsoft.AspNetCore.Http;
using WgAgent.Core;

namespace WgAgent.Api;

/// <summary>
/// The error model of SPEC-04 section 7: an <see cref="AgentException"/> becomes an
/// <c>application/problem+json</c> body carrying its reason code (REQ-API-082), at the HTTP status
/// the table of REQ-API-083 assigns the code.
/// </summary>
public static class Problems
{
    private static readonly Dictionary<string, int> Status = Build();

    /// <summary>The HTTP status REQ-API-083 assigns a reason code.</summary>
    public static int StatusOf(string reason) => Status.TryGetValue(reason, out var code) ? code : StatusCodes.Status500InternalServerError;

    /// <summary>The problem result for a refused operation.</summary>
    public static IResult Result(string reason, string detail)
    {
        var status = StatusOf(reason);
        var problem = new Problem { Title = Title(status), Status = status, Detail = detail, Reason = reason };
        return Microsoft.AspNetCore.Http.Results.Json(problem, ApiJsonContext.Default.Problem, "application/problem+json", status);
    }

    public static IResult Result(AgentException error) => Result(error.Code, error.Message);

    private static string Title(int status) => status switch
    {
        400 => "Bad Request",
        401 => "Unauthorized",
        404 => "Not Found",
        409 => "Conflict",
        412 => "Precondition Failed",
        503 => "Service Unavailable",
        _ => "Internal Server Error",
    };

    private static Dictionary<string, int> Build()
    {
        var map = new Dictionary<string, int>();
        void Add(int status, params string[] codes) { foreach (var c in codes) map[c] = status; }

        Add(400, ReasonCodes.InterfaceNameInvalid, ReasonCodes.PublicKeyInvalid, ReasonCodes.KeyInvalid,
            ReasonCodes.AddressesRequired, ReasonCodes.AllowedIpsRequired, ReasonCodes.AllowedIpsDuplicate,
            ReasonCodes.AllowedIpsNotCanonical, ReasonCodes.Ipv6NotSupported, ReasonCodes.ListenPortInvalid,
            ReasonCodes.KeepaliveInvalid, ReasonCodes.MtuInvalid, ReasonCodes.EndpointInvalid,
            ReasonCodes.EndpointRequired, ReasonCodes.PeerIsInterface, ReasonCodes.ClientAddressMissing,
            ReasonCodes.AllowedIpsDefaultRoute, ReasonCodes.HookInvalid, ReasonCodes.CidrInvalid,
            ReasonCodes.FieldImmutable, ReasonCodes.FieldUnknown, ReasonCodes.RequestMalformed);
        Add(401, ReasonCodes.TokenInvalid);
        Add(404, ReasonCodes.InterfaceNotManaged, ReasonCodes.PeerNotFound);
        Add(409, ReasonCodes.InterfaceExists, ReasonCodes.PeerExists, ReasonCodes.SubnetFull,
            ReasonCodes.ListenPortInUse, ReasonCodes.AddressConflict);
        Add(500, ReasonCodes.ApplyFailed);
        Add(503, ReasonCodes.StoreBusy);
        return map;
    }
}

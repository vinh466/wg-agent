using Microsoft.AspNetCore.Http;
using WgAgent.Core;
using WgAgent.Platform.Linux;

namespace WgAgent.Api;

/// <summary>
/// The bearer check of ADR-0015: every request but the health endpoint carries the token
/// (REQ-SEC-071, REQ-SEC-080), compared in constant time (REQ-SEC-073). The token file is read on
/// each request, so a replacement takes effect at once and the one it replaced is refused
/// (REQ-SEC-085); a missing and a wrong token are refused alike (REQ-SEC-078).
/// </summary>
internal static class Authentication
{
    private const string HealthPath = "/v1/health";
    private const string Scheme = "Bearer ";

    public static async Task Apply(HttpContext context, TokenFile tokenFile, RequestDelegate next)
    {
        if (HttpMethods.IsGet(context.Request.Method) && context.Request.Path.Equals(HealthPath, StringComparison.Ordinal))
        {
            await next(context);
            return;
        }

        if (!Authenticated(context, tokenFile))
        {
            await Problems.Result(ReasonCodes.TokenInvalid, "A valid bearer token is required.").ExecuteAsync(context);
            return;
        }

        await next(context);
    }

    private static bool Authenticated(HttpContext context, TokenFile tokenFile)
    {
        Platform.Secret? token;
        try
        {
            token = tokenFile.Read();
        }
        catch (TokenFile.Invalid)
        {
            return false;   // an untrustworthy token file authenticates no one
        }
        if (token is null) return false;

        var header = context.Request.Headers.Authorization.ToString();
        return header.StartsWith(Scheme, StringComparison.Ordinal) && token.Matches(header[Scheme.Length..]);
    }
}

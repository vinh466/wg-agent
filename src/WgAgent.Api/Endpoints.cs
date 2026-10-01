using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using WgAgent.Core;
using WgAgent.Platform;
using WgAgent.Service;

namespace WgAgent.Api;

/// <summary>
/// The twelve routes of REQ-API-063, each calling the service the CLI shares (REQ-CLI-023). Handlers
/// are plain <see cref="RequestDelegate"/>s, so routing needs no reflection over their parameters and
/// the listener publishes with no AOT warning (ADR-0014).
/// </summary>
internal static class Endpoints
{
    public static void Map(IEndpointRouteBuilder v1, AgentService service, TimeProvider clock, DateTimeOffset startedAt, ILogger logger)
    {
        v1.MapPost("/interfaces", Handle(logger, async c =>
        {
            var body = await Bodies.Read(c, ApiJsonContext.Default.CreateInterfaceRequest);
            if (body.Name is null) throw new AgentException(ReasonCodes.RequestMalformed, "name is required.");
            var result = service.CreateInterface(body.Name, body.ToSpec());
            return Created($"/v1/interfaces/{body.Name}", new InterfaceWriteResult { Interface = ApiInterface.From(result.Resource), Restarted = result.Restarted }, ApiJsonContext.Default.InterfaceWriteResult);
        }));

        v1.MapGet("/interfaces", Handle(logger, _ => Task.FromResult(
            Ok(service.ListInterfaces().Select(ApiInterface.From).ToList() as IReadOnlyList<ApiInterface>, ApiJsonContext.Default.IReadOnlyListApiInterface))));

        v1.MapGet("/interfaces/{name}", Handle(logger, c => Task.FromResult(
            Ok(ApiInterface.From(service.GetInterface(Name(c))), ApiJsonContext.Default.ApiInterface))));

        v1.MapPut("/interfaces/{name}", Handle(logger, async c =>
        {
            var name = Name(c);
            var body = await Bodies.Read(c, ApiJsonContext.Default.UpdateInterfaceRequest);
            if (body.Name is not null && body.Name != name)
                throw new AgentException(ReasonCodes.FieldImmutable, $"The body names '{body.Name}', but the path names '{name}'; name is immutable.");
            var result = service.UpdateInterface(name, body.ToSpec(), keepHooks: true);   // REQ-API-085
            return Ok(new InterfaceWriteResult { Interface = ApiInterface.From(result.Resource), Restarted = result.Restarted }, ApiJsonContext.Default.InterfaceWriteResult);
        }));

        v1.MapDelete("/interfaces/{name}", Handle(logger, c =>
        {
            service.DeleteInterface(Name(c));
            return Task.FromResult(Results.NoContent());
        }));

        v1.MapPost("/interfaces/{name}/peers", Handle(logger, async c =>
        {
            var name = Name(c);
            var body = await Bodies.Read(c, ApiJsonContext.Default.CreatePeerRequest);
            var result = service.CreatePeer(name, body.ToServiceRequest());
            var dto = new CreatePeerResult
            {
                Peer = ApiPeer.From(result.Peer),
                Restarted = result.Restarted,
                PrivateKey = result.GeneratedPrivateKey?.Reveal(),      // REQ-KEY-011
                PresharedKey = result.GeneratedPresharedKey?.Reveal(),  // REQ-KEY-021
                ClientConfiguration = result.ClientConfiguration,       // REQ-KEY-042
            };
            return Created($"/v1/interfaces/{name}/peers/{FromStandard(result.Peer.PublicKey)}", dto, ApiJsonContext.Default.CreatePeerResult);
        }));

        v1.MapGet("/interfaces/{name}/peers", Handle(logger, c => Task.FromResult(
            Ok(service.ListPeers(Name(c)).Select(ApiPeer.From).ToList() as IReadOnlyList<ApiPeer>, ApiJsonContext.Default.IReadOnlyListApiPeer))));

        v1.MapGet("/interfaces/{name}/peers/{public_key}", Handle(logger, c => Task.FromResult(
            Ok(ApiPeer.From(service.GetPeer(Name(c), Key(c))), ApiJsonContext.Default.ApiPeer))));

        v1.MapPut("/interfaces/{name}/peers/{public_key}", Handle(logger, async c =>
        {
            var name = Name(c);
            var key = Key(c);
            var body = await Bodies.Read(c, ApiJsonContext.Default.UpdatePeerRequest);
            if (body.PublicKey is not null && body.PublicKey != key)
                throw new AgentException(ReasonCodes.FieldImmutable, "The body's public_key differs from the path; the public key is immutable.");
            var result = service.UpdatePeer(name, key, body.ToSpec());
            return Ok(new PeerWriteResult { Peer = ApiPeer.From(result.Resource), Restarted = result.Restarted }, ApiJsonContext.Default.PeerWriteResult);
        }));

        v1.MapDelete("/interfaces/{name}/peers/{public_key}", Handle(logger, c =>
        {
            service.DeletePeer(Name(c), Key(c));
            return Task.FromResult(Results.NoContent());
        }));

        v1.MapGet("/version", Handle(logger, _ =>
        {
            var (version, commit) = BuildInfo.Current();
            return Task.FromResult(Ok(new VersionResponse
            {
                Version = version,
                Commit = commit,
                StartedAt = startedAt,
                UptimeSeconds = (long)(clock.GetUtcNow() - startedAt).TotalSeconds,
            }, ApiJsonContext.Default.VersionResponse));
        }));

        // REQ-API-051, unauthenticated (REQ-SEC-080).
        v1.MapGet("/health", (RequestDelegate)(context =>
            Results.Json(new HealthResponse { Status = "ok" }, ApiJsonContext.Default.HealthResponse).ExecuteAsync(context)));
    }

    /// <summary>Wraps a handler so a refused operation becomes its problem document (REQ-API-082).</summary>
    private static RequestDelegate Handle(ILogger logger, Func<HttpContext, Task<IResult>> body) => async context =>
    {
        IResult result;
        try { result = await body(context); }
        catch (AgentException error)
        {
            logger.LogWarning(error, "request refused {reason}", error.Code);   // REQ-OBS-011: reason and error
            result = Problems.Result(error);
        }
        await result.ExecuteAsync(context);
    };

    private static string Name(HttpContext c) => (string)c.Request.RouteValues["name"]!;

    /// <summary>The peer key as standard base64, converted from the path's unpadded base64url (REQ-API-081).</summary>
    private static string Key(HttpContext c)
    {
        var segment = (string)c.Request.RouteValues["public_key"]!;
        return PublicKey.TryParsePathSegment(segment, out var key) ? key.ToString()
            : throw new AgentException(ReasonCodes.PublicKeyInvalid, $"'{segment}' is not a public key in unpadded base64url.");
    }

    private static IResult Ok<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info) =>
        Results.Json(value, info, statusCode: StatusCodes.Status200OK);

    private static IResult Created<T>(string location, T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info) =>
        new WithLocation(Results.Json(value, info, statusCode: StatusCodes.Status201Created), location);

    private static string FromStandard(string standard) =>
        PublicKey.TryParse(standard, out var key) ? key.ToPathSegment() : standard;

    private sealed class WithLocation(IResult inner, string location) : IResult
    {
        public Task ExecuteAsync(HttpContext context)
        {
            context.Response.Headers.Location = location;
            return inner.ExecuteAsync(context);
        }
    }
}

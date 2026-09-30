using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using WgAgent.Core.Store;
using WgAgent.Platform.Linux;
using WgAgent.Service;

namespace WgAgent.Api;

/// <summary>What <c>serve</c> needs to run the listener.</summary>
public sealed record ServerOptions
{
    public required AgentService Service { get; init; }
    public required StateStore Store { get; init; }
    public required TokenFile Token { get; init; }
    public required string ListenAddress { get; init; }
    public Func<bool> HasNetAdmin { get; init; } = Capabilities.HasNetAdmin;
    public TimeProvider Clock { get; init; } = TimeProvider.System;
}

/// <summary>
/// The REST listener of ADR-0014: Minimal APIs with source-generated JSON. The startup checks of
/// REQ-API-050 run before it serves; a failure ends the process with the check's reason code.
/// </summary>
public static class Server
{
    /// <summary>Runs the checks then the listener; returns a non-zero exit code on a failed check.</summary>
    public static int Run(ServerOptions options, TextWriter error)
    {
        if (StartupChecks.Run(options.Store, options.Token, options.HasNetAdmin) is { } failure)
        {
            error.WriteLine($"wg-agent: {failure.Reason}: {failure.Message}");
            return 1;
        }
        Build(options).Run();   // blocks until SIGTERM; graceful shutdown drains in flight (REQ-API-073)
        return 0;
    }

    /// <summary>Builds the app without running it — for the contract test and in-process integration.</summary>
    public static WebApplication Build(ServerOptions options)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.ConfigureHttpJsonOptions(json =>
            json.SerializerOptions.TypeInfoResolverChain.Insert(0, ApiJsonContext.Default));

        var app = builder.Build();
        app.Urls.Add($"http://{options.ListenAddress}");
        app.Use((context, next) => Authentication.Apply(context, options.Token, next));   // REQ-SEC-071
        Endpoints.Map(app.MapGroup("/v1"), options.Service, options.Clock, options.Clock.GetUtcNow());
        return app;
    }
}

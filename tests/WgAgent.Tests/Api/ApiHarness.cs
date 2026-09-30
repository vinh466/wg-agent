using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using WgAgent.Api;
using WgAgent.Core.Store;
using WgAgent.Platform;
using WgAgent.Platform.Linux;
using WgAgent.Service;
using WgAgent.Testing;

namespace WgAgent.Tests.Api;

/// <summary>
/// The API served in-process over loopback Kestrel on an ephemeral port, against the in-memory host.
/// Real HTTP, no privilege: the startup checks of Server.Run are not on this path (Build alone).
/// </summary>
public sealed class ApiHarness : IAsyncDisposable
{
    public const string Token = "the-secret-token-value";
    public FakeHost Host { get; } = new();
    public AgentService Service { get; }
    public TokenFile TokenFile { get; }
    public ManualClock Clock { get; } = new(DateTimeOffset.Parse("2026-10-01T12:00:00Z"));

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wg-agent-api-" + Guid.NewGuid().ToString("N"));
    private readonly WebApplication _app;
    private readonly HttpClient _client = new();

    public ApiHarness()
    {
        Directory.CreateDirectory(_dir);
        Service = new AgentService(
            new StateStore(Path.Combine(_dir, "state.json")),
            new HostPorts(Host, Host, Host, Host, new ApplyDeadline()),
            new AgentOptions { NodeEndpoint = "vpn.example.net" }, Clock);
        TokenFile = new TokenFile(Path.Combine(_dir, "token"));
        TokenFile.Write(Secret.From(Token));

        _app = Server.Build(new ServerOptions
        {
            Service = Service, Store = new StateStore(Path.Combine(_dir, "state.json")),
            Token = TokenFile, ListenAddress = "127.0.0.1:0", Clock = Clock,
        });
        _app.StartAsync().GetAwaiter().GetResult();
        var address = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        _client.BaseAddress = new Uri(address);
        _client.DefaultRequestHeaders.Add("Authorization", "Bearer " + Token);
    }

    public HttpClient Client => _client;

    /// <summary>A client carrying a different (or no) token.</summary>
    public HttpClient ClientWith(string? token)
    {
        var client = new HttpClient { BaseAddress = _client.BaseAddress };
        if (token is not null) client.DefaultRequestHeaders.Add("Authorization", "Bearer " + token);
        return client;
    }

    public async Task<(System.Net.HttpStatusCode Code, JsonDocument Body)> Send(HttpMethod method, string path, string? json = null, HttpClient? client = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (json is not null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await (client ?? _client).SendAsync(request, TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var body = text.Length == 0 ? JsonDocument.Parse("null") : JsonDocument.Parse(text);
        return (response.StatusCode, body);
    }

    public Task<(System.Net.HttpStatusCode, JsonDocument)> Get(string path, HttpClient? client = null) => Send(HttpMethod.Get, path, null, client);
    public Task<(System.Net.HttpStatusCode, JsonDocument)> Post(string path, string json, HttpClient? client = null) => Send(HttpMethod.Post, path, json, client);
    public Task<(System.Net.HttpStatusCode, JsonDocument)> Put(string path, string json, HttpClient? client = null) => Send(HttpMethod.Put, path, json, client);
    public Task<(System.Net.HttpStatusCode, JsonDocument)> Delete(string path, HttpClient? client = null) => Send(HttpMethod.Delete, path, null, client);

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}

using System.Net;
using System.Text.Json;
using WgAgent.Core.Model;
using WgAgent.Platform;

namespace WgAgent.Tests.Api;

public sealed class ApiTests
{
    private static readonly string PeerA = TestKeys.Base64(0x0A);
    private static string PathSegment(string standardBase64) =>
        PublicKey.TryParse(standardBase64, out var k) ? k.ToPathSegment() : standardBase64;

    private static string Reason(JsonDocument body) => body.RootElement.GetProperty("reason").GetString()!;

    // ---- Authentication (SPEC-05 section 4)

    [Fact]
    public async Task EveryRequestButHealth_NeedsTheToken_REQ_SEC_071()
    {
        await using var api = new ApiHarness();
        var (code, body) = await api.Get("/v1/interfaces", api.ClientWith(null));
        Assert.Equal(HttpStatusCode.Unauthorized, code);
        Assert.Equal("TOKEN_INVALID", Reason(body));

        var (ok, _) = await api.Get("/v1/interfaces");
        Assert.Equal(HttpStatusCode.OK, ok);
    }

    [Fact]
    public async Task MissingAndWrongToken_AreRefusedAlike_REQ_SEC_078()
    {
        await using var api = new ApiHarness();
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.Get("/v1/interfaces", api.ClientWith(null))).Item1);
        var (code, body) = await api.Get("/v1/interfaces", api.ClientWith("wrong-token"));
        Assert.Equal(HttpStatusCode.Unauthorized, code);
        Assert.Equal("TOKEN_INVALID", Reason(body));
    }

    [Fact]
    public async Task AnEmptyTokenFile_AuthenticatesNoOne_REQ_SEC_072()
    {
        await using var api = new ApiHarness();
        Assert.Equal(HttpStatusCode.OK, (await api.Get("/v1/interfaces")).Item1);

        File.WriteAllText(api.TokenFile.Path, "\n");   // the token is gone at runtime

        Assert.Equal(HttpStatusCode.Unauthorized, (await api.Get("/v1/interfaces")).Item1);
    }

    [Fact]
    public async Task Health_IsServedWithoutAToken_REQ_SEC_080()
    {
        await using var api = new ApiHarness();
        var (code, body) = await api.Get("/v1/health", api.ClientWith(null));
        Assert.Equal(HttpStatusCode.OK, code);
        Assert.Equal("ok", body.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task ReplacedToken_IsHonouredAtOnce_REQ_SEC_085()
    {
        await using var api = new ApiHarness();
        Assert.Equal(HttpStatusCode.OK, (await api.Get("/v1/interfaces")).Item1);   // old token works

        api.TokenFile.Write(Secret.From("a-new-token"));

        Assert.Equal(HttpStatusCode.Unauthorized, (await api.Get("/v1/interfaces")).Item1);                    // the old is refused
        Assert.Equal(HttpStatusCode.OK, (await api.Get("/v1/interfaces", api.ClientWith("a-new-token"))).Item1); // the new works
    }

    // ---- The surface (SPEC-04)

    private const string Wg0 = """{"name":"wg0","addresses":["10.8.0.1/24"]}""";

    [Fact]
    public async Task CreateGetListUpdateDelete_AnInterface_REQ_API_063()
    {
        await using var api = new ApiHarness();

        var (created, createdBody) = await api.Post("/v1/interfaces", Wg0);
        Assert.Equal(HttpStatusCode.Created, created);
        Assert.Equal("wg0", createdBody.RootElement.GetProperty("interface").GetProperty("name").GetString());
        Assert.False(createdBody.RootElement.GetProperty("restarted").GetBoolean());

        var (got, gotBody) = await api.Get("/v1/interfaces/wg0");
        Assert.Equal(HttpStatusCode.OK, got);
        Assert.Equal("UP", gotBody.RootElement.GetProperty("status").GetProperty("oper_state").GetString());

        var (listed, listBody) = await api.Get("/v1/interfaces");
        Assert.Equal(1, listBody.RootElement.GetArrayLength());

        var (updated, _) = await api.Put("/v1/interfaces/wg0", """{"addresses":["10.8.0.1/24"],"mtu":1380}""");
        Assert.Equal(HttpStatusCode.OK, updated);
        Assert.Equal(1380u, api.Service.GetInterface("wg0").Spec!.Mtu);

        Assert.Equal(HttpStatusCode.NoContent, (await api.Delete("/v1/interfaces/wg0")).Item1);
        Assert.Equal(HttpStatusCode.NotFound, (await api.Get("/v1/interfaces/wg0")).Item1);
    }

    [Fact]
    public async Task CreatePeer_Generated_ReturnsTheKeyAndClientConfigOnce_REQ_KEY_042()
    {
        await using var api = new ApiHarness();
        await api.Post("/v1/interfaces", Wg0);

        var (code, body) = await api.Post("/v1/interfaces/wg0/peers", """{"generate_keypair":true}""");

        Assert.Equal(HttpStatusCode.Created, code);
        var privateKey = body.RootElement.GetProperty("private_key").GetString()!;
        Assert.Equal(44, privateKey.Length);
        Assert.Contains(privateKey, body.RootElement.GetProperty("client_configuration").GetString());
        Assert.Contains("vpn.example.net:51820", body.RootElement.GetProperty("client_configuration").GetString());
    }

    [Fact]
    public async Task PeerPath_IsUnpaddedBase64Url_REQ_API_081()
    {
        await using var api = new ApiHarness();
        await api.Post("/v1/interfaces", Wg0);
        await api.Post("/v1/interfaces/wg0/peers", $$"""{"public_key":"{{PeerA}}","allowed_ips":["10.8.0.2/32"]}""");

        var (code, body) = await api.Get($"/v1/interfaces/wg0/peers/{PathSegment(PeerA)}");
        Assert.Equal(HttpStatusCode.OK, code);
        Assert.Equal(PeerA, body.RootElement.GetProperty("public_key").GetString());

        var (bad, badBody) = await api.Get("/v1/interfaces/wg0/peers/not-a-key");
        Assert.Equal(HttpStatusCode.BadRequest, bad);
        Assert.Equal("PUBLIC_KEY_INVALID", Reason(badBody));
    }

    // ---- The hooks stay out of the API (ADR-0017)

    [Fact]
    public async Task Response_NeverCarriesTheHooks_REQ_API_084()
    {
        await using var api = new ApiHarness();
        api.Service.CreateInterface("wg0", new InterfaceSpec { Addresses = ["10.8.0.1/24"], PostUp = ["iptables -A FORWARD -j ACCEPT"] });

        var (_, body) = await api.Get("/v1/interfaces/wg0");
        var spec = body.RootElement.GetProperty("spec");
        Assert.False(spec.TryGetProperty("post_up", out _));
        Assert.False(spec.TryGetProperty("post_down", out _));
    }

    [Fact]
    public async Task Write_LeavesTheHooksAsTheCliSetThem_REQ_API_085()
    {
        await using var api = new ApiHarness();
        api.Service.CreateInterface("wg0", new InterfaceSpec { Addresses = ["10.8.0.1/24"], PostUp = ["iptables -A FORWARD -j ACCEPT"] });

        Assert.Equal(HttpStatusCode.OK, (await api.Put("/v1/interfaces/wg0", """{"addresses":["10.8.0.1/24"],"mtu":1380}""")).Item1);

        Assert.Equal(["iptables -A FORWARD -j ACCEPT"], api.Service.GetInterface("wg0").Spec!.PostUp);
    }

    // ---- The error model (SPEC-04 section 7)

    [Fact]
    public async Task Error_IsAProblemDocumentWithReasonAndStatus_REQ_API_082()
    {
        await using var api = new ApiHarness();

        var response = await api.Client.GetAsync("/v1/interfaces/absent", TestContext.Current.CancellationToken);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("INTERFACE_NOT_MANAGED", body.RootElement.GetProperty("reason").GetString());
        Assert.Equal(404, body.RootElement.GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task Status_MatchesTheReasonCode_REQ_API_083()
    {
        await using var api = new ApiHarness();
        await api.Post("/v1/interfaces", Wg0);

        Assert.Equal(HttpStatusCode.Conflict, (await api.Post("/v1/interfaces", Wg0)).Item1);            // INTERFACE_EXISTS 409
        Assert.Equal(HttpStatusCode.BadRequest, (await api.Post("/v1/interfaces", """{"name":"wg1","addresses":["fd00::1/64"]}""")).Item1);  // IPV6 400
        Assert.Equal(HttpStatusCode.NotFound, (await api.Get("/v1/interfaces/none/peers/" + PathSegment(PeerA))).Item1);  // INTERFACE_NOT_MANAGED 404
    }

    [Fact]
    public async Task UnknownMember_IsFieldUnknown_REQ_VAL_050()
    {
        await using var api = new ApiHarness();
        var (code, body) = await api.Post("/v1/interfaces", """{"name":"wg0","addresses":["10.8.0.1/24"],"enable":false}""");
        Assert.Equal(HttpStatusCode.BadRequest, code);
        Assert.Equal("FIELD_UNKNOWN", Reason(body));
    }

    [Fact]
    public async Task MalformedBody_IsRequestMalformed_REQ_API_086()
    {
        await using var api = new ApiHarness();
        Assert.Equal("REQUEST_MALFORMED", Reason((await api.Post("/v1/interfaces", "{not json")).Item2));
        Assert.Equal("REQUEST_MALFORMED", Reason((await api.Post("/v1/interfaces", """{"name":"wg0","addresses":"10.8.0.1/24"}""")).Item2));  // wrong type
    }

    [Fact]
    public async Task BodyNameDifferingFromPath_IsFieldImmutable_REQ_API_066()
    {
        await using var api = new ApiHarness();
        await api.Post("/v1/interfaces", Wg0);
        var (code, body) = await api.Put("/v1/interfaces/wg0", """{"name":"wg9","addresses":["10.8.0.1/24"]}""");
        Assert.Equal(HttpStatusCode.BadRequest, code);
        Assert.Equal("FIELD_IMMUTABLE", Reason(body));
    }

    [Fact]
    public async Task OmittedEnabled_TakesItsDefault_REQ_API_076()
    {
        await using var api = new ApiHarness();
        await api.Post("/v1/interfaces", Wg0);                       // enabled omitted → true
        Assert.Contains("wg0", api.Host.Active);

        await api.Post("/v1/interfaces", """{"name":"wg1","addresses":["10.9.0.1/24"],"listen_port":51821,"enabled":false}""");
        Assert.DoesNotContain("wg1", api.Host.Active);              // explicit false → stopped
    }

    [Fact]
    public async Task NoResponse_CarriesAWriteOnlyKey_REQ_RES_013()
    {
        await using var api = new ApiHarness();
        var secret = TestKeys.Base64(0x17);
        var texts = new List<string>
        {
            (await api.Post("/v1/interfaces", $$"""{"name":"wg0","private_key":"{{secret}}","addresses":["10.8.0.1/24"]}""")).Item2.RootElement.GetRawText(),
            (await api.Get("/v1/interfaces/wg0")).Item2.RootElement.GetRawText(),
            (await api.Get("/v1/interfaces")).Item2.RootElement.GetRawText(),
        };
        var psk = TestKeys.Base64(0x18);
        texts.Add((await api.Post("/v1/interfaces/wg0/peers", $$"""{"public_key":"{{PeerA}}","preshared_key":"{{psk}}","allowed_ips":["10.8.0.2/32"]}""")).Item2.RootElement.GetRawText());
        texts.Add((await api.Get($"/v1/interfaces/wg0/peers/{PathSegment(PeerA)}")).Item2.RootElement.GetRawText());
        foreach (var text in texts)
        {
            Assert.DoesNotContain(secret, text);
            Assert.DoesNotContain(psk, text);
            Assert.DoesNotContain("private_key", text);
            Assert.DoesNotContain("preshared_key", text);
        }
    }

    [Fact]
    public async Task Version_ReportsVersionCommitAndUptime_REQ_API_078()
    {
        await using var api = new ApiHarness();
        api.Clock.Now = api.Clock.Now.AddSeconds(42);
        var (code, body) = await api.Get("/v1/version");
        Assert.Equal(HttpStatusCode.OK, code);
        foreach (var field in new[] { "version", "commit", "started_at", "uptime_seconds" })
            Assert.True(body.RootElement.TryGetProperty(field, out _), field);
        Assert.Equal(42, body.RootElement.GetProperty("uptime_seconds").GetInt64());
    }
}

using System.Text.Json;
using WgAgent.Core.Json;
using WgAgent.Core.Model;
using WgAgent.Core.Validation;
using WgAgent.Platform;

namespace WgAgent.Tests.Model;

public class ResourceModelTests
{
    private static JsonElement Json<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info) =>
        JsonDocument.Parse(JsonSerializer.Serialize(value, info)).RootElement;

    private static readonly InterfaceResource Interface = new()
    {
        Name = "wg0",
        Spec = Defaults.Apply(new InterfaceSpec { Addresses = ["10.8.0.1/24"] }),
        Status = new InterfaceStatus { PublicKey = TestKeys.Base64(1), OperState = OperState.Up },
    };

    private static readonly PeerResource Peer = new()
    {
        InterfaceName = "wg0",
        PublicKey = TestKeys.Base64(2),
        Spec = Defaults.Apply(new PeerSpec { AllowedIps = ["10.8.0.2/32"] }),
        Status = new PeerStatus(),
    };

    [Fact]
    public void Resource_SeparatesSpecFromStatus_REQ_RES_001()
    {
        var json = Json(Interface, CoreJsonContext.Default.InterfaceResource);
        Assert.Equal(JsonValueKind.Object, json.GetProperty("spec").ValueKind);
        Assert.Equal(JsonValueKind.Object, json.GetProperty("status").ValueKind);
        Assert.False(json.GetProperty("spec").TryGetProperty("oper_state", out _));
        Assert.False(json.GetProperty("status").TryGetProperty("addresses", out _));
    }

    [Fact]
    public void Resources_CarryIdentityOutsideSpecAndStatus_REQ_RES_034()
    {
        var iface = Json(Interface, CoreJsonContext.Default.InterfaceResource);
        Assert.Equal("wg0", iface.GetProperty("name").GetString());
        Assert.False(iface.GetProperty("spec").TryGetProperty("name", out _));
        Assert.False(iface.GetProperty("status").TryGetProperty("name", out _));

        var peer = Json(Peer, CoreJsonContext.Default.PeerResource);
        Assert.Equal("wg0", peer.GetProperty("interface_name").GetString());
        Assert.Equal(TestKeys.Base64(2), peer.GetProperty("public_key").GetString());
        Assert.False(peer.GetProperty("spec").TryGetProperty("public_key", out _));
        Assert.False(peer.GetProperty("spec").TryGetProperty("interface_name", out _));
    }

    [Fact]
    public void InterfaceSpec_HasNoPeerList_REQ_RES_030()
    {
        foreach (var property in typeof(InterfaceSpec).GetProperties())
        {
            var type = property.PropertyType;
            var mentionsPeers = type == typeof(PeerSpec) || type == typeof(PeerResource)
                || type.GetGenericArguments().Any(a => a == typeof(PeerSpec) || a == typeof(PeerResource));
            Assert.False(mentionsPeers, $"InterfaceSpec.{property.Name} carries peers.");
        }
    }

    [Fact]
    public void Ipv6_IsRejectedExplicitly_REQ_RES_004()
    {
        var spec = Defaults.Apply(new InterfaceSpec { Addresses = ["fd00::1/64"] });
        var result = InterfaceRules.Check("wg0", spec, creating: true, new HostView(), new Dictionary<string, InterfaceSpec>());
        Assert.Equal("IPV6_NOT_SUPPORTED", result.Error?.Code);
    }

    [Theory]
    [InlineData("wg0", true)]
    [InlineData("a", true)]
    [InlineData("office-vpn_2", true)]
    [InlineData("abcdefghijklmno", true)]
    [InlineData("abcdefghijklmnop", false)]
    [InlineData("0wg", false)]
    [InlineData("wg.0", false)]
    [InlineData("wg 0", false)]
    [InlineData("", false)]
    public void InterfaceName_MatchesThePattern_REQ_RES_011(string name, bool valid) =>
        Assert.Equal(valid, InterfaceRules.IsValidName(name));

    [Fact]
    public void PublicKey_InPathIsUnpaddedBase64Url_REQ_RES_021()
    {
        // 0xFB and 0xFF bytes produce '+' and '/' in standard base64.
        var key = PublicKey.FromBytes(Enumerable.Range(0, 32).Select(i => (byte)(i % 2 == 0 ? 0xFB : 0xFF)).ToArray());
        var segment = key.ToPathSegment();
        Assert.Equal(43, segment.Length);
        Assert.DoesNotContain('=', segment);
        Assert.DoesNotContain('+', segment);
        Assert.DoesNotContain('/', segment);
        Assert.True(PublicKey.TryParsePathSegment(segment, out var back));
        Assert.Equal(key, back);
        Assert.False(PublicKey.TryParsePathSegment(key.ToString(), out _));
    }

    [Fact]
    public void KeyFields_AreStandardPaddedBase64_REQ_RES_027()
    {
        var text = TestKeys.Base64(7);
        Assert.True(PublicKey.TryParse(text, out var key));
        Assert.Equal(text, key.ToString());
        Assert.Equal(44, text.Length);
        Assert.EndsWith("=", text);
        Assert.False(PublicKey.TryParse(text.TrimEnd('='), out _));
        Assert.False(PublicKey.TryParse(key.ToPathSegment(), out _));
        Assert.Equal(text, SecretKey.Parse(text).Reveal());
    }

    [Fact]
    public void Online_IsAFreshHandshake_REQ_RES_025()
    {
        var now = DateTimeOffset.UnixEpoch.AddDays(1);
        var threshold = TimeSpan.FromSeconds(180);
        Assert.False(PeerStatus.IsOnline(null, now, threshold));
        Assert.True(PeerStatus.IsOnline(now.AddSeconds(-179), now, threshold));
        Assert.False(PeerStatus.IsOnline(now.AddSeconds(-180), now, threshold));
        Assert.False(PeerStatus.IsOnline(now.AddHours(-1), now, threshold));
    }

    [Fact]
    public void Labels_AreNotInterpreted_REQ_RES_014()
    {
        var labels = new Dictionary<string, string> { ["owner"] = "alice", ["weird key\n"] = "{\"json\": [1]}", [""] = "" };
        var spec = Defaults.Apply(new InterfaceSpec { Addresses = ["10.8.0.1/24"], Labels = labels });
        var result = InterfaceRules.Check("wg0", spec, creating: true, new HostView(), new Dictionary<string, InterfaceSpec>());
        Assert.True(result.IsValid);
        Assert.Empty(result.Warnings);
        Assert.Same(labels, spec.Labels);
    }

    [Fact]
    public void SecretKey_TextAndJsonAreRedacted_REQ_SEC_050()
    {
        var secret = TestKeys.Secret(9);
        Assert.Equal("[REDACTED]", secret.ToString());
        Assert.Equal("key=[REDACTED]", $"key={secret}");

        var spec = new InterfaceSpec { PrivateKey = secret, Addresses = ["10.8.0.1/24"] };
        var json = JsonSerializer.Serialize(spec, CoreJsonContext.Default.InterfaceSpec);
        Assert.Contains("\"[REDACTED]\"", json);
        Assert.DoesNotContain(TestKeys.Base64(9), json);

        Assert.Equal(TestKeys.Base64(9), secret.Reveal());
    }

    [Fact]
    public void Spec_TellsAnAbsentFieldFromItsZeroValue_REQ_API_076()
    {
        var absent = JsonSerializer.Deserialize("""{"addresses":["10.8.0.1/24"]}""", CoreJsonContext.Default.InterfaceSpec)!;
        var zero = JsonSerializer.Deserialize("""{"addresses":["10.8.0.1/24"],"enabled":false,"mtu":0,"listen_port":0}""", CoreJsonContext.Default.InterfaceSpec)!;

        Assert.Equal((null, null, null), (absent.Enabled, absent.Mtu, absent.ListenPort));
        Assert.Equal((false, 0u, 0u), (zero.Enabled, zero.Mtu, zero.ListenPort));
        Assert.Equal(true, Defaults.Apply(absent).Enabled);   // absent takes the default...
        Assert.Equal(false, Defaults.Apply(zero).Enabled);    // ...and false stays false
    }

    [Fact]
    public void Warnings_AreAListOfCodeAndMessage_REQ_RES_033()
    {
        var warned = Interface with { Status = Interface.Status with { Warnings = [new Warning("MTU_OUT_OF_RANGE", "mtu 1200 lies outside 1280 to 1500.")] } };

        var warnings = Json(warned, CoreJsonContext.Default.InterfaceResource).GetProperty("status").GetProperty("warnings");
        var entry = Assert.Single(warnings.EnumerateArray());
        Assert.Equal(["code", "message"], entry.EnumerateObject().Select(p => p.Name));
        Assert.Equal("MTU_OUT_OF_RANGE", entry.GetProperty("code").GetString());
        Assert.Equal(JsonValueKind.Array, Json(Interface, CoreJsonContext.Default.InterfaceResource).GetProperty("status").GetProperty("warnings").ValueKind);
    }
}

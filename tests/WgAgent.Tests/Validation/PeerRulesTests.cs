using WgAgent.Core.Model;
using WgAgent.Core.Validation;
using WgAgent.Platform;

namespace WgAgent.Tests.Validation;

public class PeerRulesTests
{
    private static readonly InterfaceSpec Iface = Defaults.Apply(new InterfaceSpec { Addresses = ["10.8.0.1/24"] });

    private static PeerSpec Peer(params string[] allowedIps) => Defaults.Apply(new PeerSpec { AllowedIps = allowedIps });

    private static PeerCheck Check(PeerSpec spec, params PeerSpec[] others) => new()
    {
        Interface = Iface,
        InterfacePublicKey = TestKeys.Public(1),
        PublicKey = TestKeys.Base64(2),
        Spec = spec,
        OtherPeers = others,
    };

    private static string? ErrorOf(PeerCheck check) => PeerRules.Check(check).Error?.Code;
    private static IEnumerable<string> WarningsOf(PeerCheck check) => PeerRules.Check(check).Warnings.Select(w => w.Code);

    [Theory]
    [InlineData("tooshort=")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("-_-_-_-_-_-_-_-_-_-_-_-_-_-_-_-_-_-_-_-_-_-=")]
    public void PublicKey_NotThirtyTwoBytesOfBase64_IsRejected_REQ_VAL_011(string key) =>
        Assert.Equal("PUBLIC_KEY_INVALID", ErrorOf(Check(Peer("10.8.0.2/32")) with { PublicKey = key }));

    [Fact]
    public void AllowedIps_IdenticalToAnotherPeers_AreRejected_REQ_VAL_012()
    {
        Assert.Equal("ALLOWED_IPS_DUPLICATE", ErrorOf(Check(Peer("10.8.0.2/32"), Peer("10.8.0.2/32"))));
        Assert.Null(ErrorOf(Check(Peer("10.8.0.2/32"), Peer("10.8.0.3/32"))));
    }

    [Fact]
    public void AllowedIps_Empty_AreRejectedUnlessGenerated_REQ_VAL_017()
    {
        Assert.Equal("ALLOWED_IPS_REQUIRED", ErrorOf(Check(Peer())));
        Assert.Null(ErrorOf(Check(Peer()) with { GenerateKeypair = true, PublicKey = null }));
    }

    [Fact]
    public void Ipv6_AnywhereInAPeer_IsRejected_REQ_VAL_020()
    {
        Assert.Equal("IPV6_NOT_SUPPORTED", ErrorOf(Check(Peer("fd00::2/128"))));
        Assert.Equal("IPV6_NOT_SUPPORTED", ErrorOf(Check(Peer("10.8.0.2/32") with { Endpoint = "[2001:db8::1]:51820" })));
        Assert.Equal("IPV6_NOT_SUPPORTED", ErrorOf(Check(Peer("10.8.0.2/32")) with { ClientAllowedIps = ["::/0"] }));
        Assert.Equal("IPV6_NOT_SUPPORTED", ErrorOf(Check(Peer("10.8.0.2/32")) with { Dns = ["2001:4860:4860::8888"] }));
    }

    [Fact]
    public void AllowedIps_NotACidr_AreRejected_REQ_VAL_047()
    {
        Assert.Equal("CIDR_INVALID", ErrorOf(Check(Peer("10.8.0.2"))));
        Assert.Equal("CIDR_INVALID", ErrorOf(Check(Peer("10.8.0.2/32")) with { ClientAllowedIps = ["everything"] }));
    }

    [Fact]
    public void Keepalive_AboveSixteenBits_IsRejected_REQ_VAL_037()
    {
        Assert.Equal("KEEPALIVE_INVALID", ErrorOf(Check(Peer("10.8.0.2/32") with { PersistentKeepalive = 65536 })));
        Assert.Equal("KEEPALIVE_INVALID", ErrorOf(Check(Peer("10.8.0.2/32")) with { ClientPersistentKeepalive = 70000 }));
        Assert.Null(ErrorOf(Check(Peer("10.8.0.2/32") with { PersistentKeepalive = 65535 })));
    }

    [Fact]
    public void PresharedKey_Malformed_IsRejected_REQ_VAL_039() =>
        Assert.Equal("KEY_INVALID", ErrorOf(Check(Peer("10.8.0.2/32") with { PresharedKey = SecretKey.Parse("short") })));

    [Fact]
    public void Peer_CarryingTheInterfacesOwnKey_IsRejected_REQ_VAL_040() =>
        Assert.Equal("PEER_IS_INTERFACE", ErrorOf(Check(Peer("10.8.0.2/32")) with { PublicKey = TestKeys.Base64(1) }));

    [Fact]
    public void AllowedIps_WithHostBits_AreRejected_REQ_VAL_041()
    {
        Assert.Equal("ALLOWED_IPS_NOT_CANONICAL", ErrorOf(Check(Peer("10.8.0.5/24"))));
        Assert.Null(ErrorOf(Check(Peer("10.8.0.5/32"))));
    }

    [Theory]
    [InlineData("vpn.example.com")]
    [InlineData("vpn.example.com:0")]
    [InlineData("vpn.example.com:70000")]
    [InlineData(":51820")]
    [InlineData("bad_host!:51820")]
    [InlineData("999.1.1.1:51820")]
    public void Endpoint_NotAHostAndPort_IsRejected_REQ_VAL_042(string endpoint) =>
        Assert.Equal("ENDPOINT_INVALID", ErrorOf(Check(Peer("10.8.0.2/32") with { Endpoint = endpoint })));

    [Fact]
    public void GeneratedPeer_WithNoAddressInTheSubnets_IsRejected_REQ_VAL_043()
    {
        var generated = Check(Peer("192.168.50.0/24")) with { GenerateKeypair = true, PublicKey = null };
        Assert.Equal("CLIENT_ADDRESS_MISSING", ErrorOf(generated));
        Assert.Null(ErrorOf(generated with { Spec = Peer("10.8.0.7/32", "192.168.50.0/24") }));
    }

    [Fact]
    public void AllowedIps_DefaultRoute_IsRejected_REQ_VAL_044() =>
        Assert.Equal("ALLOWED_IPS_DEFAULT_ROUTE", ErrorOf(Check(Peer("0.0.0.0/0"))));

    [Fact]
    public void GeneratedPeer_InAFullSubnet_IsRejected_REQ_VAL_046()
    {
        // A /30 holds two hosts: .1 is the interface's, .2 another peer's.
        var small = Defaults.Apply(new InterfaceSpec { Addresses = ["10.8.0.1/30"] });
        var check = new PeerCheck { Interface = small, Spec = Peer(), GenerateKeypair = true, OtherPeers = [Peer("10.8.0.2/32")] };
        Assert.Equal("SUBNET_FULL", ErrorOf(check));
        Assert.Null(ErrorOf(check with { OtherPeers = [] }));
    }

    [Fact]
    public void AllowedIps_OverlappingAtADifferentLength_Warn_REQ_VAL_030()
    {
        Assert.Contains("ALLOWED_IPS_OVERLAP", WarningsOf(Check(Peer("10.8.0.0/28"), Peer("10.8.0.4/32"))));
        Assert.Contains("ALLOWED_IPS_OVERLAP", WarningsOf(Check(Peer("10.8.0.0/28", "10.8.0.4/32"))));
        Assert.DoesNotContain("ALLOWED_IPS_OVERLAP", WarningsOf(Check(Peer("10.8.0.2/32"), Peer("10.8.0.3/32"))));
    }

    [Fact]
    public void AllowedIps_OutsideEverySubnet_Warn_REQ_VAL_031()
    {
        Assert.Contains("ALLOWED_IPS_OUT_OF_SUBNET", WarningsOf(Check(Peer("192.168.50.0/24"))));
        Assert.DoesNotContain("ALLOWED_IPS_OUT_OF_SUBNET", WarningsOf(Check(Peer("10.8.0.2/32"))));
    }

    [Fact]
    public void Endpoint_NamingAHost_Warns_REQ_VAL_033()
    {
        Assert.Contains("ENDPOINT_NOT_IP", WarningsOf(Check(Peer("10.8.0.2/32") with { Endpoint = "vpn.example.com:51820" })));
        Assert.DoesNotContain("ENDPOINT_NOT_IP", WarningsOf(Check(Peer("10.8.0.2/32") with { Endpoint = "203.0.113.7:51820" })));
    }

    [Fact]
    public void LowestFreeHost_SkipsHeldAddresses_REQ_KEY_047()
    {
        Cidr C(string t) => Cidr.TryParse(t, out var c) ? c : throw new ArgumentException(t);
        var subnet = C("10.8.0.0/24");
        Assert.Equal(C("10.8.0.2/32"), Networks.LowestFreeHost(subnet, [C("10.8.0.1/24")], []));
        Assert.Equal(C("10.8.0.4/32"), Networks.LowestFreeHost(subnet, [C("10.8.0.1/24")], [C("10.8.0.2/32"), C("10.8.0.3/32")]));
        // An allowed-IPs range holds every address in it.
        Assert.Equal(C("10.8.0.16/32"), Networks.LowestFreeHost(subnet, [C("10.8.0.1/24")], [C("10.8.0.0/28")]));
    }
}

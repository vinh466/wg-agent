using WgAgent.Core.Model;
using WgAgent.Core.Validation;
using WgAgent.Platform;

namespace WgAgent.Tests.Validation;

public class InterfaceRulesTests
{
    private static readonly Dictionary<string, InterfaceSpec> NoOthers = [];

    private static InterfaceSpec Spec(params string[] addresses) => Defaults.Apply(new InterfaceSpec { Addresses = addresses });

    private static string? ErrorOf(InterfaceSpec spec, string name = "wg0", bool creating = true, HostView? host = null,
        Dictionary<string, InterfaceSpec>? others = null) =>
        InterfaceRules.Check(name, spec, creating, host ?? new HostView(), others ?? NoOthers).Error?.Code;

    private static Cidr C(string text) => Cidr.TryParse(text, out var c) ? c : throw new ArgumentException(text);

    [Theory]
    [InlineData("0bad")]
    [InlineData("has.dot")]
    [InlineData("abcdefghijklmnop")]
    [InlineData("all")]
    [InlineData("default")]
    public void Name_OutsideThePatternOrReserved_IsRejected_REQ_VAL_010(string name) =>
        Assert.Equal("INTERFACE_NAME_INVALID", ErrorOf(Spec("10.8.0.1/24"), name));

    [Fact]
    public void ListenPort_HeldByAnotherInterface_IsRejected_REQ_VAL_013()
    {
        var runtime = new HostView { WireGuardListenPorts = new Dictionary<string, uint> { ["wg9"] = 51820 } };
        Assert.Equal("LISTEN_PORT_IN_USE", ErrorOf(Spec("10.8.0.1/24"), host: runtime));

        // A managed interface that is disabled has no device, and its port still counts.
        var disabled = new Dictionary<string, InterfaceSpec> { ["wg1"] = Spec("10.9.0.1/24") with { Enabled = false } };
        Assert.Equal("LISTEN_PORT_IN_USE", ErrorOf(Spec("10.8.0.1/24"), others: disabled));

        // The interface the spec names is excluded.
        var self = new HostView { WireGuardListenPorts = new Dictionary<string, uint> { ["wg0"] = 51820 } };
        Assert.Null(ErrorOf(Spec("10.8.0.1/24"), creating: false, host: self));
    }

    [Fact]
    public void Addresses_OverlappingAnotherInterface_AreRejected_REQ_VAL_014()
    {
        var runtime = new HostView { WireGuardAddresses = new Dictionary<string, IReadOnlyList<Cidr>> { ["wg9"] = [C("10.8.0.9/24")] } };
        Assert.Equal("ADDRESS_CONFLICT", ErrorOf(Spec("10.8.0.1/24") with { ListenPort = 51821 }, host: runtime));

        var containing = new Dictionary<string, InterfaceSpec> { ["wg1"] = Spec("10.8.0.1/16") with { ListenPort = 51900 } };
        Assert.Equal("ADDRESS_CONFLICT", ErrorOf(Spec("10.8.3.1/24"), others: containing));

        var elsewhere = new Dictionary<string, InterfaceSpec> { ["wg1"] = Spec("10.9.0.1/24") with { ListenPort = 51900 } };
        Assert.Null(ErrorOf(Spec("10.8.0.1/24"), others: elsewhere));

        var self = new HostView { WireGuardAddresses = new Dictionary<string, IReadOnlyList<Cidr>> { ["wg0"] = [C("10.8.0.1/24")] } };
        Assert.Null(ErrorOf(Spec("10.8.0.1/24"), creating: false, host: self));
    }

    [Fact]
    public void Create_OfAnExistingLinkOrFile_IsRejected_REQ_VAL_015()
    {
        Assert.Equal("INTERFACE_EXISTS", ErrorOf(Spec("10.8.0.1/24"), host: new HostView { Links = new HashSet<string> { "wg0" } }));
        Assert.Equal("INTERFACE_EXISTS", ErrorOf(Spec("10.8.0.1/24"), host: new HostView { ConfigFiles = new HashSet<string> { "wg0" } }));
        // Any network link, not only a WireGuard one.
        Assert.Equal("INTERFACE_EXISTS", ErrorOf(Spec("10.8.0.1/24"), name: "eth0", host: new HostView { Links = new HashSet<string> { "eth0" } }));
        // The rule concerns creation alone.
        Assert.Null(ErrorOf(Spec("10.8.0.1/24"), creating: false, host: new HostView { Links = new HashSet<string> { "wg0" } }));
    }

    [Fact]
    public void Addresses_Empty_AreRejected_REQ_VAL_016() =>
        Assert.Equal("ADDRESSES_REQUIRED", ErrorOf(Spec()));

    [Fact]
    public void Ipv6_InAddresses_IsRejected_REQ_VAL_020()
    {
        Assert.Equal("IPV6_NOT_SUPPORTED", ErrorOf(Spec("fd00::1/64")));
        Assert.Equal("IPV6_NOT_SUPPORTED", ErrorOf(Spec("10.8.0.1/24", "fd00::1")));
    }

    [Theory]
    [InlineData("10.8.0.1")]
    [InlineData("vpn")]
    [InlineData("10.8.0.0/33")]
    [InlineData("10.8.1/24")]
    [InlineData("256.1.1.1/24")]
    public void Addresses_NotACidr_AreRejected_REQ_VAL_047(string address) =>
        Assert.Equal("CIDR_INVALID", ErrorOf(Spec(address)));

    [Theory]
    [InlineData(0u)]
    [InlineData(65536u)]
    public void ListenPort_OutOfRange_IsRejected_REQ_VAL_036(uint port) =>
        Assert.Equal("LISTEN_PORT_INVALID", ErrorOf(Spec("10.8.0.1/24") with { ListenPort = port }));

    [Theory]
    [InlineData(67u, "MTU_INVALID")]
    [InlineData(65536u, "MTU_INVALID")]
    [InlineData(68u, null)]
    [InlineData(65535u, null)]
    public void Mtu_OutsideSixtyEightTo65535_IsRejected_REQ_VAL_038(uint mtu, string? expected) =>
        Assert.Equal(expected, ErrorOf(Spec("10.8.0.1/24") with { Mtu = mtu }));

    [Fact]
    public void PrivateKey_Malformed_IsRejected_REQ_VAL_039() =>
        Assert.Equal("KEY_INVALID", ErrorOf(Spec("10.8.0.1/24") with { PrivateKey = SecretKey.Parse("not-a-key") }));

    [Fact]
    public void Hook_WithALineBreak_IsRejected_REQ_VAL_045()
    {
        Assert.Equal("HOOK_INVALID", ErrorOf(Spec("10.8.0.1/24") with { PostUp = ["iptables -A x\nPrivateKey = y"] }));
        Assert.Equal("HOOK_INVALID", ErrorOf(Spec("10.8.0.1/24") with { PostDown = ["echo\r"] }));
        Assert.Null(ErrorOf(Spec("10.8.0.1/24") with { PostUp = ["iptables -t nat -A POSTROUTING -s 10.8.0.0/24 -o eth0 -j MASQUERADE"] }));
    }

    [Theory]
    [InlineData(1279u, true)]
    [InlineData(1280u, false)]
    [InlineData(1500u, false)]
    [InlineData(1501u, true)]
    public void Mtu_OutsideTheUsualRange_Warns_REQ_VAL_032(uint mtu, bool warns)
    {
        var result = InterfaceRules.Check("wg0", Spec("10.8.0.1/24") with { Mtu = mtu }, true, new HostView(), NoOthers);
        Assert.True(result.IsValid);
        Assert.Equal(warns, result.Warnings.Any(w => w.Code == "MTU_OUT_OF_RANGE"));
    }
}

using WgAgent.Core.Model;
using WgAgent.Core.Validation;
using WgAgent.Platform;

namespace WgAgent.Tests.Validation;

// SPEC-07. Each method embeds the REQ ID it verifies, per the convention in
// docs/20-spec/README.md, so docs/check-traceability.sh can match it.
public sealed class ValidatorTests
{
    private sealed class FakeDesired : IDesired
    {
        public Dictionary<string, InterfaceSpec> Interfaces { get; } = [];
        public HashSet<string> Deletions { get; } = [];

        public IReadOnlyList<string> Names() => [.. Interfaces.Keys];

        public bool TryGetInterface(string name, out InterfaceSpec spec)
        {
            if (Interfaces.TryGetValue(name, out var s)) { spec = s; return true; }
            spec = new InterfaceSpec();
            return false;
        }

        public bool DeletionRecord(string name) => Deletions.Contains(name);
    }

    private static string Pub() => Key.Generate().GetPublicKey().ToBase64();

    private static InterfaceSpec Iface(params string[] addresses) => new()
    {
        Addresses = addresses.Length == 0 ? ["10.100.0.1/24"] : addresses,
        ForwardPolicy = ForwardPolicySpec.Default(),
    };

    private static Peer PeerWith(string publicKey, params string[] allowedIPs) => new()
    {
        PublicKey = publicKey,
        Spec = new PeerSpec { AllowedIPs = allowedIPs.Length == 0 ? ["10.100.0.2/32"] : allowedIPs },
    };

    private static Validator ValidatorWith(Host? host = null, IDesired? desired = null)
        => new(host ?? new Host(), desired);

    private static bool HasError(ValidationResult r, string code) => r.Errors.Any(f => f.Reason == code);
    private static bool HasWarning(ValidationResult r, string code) => r.Warnings.Any(f => f.Reason == code);

    // ── errors ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("0bad")]      // must start with a letter
    [InlineData("has space")] // no spaces
    [InlineData("waytoolonginterface")] // over 15 characters
    [InlineData("")]          // empty
    public void Name_Invalid_REQ_VAL_010(string name)
    {
        var r = ValidatorWith().Interface(name, Iface(), [], Op.Create);
        Assert.True(HasError(r, ReasonCodes.NameInvalid));
    }

    [Fact]
    public void Name_Valid_REQ_VAL_010()
    {
        var r = ValidatorWith().Interface("wg0", Iface(), [], Op.Create);
        Assert.False(HasError(r, ReasonCodes.NameInvalid));
    }

    [Fact]
    public void Addresses_Required_REQ_VAL_016()
    {
        var spec = new InterfaceSpec { Addresses = [], ForwardPolicy = ForwardPolicySpec.Default() };
        var r = ValidatorWith().Interface("wg0", spec, [], Op.Create);
        Assert.True(HasError(r, ReasonCodes.AddressesRequired));
    }

    [Fact]
    public void Addresses_IPv6_Rejected_REQ_VAL_020()
    {
        var r = ValidatorWith().Interface("wg0", Iface("fd00::1/64"), [], Op.Create);
        Assert.True(HasError(r, ReasonCodes.IPv6NotSupported));
    }

    [Fact]
    public void Addresses_Conflict_REQ_VAL_014()
    {
        var host = new Host();
        host.Addresses["wg1"] = [Cidr.Parse("10.100.0.5/24")];
        var r = ValidatorWith(host).Interface("wg0", Iface("10.100.0.1/24"), [], Op.Create);
        Assert.True(HasError(r, ReasonCodes.AddressConflict));
    }

    [Fact]
    public void ListenPort_InUse_REQ_VAL_013()
    {
        var host = new Host();
        host.Ports["wg1"] = 51820;
        var spec = Iface() with { ListenPort = 51820 };
        var r = ValidatorWith(host).Interface("wg0", spec, [], Op.Create);
        Assert.True(HasError(r, ReasonCodes.ListenPortInUse));
    }

    [Fact]
    public void Create_Collision_REQ_VAL_015()
    {
        var host = new Host();
        host.Ports["wg0"] = 0;
        var r = ValidatorWith(host).Interface("wg0", Iface(), [], Op.Create);
        Assert.True(HasError(r, ReasonCodes.InterfaceExists));
    }

    [Fact]
    public void Create_Collision_Exempt_By_Deletion_REQ_VAL_015()
    {
        var host = new Host();
        host.Ports["wg0"] = 0;
        var desired = new FakeDesired();
        desired.Deletions.Add("wg0");
        var r = ValidatorWith(host, desired).Interface("wg0", Iface(), [], Op.Create);
        Assert.False(HasError(r, ReasonCodes.InterfaceExists));
    }

    [Fact]
    public void ForwardPolicy_ExternalNeedsUplink_REQ_VAL_021()
    {
        var spec = Iface() with
        {
            ForwardPolicy = ForwardPolicySpec.Default() with { External = Axis.Allow },
        };
        var r = ValidatorWith().Interface("wg0", spec, [], Op.Create);
        Assert.True(HasError(r, ReasonCodes.NeedsUplink));
    }

    [Fact]
    public void PeerInterface_NotFound_REQ_VAL_022()
    {
        var spec = Iface() with
        {
            ForwardPolicy = ForwardPolicySpec.Default() with
            {
                InterInterface = Axis.AllowList,
                AllowedPeerInterfaces = ["nosuch"],
            },
        };
        var r = ValidatorWith(desired: new FakeDesired()).Interface("wg0", spec, [], Op.Create);
        Assert.True(HasError(r, ReasonCodes.PeerInterfaceNotFound));
    }

    [Fact]
    public void Peer_PublicKey_Invalid_REQ_VAL_011()
    {
        var peer = PeerWith("not-a-key");
        var r = ValidatorWith().Interface("wg0", Iface(), [peer], Op.Create);
        Assert.True(HasError(r, ReasonCodes.PublicKeyInvalid));
    }

    [Fact]
    public void Peer_AllowedIPs_Required_REQ_VAL_017()
    {
        var peer = new Peer { PublicKey = Pub(), Spec = new PeerSpec { AllowedIPs = [] } };
        var r = ValidatorWith().Interface("wg0", Iface(), [peer], Op.Create);
        Assert.True(HasError(r, ReasonCodes.AllowedIPsRequired));
    }

    [Fact]
    public void Peer_AllowedIPs_Duplicate_REQ_VAL_012()
    {
        var a = PeerWith(Pub(), "10.100.0.2/32");
        var b = PeerWith(Pub(), "10.100.0.2/32");
        var r = ValidatorWith().Interface("wg0", Iface(), [a, b], Op.Create);
        Assert.True(HasError(r, ReasonCodes.AllowedIPsDuplicate));
    }

    // ── warnings ────────────────────────────────────────────────────────────────

    [Fact]
    public void Mtu_OutOfRange_REQ_VAL_032()
    {
        var spec = Iface() with { Mtu = 500 };
        var r = ValidatorWith().Interface("wg0", spec, [], Op.Create);
        Assert.True(HasWarning(r, ReasonCodes.MtuOutOfRange));
    }

    [Fact]
    public void OneSided_AllowList_REQ_VAL_023()
    {
        var desired = new FakeDesired();
        desired.Interfaces["wg1"] = Iface("10.101.0.1/24"); // wg1 does not permit wg0 back
        var spec = Iface() with
        {
            ForwardPolicy = ForwardPolicySpec.Default() with
            {
                InterInterface = Axis.AllowList,
                AllowedPeerInterfaces = ["wg1"],
            },
        };
        var r = ValidatorWith(desired: desired).Interface("wg0", spec, [], Op.Create);
        Assert.True(HasWarning(r, ReasonCodes.OneSided));
    }

    [Fact]
    public void IgnoredPeerInterfaces_REQ_VAL_034()
    {
        var desired = new FakeDesired();
        desired.Interfaces["wg1"] = Iface("10.101.0.1/24");
        var spec = Iface() with
        {
            ForwardPolicy = ForwardPolicySpec.Default() with
            {
                InterInterface = Axis.Deny, // list is ignored while not ALLOW_LIST
                AllowedPeerInterfaces = ["wg1"],
            },
        };
        var r = ValidatorWith(desired: desired).Interface("wg0", spec, [], Op.Create);
        Assert.True(HasWarning(r, ReasonCodes.PeerInterfacesIgnored));
    }

    [Fact]
    public void ExternalWithoutNat_REQ_VAL_035()
    {
        var spec = Iface() with
        {
            ForwardPolicy = ForwardPolicySpec.Default() with { External = Axis.Allow },
            // uplink set so REQ-VAL-021 does not block; NAT disabled so the warning fires.
            Nat = new NatSpec { EnableUplinkForwarding = true, Enabled = false },
        };
        var r = ValidatorWith().Interface("wg0", spec, [], Op.Create);
        Assert.True(HasWarning(r, ReasonCodes.ExternalWithoutNat));
    }

    [Fact]
    public void OverlappingAllowedIPs_REQ_VAL_030()
    {
        var a = PeerWith(Pub(), "10.100.0.0/24");
        var b = PeerWith(Pub(), "10.100.0.128/25");
        var r = ValidatorWith().Interface("wg0", Iface("10.100.0.1/24"), [a, b], Op.Create);
        Assert.True(HasWarning(r, ReasonCodes.AllowedIPsOverlap));
    }

    [Fact]
    public void AllowedIPsOutOfSubnet_REQ_VAL_031()
    {
        var peer = PeerWith(Pub(), "192.168.50.0/24");
        var r = ValidatorWith().Interface("wg0", Iface("10.100.0.1/24"), [peer], Op.Create);
        Assert.True(HasWarning(r, ReasonCodes.AllowedIPsOutOfSubnet));
    }

    [Fact]
    public void Endpoint_Hostname_Warns_REQ_VAL_033()
    {
        var peer = new Peer
        {
            PublicKey = Pub(),
            Spec = new PeerSpec { AllowedIPs = ["10.100.0.2/32"], Endpoint = "vpn.example.com:51820" },
        };
        var r = ValidatorWith().Interface("wg0", Iface(), [peer], Op.Create);
        Assert.True(HasWarning(r, ReasonCodes.EndpointNotIP));
    }

    [Fact]
    public void Endpoint_IP_Accepted_REQ_VAL_033()
    {
        var peer = new Peer
        {
            PublicKey = Pub(),
            Spec = new PeerSpec { AllowedIPs = ["10.100.0.2/32"], Endpoint = "203.0.113.7:51820" },
        };
        var r = ValidatorWith().Interface("wg0", Iface(), [peer], Op.Create);
        Assert.False(HasWarning(r, ReasonCodes.EndpointNotIP));
    }

    // ── ownership ────────────────────────────────────────────────────────────────

    [Fact]
    public void Ownership_Managed_REQ_RES_017()
        => Assert.Equal(Ownership.Managed, OwnershipRule.Of(describes: true, deletionRecord: false));

    [Fact]
    public void Ownership_Orphaned_REQ_RES_017()
        => Assert.Equal(Ownership.Orphaned, OwnershipRule.Of(describes: false, deletionRecord: true));

    [Fact]
    public void Ownership_Foreign_REQ_RES_017()
        => Assert.Equal(Ownership.Foreign, OwnershipRule.Of(describes: false, deletionRecord: false));
}

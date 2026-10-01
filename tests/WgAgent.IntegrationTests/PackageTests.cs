namespace WgAgent.IntegrationTests;

/// <summary>
/// The packaging tier: the .deb installed under the distribution's own systemd. It runs only when
/// WGAGENT_DEB names a built package — `make test-packaging` builds it and sets the variable; the
/// other tiers skip these. Tests re-establish their precondition, so order does not matter.
/// </summary>
public sealed class PackageTests : IClassFixture<PackageHost>
{
    private readonly PackageHost _host;
    public PackageTests(PackageHost host) => _host = host;

    private Node Install()
    {
        Assert.SkipWhen(PackageHost.Deb is null, "set WGAGENT_DEB (run `make test-packaging`).");
        var node = _host.Node;
        node.Must("apt-get install -y /root/wg-agent.deb", TimeSpan.FromMinutes(2));   // install, and idempotent on re-run
        node.Must("""for i in $(seq 1 50); do systemctl is-active --quiet wg-agent && exit 0; sleep 0.2; done; journalctl -u wg-agent --no-pager | tail; exit 1""", TimeSpan.FromMinutes(1));
        return node;
    }

    [Fact]
    public void Installed_ServesAndIsConfined_REQ_CFG_021_REQ_SEC_031_REQ_SEC_086()
    {
        var node = Install();

        Assert.Equal("active", node.Run("systemctl is-active wg-agent").Out.Trim());
        // The one self-contained native binary runs with no .NET runtime in the image (REQ-CFG-044),
        // and it is an ELF executable; the unit and the conffile are in place (REQ-CFG-021, REQ-CFG-038).
        Assert.StartsWith("wg-agent", node.Must("/usr/bin/wg-agent version"));
        Assert.Equal("7f454c46", node.Must("head -c4 /usr/bin/wg-agent | od -An -tx1 | tr -d ' \\n'").Trim());
        Assert.Equal("0644", node.Must("stat -c %a /etc/default/wg-agent").Trim().PadLeft(4, '0'));
        // The token was generated, owner-only (REQ-CFG-045); it was not echoed to the install log.
        Assert.Equal("600", node.Must("stat -c %a /etc/wg-agent/token").Trim());
        Assert.NotEqual("", node.Must("cat /etc/wg-agent/token").Trim());
        // The listener is on loopback (REQ-CFG-022); health answers.
        Assert.Equal("200", node.Must("""curl -s -o /dev/null -w '%{http_code}' http://127.0.0.1:9585/v1/health""", TimeSpan.FromMinutes(1)).Trim());
        // Confinement holds (REQ-SEC-031, REQ-SEC-086, REQ-CFG-043): CAP_NET_ADMIN only in the unit,
        // and ProtectSystem=strict applied at runtime.
        Assert.Contains("CapabilityBoundingSet=CAP_NET_ADMIN", node.Must("systemctl cat wg-agent"));
        Assert.Equal("strict", node.Must("systemctl show wg-agent -p ProtectSystem --value").Trim());
    }

    [Fact]
    public void Reinstalling_UpdatesInPlaceKeepingTheToken_REQ_CFG_052()
    {
        var node = Install();
        var token = node.Must("cat /etc/wg-agent/token").Trim();

        node.Must("apt-get install -y --reinstall /root/wg-agent.deb", TimeSpan.FromMinutes(2));   // the update path

        Assert.Equal("active", node.Run("systemctl is-active wg-agent").Out.Trim());
        Assert.Equal(token, node.Must("cat /etc/wg-agent/token").Trim());   // REQ-CFG-045 regenerates only when absent
    }

    [Fact]
    public void Checksum_RejectsATamperedDeb_REQ_CFG_053()
    {
        Assert.SkipWhen(PackageHost.Deb is null, "set WGAGENT_DEB.");
        var node = _host.Node;
        // The exact verification install.sh runs: a line in SHA256SUMS checked with sha256sum -c.
        node.Must("cd /root && sha256sum wg-agent.deb > SHA256SUMS");
        Assert.Equal(0, node.Run("cd /root && grep ' wg-agent.deb$' SHA256SUMS | sha256sum -c -").Code);

        node.Must("cp /root/wg-agent.deb /root/tampered.deb && echo corrupt >> /root/tampered.deb && sed 's/wg-agent.deb/tampered.deb/' /root/SHA256SUMS > /root/BAD");
        Assert.NotEqual(0, node.Run("cd /root && grep ' tampered.deb$' BAD | sha256sum -c -").Code);
    }

    [Fact]
    public void Purge_KeepsLinksAndClearsState_REQ_CFG_027()
    {
        var node = Install();
        // An interface the agent created, through the installed binary.
        node.Must("wg-agent interface create pkg0 --addresses 10.40.0.1/24 --listen-port 52040");
        Assert.Equal(0, node.Run("ip link show pkg0").Code);

        var purge = node.Run("apt-get purge -y wg-agent", TimeSpan.FromMinutes(2));
        Assert.Equal(0, purge.Code);

        // REQ-CFG-025: the service was stopped and its unit removed; REQ-CFG-026: config, token and store are gone.
        Assert.NotEqual("active", node.Run("systemctl is-active wg-agent").Out.Trim());
        Assert.NotEqual(0, node.Run("test -e /etc/wg-agent/token").Code);
        Assert.NotEqual(0, node.Run("test -e /etc/default/wg-agent").Code);
        Assert.NotEqual(0, node.Run("test -e /var/lib/wg-agent/state.json").Code);
        // REQ-CFG-027: the link and its file survive; REQ-CFG-028: purge named it.
        Assert.Equal(0, node.Run("ip link show pkg0").Code);
        Assert.Equal(0, node.Run("test -e /etc/wireguard/pkg0.conf").Code);
        Assert.Contains("pkg0", purge.Out + purge.Error);

        node.Run("wg-quick down pkg0 2>/dev/null; rm -f /etc/wireguard/pkg0.conf");   // leave the shared container clean
    }
}

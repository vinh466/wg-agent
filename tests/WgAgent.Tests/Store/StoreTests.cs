using System.Collections;
using System.Diagnostics;
using System.Text.Json;
using WgAgent.Core;
using WgAgent.Core.Model;
using WgAgent.Core.Store;

namespace WgAgent.Tests.Store;

public sealed class StoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wg-agent-store-" + Guid.NewGuid().ToString("N"));
    private string StorePath => Path.Combine(_dir, "state.json");

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }

    private static StoreState OneInterface(IReadOnlyDictionary<string, string>? labels = null) =>
        StoreState.Empty.With("wg0", new StoredInterface(
            Defaults.Apply(new InterfaceSpec { PrivateKey = TestKeys.Secret(3), Addresses = ["10.8.0.1/24"], Labels = labels }),
            DateTimeOffset.Parse("2026-09-30T10:00:00Z"),
            new Dictionary<string, PeerSpec>
            {
                [TestKeys.Base64(4)] = Defaults.Apply(new PeerSpec { PresharedKey = TestKeys.Secret(5), AllowedIps = ["10.8.0.2/32"] }),
            }));

    [Fact]
    public void DesiredState_SurvivesARestart_REQ_RCN_001()
    {
        new StateStore(StorePath).Save(OneInterface());
        var loaded = new StateStore(StorePath).Load();   // a new process reading the file

        var wg0 = loaded.Interfaces["wg0"];
        Assert.Equal(TestKeys.Base64(3), wg0.Spec.PrivateKey!.Reveal());
        Assert.Equal(["10.8.0.1/24"], wg0.Spec.Addresses);
        Assert.Equal(51820u, wg0.Spec.ListenPort);
        Assert.Equal(DateTimeOffset.Parse("2026-09-30T10:00:00Z"), wg0.CreatedAt);
        var peer = wg0.Peers[TestKeys.Base64(4)];
        Assert.Equal(TestKeys.Base64(5), peer.PresharedKey!.Reveal());
        Assert.Equal(["10.8.0.2/32"], peer.AllowedIps);
    }

    [Fact]
    public void Store_HoldsSpecValuesAndCreationTimeOnly_REQ_RCN_002()
    {
        new StateStore(StorePath).Save(OneInterface());
        using var doc = JsonDocument.Parse(File.ReadAllText(StorePath));
        string[] Keys(JsonElement e) => [.. e.EnumerateObject().Select(p => p.Name).Order()];

        Assert.Equal(["interfaces", "schema_version"], Keys(doc.RootElement));
        var wg0 = doc.RootElement.GetProperty("interfaces").GetProperty("wg0");
        Assert.Equal(["created_at", "peers", "spec"], Keys(wg0));
        Assert.Subset(new HashSet<string> { "private_key", "listen_port", "addresses", "mtu", "enabled", "labels", "post_up", "post_down" },
            Keys(wg0.GetProperty("spec")).ToHashSet());
        Assert.Subset(new HashSet<string> { "preshared_key", "allowed_ips", "endpoint", "persistent_keepalive", "labels" },
            Keys(wg0.GetProperty("peers").GetProperty(TestKeys.Base64(4))).ToHashSet());
    }

    [Fact]
    public void Store_HoldsNoStatusAndNoCounter_REQ_RCN_050()
    {
        new StateStore(StorePath).Save(OneInterface());
        var text = File.ReadAllText(StorePath);
        foreach (var word in new[] { "status", "rx_bytes", "tx_bytes", "last_handshake", "oper_state", "peer_count", "online" })
            Assert.DoesNotContain(word, text);
    }

    [Fact]
    public void AFailedWrite_LeavesTheStoreAsItWas_REQ_RCN_003()
    {
        var store = new StateStore(StorePath);
        store.Save(OneInterface());
        var before = File.ReadAllBytes(StorePath);

        Assert.Throws<InvalidOperationException>(() => store.Save(OneInterface(new ExplodingLabels())));

        Assert.Equal(before, File.ReadAllBytes(StorePath));
        Assert.Equal(["state.json"], Directory.GetFiles(_dir).Select(Path.GetFileName).Order());
    }

    [Fact]
    public void StoreFile_IsOwnerOnly_REQ_RCN_004()
    {
        new StateStore(StorePath).Save(OneInterface());
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(StorePath));
        Assert.Equal(Run("id", "-u"), Run("stat", "-c", "%u", StorePath));
    }

    [Fact]
    public void ANewerSchema_IsRefusedWithBothVersions_REQ_RCN_005()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(StorePath, """{"schema_version": 99, "interfaces": {}}""");
        var error = Assert.Throws<AgentException>(() => new StateStore(StorePath).Load());
        Assert.Equal("STORE_SCHEMA_TOO_NEW", error.Code);
        Assert.Contains("99", error.Message);
        Assert.Contains("version 1", error.Message);
    }

    [Fact]
    public void Writers_AreSerialisedByOneLockThatARenameCannotBreak_REQ_RCN_042()
    {
        var store = new StateStore(StorePath);
        store.Save(OneInterface());

        // Held across a Save, which renames the store: the lock file is untouched.
        using (StoreLock.Acquire(store.LockPath, TimeSpan.FromSeconds(1)))
        {
            store.Save(OneInterface());
            Assert.Equal("STORE_BUSY", Assert.Throws<AgentException>(() =>
                StoreLock.Acquire(store.LockPath, TimeSpan.FromMilliseconds(100))).Code);
        }

        // Concurrent writers never overlap.
        var inside = 0; var maximum = 0;
        Parallel.For(0, 8, _ =>
        {
            using (StoreLock.Acquire(store.LockPath, TimeSpan.FromSeconds(10)))
            {
                var now = Interlocked.Increment(ref inside);
                lock (_dir) maximum = Math.Max(maximum, now);
                Thread.Sleep(20);
                Interlocked.Decrement(ref inside);
            }
        });
        Assert.Equal(1, maximum);
    }

    [Fact]
    public void AWriterGivesUpAfterTheTimeout_REQ_RCN_075()
    {
        Directory.CreateDirectory(_dir);
        var lockPath = StorePath + ".lock";
        using var held = StoreLock.Acquire(lockPath, TimeSpan.FromSeconds(1));
        var clock = Stopwatch.StartNew();
        var error = Assert.Throws<AgentException>(() => StoreLock.Acquire(lockPath, TimeSpan.FromMilliseconds(300)));
        Assert.Equal("STORE_BUSY", error.Code);
        Assert.InRange(clock.ElapsedMilliseconds, 250, 5000);
    }

    private static string Run(string file, params string[] args)
    {
        var info = new ProcessStartInfo(file) { RedirectStandardOutput = true };
        foreach (var a in args) info.ArgumentList.Add(a);
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        return output;
    }

    /// <summary>A labels map that fails while it is copied, midway through a write.</summary>
    private sealed class ExplodingLabels : IReadOnlyDictionary<string, string>
    {
        public string this[string key] => throw new InvalidOperationException();
        public IEnumerable<string> Keys => throw new InvalidOperationException();
        public IEnumerable<string> Values => throw new InvalidOperationException();
        public int Count => 1;
        public bool ContainsKey(string key) => false;
        public bool TryGetValue(string key, out string value) { value = ""; return false; }
        public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => throw new InvalidOperationException("boom");
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

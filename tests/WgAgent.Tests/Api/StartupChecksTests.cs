using WgAgent.Api;
using WgAgent.Core;
using WgAgent.Core.Store;
using WgAgent.Platform;
using WgAgent.Platform.Linux;

namespace WgAgent.Tests.Api;

public sealed class StartupChecksTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wg-agent-startup-" + Guid.NewGuid().ToString("N"));
    public StartupChecksTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private StateStore Store(string name = "state.json") => new(Path.Combine(_dir, name));
    private TokenFile Token(string content = "a-token")
    {
        var file = new TokenFile(Path.Combine(_dir, "token"));
        file.Write(Secret.From(content));
        return file;
    }

    [Fact]
    public void EveryCheckMustPassBeforeServing_REQ_API_050()
    {
        // A healthy node: nothing fails.
        Assert.Null(StartupChecks.Run(Store(), Token(), () => true));

        // Check 3: no token configured.
        var noToken = new TokenFile(Path.Combine(_dir, "absent-token"));
        Assert.Equal(ReasonCodes.TokenInvalid, StartupChecks.Run(Store(), noToken, () => true)!.Reason);

        // Check 4: CAP_NET_ADMIN absent.
        Assert.Equal(ReasonCodes.MissingCapNetAdmin, StartupChecks.Run(Store(), Token(), () => false)!.Reason);

        // Checks 1 and 2: a corrupt store.
        File.WriteAllText(Path.Combine(_dir, "bad.json"), "{ not json");
        Assert.Equal(ReasonCodes.StoreCorrupt, StartupChecks.Run(Store("bad.json"), Token(), () => true)!.Reason);
    }

    [Fact]
    public void ServeRefusesUntilTheChecksPass_REQ_API_051()
    {
        // health reports success only after every check passes; a failing check ends serve before it
        // ever serves, so health is never reachable until the node is healthy.
        var options = new ServerOptions
        {
            Service = null!, Store = Store(), Token = Token(), ListenAddress = "127.0.0.1:0", HasNetAdmin = () => false,
        };
        using var error = new StringWriter();
        Assert.Equal(1, Server.Run(options, error));
        Assert.Contains("MISSING_CAP_NET_ADMIN", error.ToString());
    }
}

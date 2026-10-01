namespace WgAgent.IntegrationTests;

/// <summary>
/// The container for the packaging tier, built lazily so the other tiers (which leave WGAGENT_DEB
/// unset) skip without ever starting one. The built .deb is copied in once.
/// </summary>
public sealed class PackageHost : IDisposable
{
    public static string? Deb => Environment.GetEnvironmentVariable("WGAGENT_DEB");

    private Node? _node;

    public Node Node => _node ??= Build();

    private static Node Build()
    {
        var node = new Node();
        node.CopyIn(Deb!, "/root/wg-agent.deb");
        return node;
    }

    public void Dispose() => _node?.Dispose();
}

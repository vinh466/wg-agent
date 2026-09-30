using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace WgAgent.Platform.Linux;

/// <summary><see cref="IHostNetwork"/> read in-process, with no child process.</summary>
public sealed class HostNetwork : IHostNetwork
{
    public IReadOnlySet<string> LinkNames() =>
        NetworkInterface.GetAllNetworkInterfaces().Select(i => i.Name).ToHashSet();

    public IReadOnlyList<Cidr> AddressesOf(string linkName) =>
        [.. NetworkInterface.GetAllNetworkInterfaces()
            .Where(i => i.Name == linkName)
            .SelectMany(i => i.GetIPProperties().UnicastAddresses)
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
            .Select(a => new Cidr(a.Address, a.PrefixLength))];
}

using WgAgent.Platform;

namespace WgAgent.Testing;

/// <summary>
/// A <see cref="ILink"/> view over a <see cref="FakeNode"/>. It is a view of its
/// own because <see cref="ILink.Names"/> covers every interface, while
/// <see cref="IDevice.Names"/> lists the WireGuard ones alone.
/// </summary>
public sealed class FakeLink(FakeNode node) : ILink
{
    private readonly FakeNode _n = node;

    public IReadOnlyList<string> Names()
    {
        var names = new List<string>(_n.Links.Keys);
        names.Sort(StringComparer.Ordinal);
        return names;
    }

    public LinkState State(string name)
        => _n.Links.TryGetValue(name, out var s) ? s : throw new InvalidOperationException($"no such link \"{name}\"");

    public void Add(string name)
    {
        if (_n.Links.ContainsKey(name)) throw new InvalidOperationException($"link \"{name}\" exists");
        _n.Links[name] = new LinkState { Name = name, Type = "wireguard", Mtu = 1420 };
        _n.Devices[name] = new DeviceState { Name = name };
        _n.Calls.Add($"LinkAdd({name})");
    }

    public void Delete(string name)
    {
        _n.Links.Remove(name);
        _n.Devices.Remove(name);
        _n.RouteTable.Remove(name);
        _n.Calls.Add($"LinkDel({name})");
    }

    public void SetUp(string name) => SetState(name, up: true);
    public void SetDown(string name) => SetState(name, up: false);

    private void SetState(string name, bool up)
    {
        _n.Links[name] = State(name) with { AdminUp = up };
        _n.Calls.Add((up ? "LinkSetUp(" : "LinkSetDown(") + name + ")");
    }

    public void SetMtu(string name, int mtu)
    {
        _n.Links[name] = State(name) with { Mtu = mtu };
        _n.Calls.Add($"SetMtu({name},{mtu})");
    }

    public void AddAddress(string name, Cidr address)
    {
        var s = State(name);
        if (s.Addresses.Contains(address)) return;
        _n.Links[name] = s with { Addresses = [.. s.Addresses, address] };
        _n.Calls.Add($"AddrAdd({name},{address})");
    }

    public void DeleteAddress(string name, Cidr address)
    {
        var s = State(name);
        _n.Links[name] = s with { Addresses = [.. s.Addresses.Where(a => a != address)] };
        _n.Calls.Add($"AddrDel({name},{address})");
    }

    public IReadOnlyList<Cidr> Routes(string name)
    {
        if (!_n.Links.ContainsKey(name)) throw new InvalidOperationException($"no such link \"{name}\"");
        return _n.RouteTable.TryGetValue(name, out var routes) ? [.. routes] : [];
    }

    public void AddRoute(string name, Cidr destination)
    {
        var routes = _n.RouteTable.TryGetValue(name, out var r) ? r : _n.RouteTable[name] = [];
        if (routes.Contains(destination)) return;
        routes.Add(destination);
        _n.Calls.Add($"RouteAdd({name},{destination})");
    }

    public void DeleteRoute(string name, Cidr destination)
    {
        if (_n.RouteTable.TryGetValue(name, out var routes))
            routes.RemoveAll(r => r == destination);
        _n.Calls.Add($"RouteDel({name},{destination})");
    }
}

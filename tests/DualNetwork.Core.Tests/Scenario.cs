using System.Net;
using DualNetwork.Core.Model;
using DualNetwork.Core.Net;

namespace DualNetwork.Core.Tests;

/// <summary>Fluent builder for realistic Windows network states, including the routes Windows adds automatically.</summary>
internal sealed class Scenario
{
    private readonly List<AdapterInfo> _adapters = new();
    private readonly List<RouteEntry> _routes = new();
    private readonly List<NrptRule> _nrpt = new();

    public const int WiFi = 12;
    public const int Eth = 7;
    public const int Vpn = 40;

    public static Scenario Create() => new();

    public Scenario WithWiFi(string cidr = "192.168.1.25/24", string? gateway = "192.168.1.1", int metric = 35,
        string[]? dns = null, InternetStatus internet = InternetStatus.Available, int index = WiFi, string name = "Wi-Fi")
        => Add(new AdapterInfo
        {
            Index = index, Name = name, Kind = AdapterKind.WiFi, IsUp = true, InterfaceMetric = metric,
            Addresses = new[] { Addr(cidr) },
            Gateways = gateway is null ? Array.Empty<IPAddress>() : new[] { IPAddress.Parse(gateway) },
            DnsServers = (dns ?? new[] { "192.168.1.1" }).Select(IPAddress.Parse).ToList(),
            Internet = internet,
        });

    public Scenario WithEthernet(string cidr = "10.25.30.50/24", string? gateway = null, int metric = 25,
        string[]? dns = null, string? suffix = null, InternetStatus internet = InternetStatus.NotAvailable,
        int index = Eth, string name = "Ethernet", bool ipv6Default = false)
        => Add(new AdapterInfo
        {
            Index = index, Name = name, Kind = AdapterKind.Ethernet, IsUp = true, InterfaceMetric = metric,
            Addresses = new[] { Addr(cidr) },
            Gateways = gateway is null ? Array.Empty<IPAddress>() : new[] { IPAddress.Parse(gateway) },
            DnsServers = (dns ?? Array.Empty<string>()).Select(IPAddress.Parse).ToList(),
            DnsSuffix = suffix,
            Internet = internet,
            HasIPv6DefaultRoute = ipv6Default,
        });

    public Scenario WithVpn(string cidr = "100.101.1.2/32", string product = "Tailscale", int index = Vpn, int metric = 5)
        => Add(new AdapterInfo
        {
            Index = index, Name = product, Kind = AdapterKind.Vpn, Product = product, IsUp = true, InterfaceMetric = metric,
            Addresses = new[] { Addr(cidr) },
        });

    public Scenario WithAdapter(AdapterInfo a) => Add(a);

    public Scenario Route(string prefix, string nextHop, int ifIndex, int metric = 0)
    {
        _routes.Add(new RouteEntry(IPv4Prefix.Parse(prefix), IPAddress.Parse(nextHop), ifIndex, metric));
        return this;
    }

    public Scenario Nrpt(string ns, string server, string? comment = null, string? name = null)
    {
        _nrpt.Add(new NrptRule(name ?? Guid.NewGuid().ToString("B"), ns, new[] { server }, comment));
        return this;
    }

    public Scenario Disconnect(int index)
    {
        var i = _adapters.FindIndex(a => a.Index == index);
        _adapters[i] = _adapters[i] with { IsUp = false };
        _routes.RemoveAll(r => r.InterfaceIndex == index);
        return this;
    }

    private Scenario Add(AdapterInfo a)
    {
        _adapters.Add(a);
        // The routes Windows creates for a connected adapter.
        foreach (var addr in a.Addresses)
        {
            _routes.Add(new RouteEntry(addr.Subnet, IPAddress.Any, a.Index, 256, "Local"));
            _routes.Add(new RouteEntry(IPv4Prefix.Host(addr.Address), IPAddress.Any, a.Index, 256, "Local"));
            if (addr.PrefixLength < 31) _routes.Add(new RouteEntry(new IPv4Prefix(addr.Subnet.Broadcast, 32), IPAddress.Any, a.Index, 256, "Local"));
        }
        _routes.Add(new RouteEntry(IPv4Prefix.Multicast, IPAddress.Any, a.Index, 256, "Local"));
        foreach (var gw in a.Gateways) _routes.Add(new RouteEntry(IPv4Prefix.Default, gw, a.Index, 0, "NetMgmt"));
        return this;
    }

    private static AdapterAddress Addr(string cidr)
    {
        var parts = cidr.Split('/');
        return new AdapterAddress(IPAddress.Parse(parts[0]), int.Parse(parts[1]));
    }

    public NetworkState Build() => new()
    {
        Adapters = _adapters.ToList(),
        Routes = _routes.ToList(),
        NrptRules = _nrpt.ToList(),
    };
}

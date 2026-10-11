using System.Net;
using System.Text.Json.Serialization;
using DualNetwork.Core.Net;

namespace DualNetwork.Core.Model;

public enum AdapterKind { WiFi, Ethernet, Vpn, Virtual, Cellular, Loopback, Other }

public enum InternetStatus { Unknown, Available, NotAvailable }

/// <summary>An IPv4 address assigned to an adapter together with its on-link prefix length.</summary>
public sealed record AdapterAddress(IPAddress Address, int PrefixLength)
{
    [JsonIgnore] public IPv4Prefix Subnet => IPv4Prefix.FromAddress(Address, PrefixLength);
    [JsonIgnore] public bool IsLinkLocal => IPv4.IsLinkLocal(Address);
    public override string ToString() => $"{Address}/{PrefixLength}";
}

/// <summary>Everything the planner needs to know about one network adapter (IPv4 view).</summary>
public sealed record AdapterInfo
{
    public required int Index { get; init; }
    public required string Name { get; init; }
    public string Description { get; init; } = "";
    public required AdapterKind Kind { get; init; }
    /// <summary>Product name for VPN/virtual adapters, e.g. "Tailscale", "Hyper-V".</summary>
    public string? Product { get; init; }
    public bool IsUp { get; init; }
    public IReadOnlyList<AdapterAddress> Addresses { get; init; } = Array.Empty<AdapterAddress>();
    public IReadOnlyList<IPAddress> Gateways { get; init; } = Array.Empty<IPAddress>();
    public IReadOnlyList<IPAddress> DnsServers { get; init; } = Array.Empty<IPAddress>();
    public string? DnsSuffix { get; init; }
    public bool DhcpEnabled { get; init; }
    public int InterfaceMetric { get; init; }
    public bool AutomaticMetric { get; init; } = true;
    public InternetStatus Internet { get; init; } = InternetStatus.Unknown;
    public bool HasIPv6DefaultRoute { get; init; }
    public string? MacAddress { get; init; }
    public long SpeedBitsPerSecond { get; init; }

    [JsonIgnore] public IEnumerable<AdapterAddress> UsableAddresses => Addresses.Where(a => !a.IsLinkLocal);
    [JsonIgnore] public bool HasUsableIPv4 => IsUp && UsableAddresses.Any();
    [JsonIgnore] public bool HasOnlyLinkLocal => IsUp && Addresses.Count > 0 && !UsableAddresses.Any();
    [JsonIgnore] public AdapterAddress? PrimaryAddress => UsableAddresses.FirstOrDefault() ?? Addresses.FirstOrDefault();
    [JsonIgnore] public IEnumerable<IPv4Prefix> Subnets => UsableAddresses.Select(a => a.Subnet).Distinct();
    [JsonIgnore] public IPAddress? Gateway => Gateways.FirstOrDefault(IPv4.IsV4);
    [JsonIgnore] public IEnumerable<IPAddress> DnsServersV4 => DnsServers.Where(IPv4.IsV4);
    [JsonIgnore] public bool IsVpnOrVirtual => Kind is AdapterKind.Vpn or AdapterKind.Virtual or AdapterKind.Loopback;

    public bool IsOnLink(IPAddress address) => UsableAddresses.Any(a => a.Subnet.Contains(address));

    public override string ToString() => $"{Name} (#{Index}, {Kind})";
}

/// <summary>One entry of the IPv4 routing table.</summary>
public sealed record RouteEntry(IPv4Prefix Destination, IPAddress NextHop, int InterfaceIndex, int RouteMetric, string Protocol = "")
{
    [JsonIgnore] public bool IsOnLink => IPv4.IsUnspecified(NextHop);
    [JsonIgnore] public RouteKey Key => new(Destination, NextHop.ToString(), InterfaceIndex);
    public override string ToString() => $"{Destination} via {(IsOnLink ? "on-link" : NextHop)} if {InterfaceIndex} metric {RouteMetric}";
}

/// <summary>Identity of a route as Windows sees it: destination + next hop + interface.</summary>
public readonly record struct RouteKey(IPv4Prefix Destination, string NextHop, int InterfaceIndex)
{
    public override string ToString() => $"{Destination} via {NextHop} if {InterfaceIndex}";
}

/// <summary>An NRPT (Name Resolution Policy Table) rule as read from Windows.</summary>
public sealed record NrptRule(string Name, string Namespace, IReadOnlyList<string> NameServers, string? Comment)
{
    [JsonIgnore] public bool IsOurs => Comment?.StartsWith(NrptTag.Comment, StringComparison.OrdinalIgnoreCase) == true;
}

public static class NrptTag
{
    /// <summary>Comment written on every NRPT rule we create so leftovers can always be identified.</summary>
    public const string Comment = "DualNetwork";
}

/// <summary>A point-in-time view of the machine's IPv4 networking.</summary>
public sealed record NetworkState
{
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.Now;
    public IReadOnlyList<AdapterInfo> Adapters { get; init; } = Array.Empty<AdapterInfo>();
    public IReadOnlyList<RouteEntry> Routes { get; init; } = Array.Empty<RouteEntry>();
    /// <summary>Null when NRPT was not read for this capture.</summary>
    public IReadOnlyList<NrptRule>? NrptRules { get; init; }

    public AdapterInfo? Adapter(int index) => Adapters.FirstOrDefault(a => a.Index == index);

    public int InterfaceMetric(int index) => Adapter(index)?.InterfaceMetric ?? 0;

    public int EffectiveMetric(RouteEntry r) => r.RouteMetric + InterfaceMetric(r.InterfaceIndex);
}

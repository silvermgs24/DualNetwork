using System.Net;
using DualNetwork.Core.Model;
using DualNetwork.Core.Net;

namespace DualNetwork.Core.Routing;

/// <summary>
/// Simulates Windows' IPv4 route selection: longest prefix first, then the lowest
/// route metric + interface metric. Routes on adapters that are down are ignored.
/// Used to predict the effect of a plan before anything is changed.
/// </summary>
public static class RouteTable
{
    public static RouteEntry? Lookup(IPAddress destination, IEnumerable<RouteEntry> routes, NetworkState state) =>
        Lookup(IPv4.ToUInt(destination), routes, state);

    public static RouteEntry? Lookup(uint destination, IEnumerable<RouteEntry> routes, NetworkState state)
    {
        RouteEntry? best = null;
        var bestMetric = int.MaxValue;
        foreach (var r in routes)
        {
            if (!r.Destination.Contains(destination)) continue;
            var adapter = state.Adapter(r.InterfaceIndex);
            if (adapter is { IsUp: false }) continue;
            var metric = r.RouteMetric + (adapter?.InterfaceMetric ?? 0);
            if (best is null
                || r.Destination.Length > best.Destination.Length
                || (r.Destination.Length == best.Destination.Length && metric < bestMetric))
            {
                best = r;
                bestMetric = metric;
            }
        }
        return best;
    }

    /// <summary>The route Windows would use for a typical public destination.</summary>
    public static RouteEntry? InternetRoute(IEnumerable<RouteEntry> routes, NetworkState state)
    {
        // Probe both halves of the address space so a /1 override on either side is noticed.
        var low = Lookup(IPv4.ToUInt(IPAddress.Parse("8.8.8.8")), routes, state);
        var high = Lookup(IPv4.ToUInt(IPAddress.Parse("142.250.0.1")), routes, state);
        return low ?? high;
    }

    /// <summary>Routes that are part of the per-interface plumbing Windows creates automatically.</summary>
    public static bool IsSystemRoute(RouteEntry r, AdapterInfo? adapter)
    {
        if (IPv4Prefix.Multicast.Contains(r.Destination) || IPv4Prefix.Loopback.Contains(r.Destination)) return true;
        if (r.Destination.IsHost && IPv4.ToUInt(r.Destination.NetworkAddress) == uint.MaxValue) return true; // 255.255.255.255
        if (adapter is null) return false;
        foreach (var a in adapter.Addresses)
        {
            if (r.Destination == a.Subnet && r.IsOnLink) return true;                           // connected subnet
            if (r.Destination == IPv4Prefix.Host(a.Address)) return true;                        // own address
            if (r.Destination.IsHost && r.Destination.Network == a.Subnet.Broadcast) return true; // subnet broadcast
        }
        return false;
    }
}

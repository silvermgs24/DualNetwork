using System.Net;
using System.Text.Json.Serialization;
using DualNetwork.Core.Model;
using DualNetwork.Core.Net;
using DualNetwork.Core.Planning;

namespace DualNetwork.Core.Routing;

[Flags]
public enum PersistentRouteSource
{
    /// <summary>Legacy store written by "route -p add" (HKLM\...\Tcpip\Parameters\PersistentRoutes).</summary>
    RouteExe = 1,
    /// <summary>Modern persistent store (New-NetRoute -PolicyStore PersistentStore, netsh).</summary>
    PersistentStore = 2,
}

/// <summary>
/// A route saved permanently in Windows: it applies on every network and survives restarts.
/// Dual Network never creates these; it shows them and lets the user remove or convert them.
/// </summary>
public sealed record PersistentRoute(
    IPv4Prefix Destination,
    [property: JsonConverter(typeof(IPAddressJsonConverter))] IPAddress NextHop,
    int Metric,
    PersistentRouteSource Source,
    int? InterfaceIndex = null,
    string? InterfaceAlias = null)
{
    [JsonIgnore] public string Key => $"{Destination}|{NextHop}";

    /// <summary>The command that recreates it, for the log and the undo list.</summary>
    public string ToRouteExe() =>
        $"route -p add {Destination.NetworkAddress} mask {Destination.MaskAddress} {NextHop} metric {Math.Max(1, Metric)}";

    public override string ToString() => $"{Destination} → {NextHop}";

    /// <summary>Parses a value name of the legacy registry store, e.g. "10.0.0.0,255.255.0.0,10.20.30.254,1".</summary>
    public static PersistentRoute? TryParseLegacy(string valueName)
    {
        var parts = valueName.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length < 3) return null;
        if (!IPAddress.TryParse(parts[0], out var dest) || !IPAddress.TryParse(parts[1], out var mask) || !IPAddress.TryParse(parts[2], out var gw)) return null;
        if (!IPv4.IsV4(dest) || !IPv4.IsV4(mask) || !IPv4.IsV4(gw)) return null;
        IPv4Prefix prefix;
        try { prefix = IPv4Prefix.FromAddressAndMask(dest, mask); }
        catch (FormatException) { return null; }
        var metric = parts.Length > 3 && int.TryParse(parts[3], out var m) ? m : 1;
        return new PersistentRoute(prefix, gw, metric, PersistentRouteSource.RouteExe);
    }

    /// <summary>Merges the two stores: the same destination + next hop in both becomes one entry.</summary>
    public static IReadOnlyList<PersistentRoute> Merge(IEnumerable<PersistentRoute> legacy, IEnumerable<PersistentRoute> store) =>
        legacy.Concat(store)
            .GroupBy(r => r.Key)
            .Select(g => g.Aggregate((a, b) => a with
            {
                Source = a.Source | b.Source,
                InterfaceIndex = a.InterfaceIndex ?? b.InterfaceIndex,
                InterfaceAlias = a.InterfaceAlias ?? b.InterfaceAlias,
            }))
            .OrderBy(r => r.Destination)
            .ToList();
}

/// <summary>What a permanent route means on the network you are connected to right now.</summary>
public sealed record PersistentRouteStatus(PersistentRoute Route, bool Active, string? ViaAdapter, Severity Severity, IReadOnlyList<string> Notes)
{
    /// <summary>Converting makes sense for internal networks: Dual Network then routes them only while plugged into a client.</summary>
    public bool CanConvert => Route.Destination.Length >= 8 && Route.Destination.Length < 32 && !Route.Destination.IsDefault;
}

public static class PersistentRouteAnalyzer
{
    public static IReadOnlyList<PersistentRouteStatus> Analyze(IEnumerable<PersistentRoute> routes, NetworkState state, RoutingPlan? plan)
    {
        var list = new List<PersistentRouteStatus>();
        foreach (var r in routes)
        {
            var notes = new List<string>();
            var active = state.Routes.Any(x => x.Destination == r.Destination && x.NextHop.Equals(r.NextHop));
            var adapter = state.Adapters.FirstOrDefault(a => a.IsUp && a.IsOnLink(r.NextHop));
            var severity = Severity.Info;

            if (r.Destination.IsDefault)
            {
                notes.Add("This is a permanent default route: it competes with every network's own gateway for Internet traffic.");
                severity = Severity.Warning;
            }

            if (adapter is null)
                notes.Add($"Gateway {r.NextHop} is not on any network you are connected to, so this route does nothing here. " +
                          "It becomes active again whenever a network with that gateway is connected.");
            else
            {
                notes.Add($"Gateway {r.NextHop} is on {adapter.Name}: traffic for {r.Destination} goes there on this network.");
                if (plan?.InternetAdapter is { } wifi && wifi.Index != adapter.Index && wifi.Subnets.Any(s => s.Overlaps(r.Destination)))
                {
                    notes.Add($"It overlaps the {wifi.Name} network ({string.Join(", ", wifi.Subnets.Where(s => s.Overlaps(r.Destination)))}), which can break that network.");
                    severity = Severity.Warning;
                }
            }

            if (adapter is not null && plan?.ClientAdapter?.Index == adapter.Index)
                notes.Add("Dual Network can do this for you only while you are plugged into a client: use Convert.");
            else if (adapter is not null && plan?.InternetAdapter?.Index == adapter.Index && !r.Destination.IsDefault)
                notes.Add($"It sends {r.Destination} through your Internet adapter.");

            if (r.Source == PersistentRouteSource.RouteExe)
                notes.Add("Created with \"route -p\".");

            list.Add(new PersistentRouteStatus(r, active, adapter?.Name, severity, notes));
        }
        return list;
    }
}

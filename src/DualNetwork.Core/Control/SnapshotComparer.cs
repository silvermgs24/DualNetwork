using DualNetwork.Core.Model;
using DualNetwork.Core.Routing;

namespace DualNetwork.Core.Control;

/// <summary>Describes how the current state differs from a snapshot (used to verify a restore).</summary>
public static class SnapshotComparer
{
    public static IReadOnlyList<string> Compare(NetworkState before, NetworkState after, IEnumerable<RouteKey>? ignore = null)
    {
        var diffs = new List<string>();
        var ignored = (ignore ?? Array.Empty<RouteKey>()).ToHashSet();

        bool Relevant(RouteEntry r, NetworkState s) =>
            !ignored.Contains(r.Key) && !RouteTable.IsSystemRoute(r, s.Adapter(r.InterfaceIndex));

        var beforeRoutes = before.Routes.Where(r => Relevant(r, before)).GroupBy(r => r.Key).ToDictionary(g => g.Key, g => g.First());
        var afterRoutes = after.Routes.Where(r => Relevant(r, after)).GroupBy(r => r.Key).ToDictionary(g => g.Key, g => g.First());

        foreach (var (k, r) in beforeRoutes)
        {
            if (!afterRoutes.TryGetValue(k, out var a))
                diffs.Add($"Route no longer present: {Describe(r, before)}");
            else if (a.RouteMetric != r.RouteMetric)
                diffs.Add($"Route metric changed: {Describe(r, before)} {r.RouteMetric} → {a.RouteMetric}");
        }
        foreach (var (k, r) in afterRoutes)
            if (!beforeRoutes.ContainsKey(k))
                diffs.Add($"New route (not created by Dual Network): {Describe(r, after)}");

        foreach (var b in before.Adapters)
        {
            var a = after.Adapter(b.Index);
            if (a is null) { diffs.Add($"{b.Name}: adapter no longer present"); continue; }
            if (a.IsUp != b.IsUp) diffs.Add($"{b.Name}: {(b.IsUp ? "was connected, now disconnected" : "was disconnected, now connected")}");
            if (a.InterfaceMetric != b.InterfaceMetric) diffs.Add($"{b.Name}: interface metric {b.InterfaceMetric} → {a.InterfaceMetric}");
            var ba = string.Join(", ", b.Addresses); var aa = string.Join(", ", a.Addresses);
            if (ba != aa) diffs.Add($"{b.Name}: IP address {Or(ba)} → {Or(aa)} (DHCP/network change)");
            var bg = string.Join(", ", b.Gateways); var ag = string.Join(", ", a.Gateways);
            if (bg != ag) diffs.Add($"{b.Name}: gateway {Or(bg)} → {Or(ag)}");
            var bd = string.Join(", ", b.DnsServers); var ad = string.Join(", ", a.DnsServers);
            if (bd != ad) diffs.Add($"{b.Name}: DNS servers {Or(bd)} → {Or(ad)}");
        }

        if (before.NrptRules is not null && after.NrptRules is not null)
        {
            var bn = before.NrptRules.Where(r => !r.IsOurs).Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var an = after.NrptRules.Where(r => !r.IsOurs).Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var r in before.NrptRules.Where(r => !r.IsOurs && !an.Contains(r.Name))) diffs.Add($"NRPT rule no longer present: {r.Namespace}");
            foreach (var r in after.NrptRules.Where(r => !r.IsOurs && !bn.Contains(r.Name))) diffs.Add($"New NRPT rule (not created by Dual Network): {r.Namespace}");
            foreach (var r in after.NrptRules.Where(r => r.IsOurs)) diffs.Add($"Dual Network NRPT rule still present: {r.Namespace}");
        }
        return diffs;
    }

    private static string Or(string s) => s.Length == 0 ? "none" : s;

    private static string Describe(RouteEntry r, NetworkState s) =>
        $"{r.Destination} via {(r.IsOnLink ? "on-link" : r.NextHop.ToString())} on {s.Adapter(r.InterfaceIndex)?.Name ?? $"if {r.InterfaceIndex}"}";
}

using DualNetwork.Core.Model;

namespace DualNetwork.Core.Control;

/// <summary>Turns two consecutive network states into human-readable events for the log.</summary>
public static class NetworkChangeDescriber
{
    public static IReadOnlyList<string> Describe(NetworkState? before, NetworkState after)
    {
        var events = new List<string>();
        if (before is null) return events;

        foreach (var a in after.Adapters.Where(Interesting))
        {
            var b = before.Adapter(a.Index);
            var label = Label(a);
            if (b is null || (!b.IsUp && a.IsUp))
            {
                if (a.IsUp) events.Add($"{label} connected{Net(a)}");
                continue;
            }
            if (b.IsUp && !a.IsUp) { events.Add($"{label} disconnected"); continue; }
            if (!a.IsUp) continue;

            if (Net(b) != Net(a)) events.Add($"{label} changed:{Net(b)} →{Net(a)}");
            else if (b.Internet != a.Internet && a.Internet != InternetStatus.Unknown && b.Internet != InternetStatus.Unknown)
                events.Add($"{label}: Internet {(a.Internet == InternetStatus.Available ? "available" : "not available")}");
            if (!b.DnsServers.SequenceEqual(a.DnsServers))
                events.Add($"{label} DNS servers changed: {Join(b.DnsServers)} → {Join(a.DnsServers)}");
        }
        foreach (var b in before.Adapters.Where(Interesting).Where(b => b.IsUp && after.Adapter(b.Index) is null))
            events.Add($"{Label(b)} removed");
        return events;
    }

    private static bool Interesting(AdapterInfo a) => a.Kind != AdapterKind.Loopback;

    private static string Label(AdapterInfo a) => a.Kind == AdapterKind.Vpn ? $"VPN {a.Name} ({a.Product})" : a.Name;

    private static string Net(AdapterInfo a) =>
        a.Addresses.Count == 0 ? "" : $" {string.Join(", ", a.Addresses)} (gateway {a.Gateway?.ToString() ?? "none"})";

    private static string Join<T>(IEnumerable<T> items) { var s = string.Join(", ", items); return s.Length == 0 ? "none" : s; }
}

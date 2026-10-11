using DualNetwork.Core.Model;
using DualNetwork.Core.Planning;

namespace DualNetwork.App;

public sealed record DetailLine(string Label, string Value);

public sealed class AdapterViewModel
{
    public required string Name { get; init; }
    public required string KindText { get; init; }
    public string? Role { get; init; }
    public required string StatusLine { get; init; }
    public required string Address { get; init; }
    public required Severity Severity { get; init; }
    public required IReadOnlyList<DetailLine> Details { get; init; }
    public string Description { get; init; } = "";

    public static AdapterViewModel From(AdapterInfo a, RoutingPlan? plan)
    {
        var isInternet = plan?.InternetAdapter?.Index == a.Index;
        var isClient = plan?.ClientAdapter?.Index == a.Index;
        var role = isInternet ? "Internet" : isClient ? "Client Network" : a.Kind switch
        {
            AdapterKind.Vpn => "VPN",
            AdapterKind.Virtual => "Virtual",
            _ => null,
        };

        var kind = a.Kind switch
        {
            AdapterKind.WiFi => "Wi-Fi",
            AdapterKind.Ethernet => "Ethernet",
            AdapterKind.Vpn => $"VPN · {a.Product}",
            AdapterKind.Virtual => $"Virtual · {a.Product}",
            AdapterKind.Cellular => "Mobile broadband",
            AdapterKind.Loopback => "Loopback",
            _ => "Other",
        };

        var parts = new List<string> { a.IsUp ? "Connected" : "Disconnected" };
        Severity sev;
        if (!a.IsUp)
            sev = isInternet || isClient ? Severity.Problem : Severity.Info;
        else if (a.HasOnlyLinkLocal)
        {
            parts.Add("No address from the network (DHCP failed?)");
            sev = Severity.Warning;
        }
        else if (!a.HasUsableIPv4)
        {
            parts.Add("No IPv4 address");
            sev = Severity.Warning;
        }
        else
        {
            sev = Severity.Good;
            if (isClient) parts.Add("Client Network");
            var internet = a.Internet switch
            {
                InternetStatus.Available => "Internet",
                InternetStatus.NotAvailable => "No Internet",
                // VPN tunnels and gateway-less adapters aren't probed individually.
                _ => a.IsVpnOrVirtual || a.Gateway is null ? null : "Internet: checking…",
            };
            if (internet is not null) parts.Add(internet);
            if (isInternet && a.Internet == InternetStatus.NotAvailable) sev = Severity.Warning;
        }

        var details = new List<DetailLine>();
        if (a.Addresses.Count > 0)
        {
            details.Add(new DetailLine("IPv4", string.Join(", ", a.Addresses.Select(x => x.Address))));
            details.Add(new DetailLine("Subnet", string.Join(", ", a.Addresses.Select(x => $"{x.Subnet} ({x.Subnet.MaskAddress})"))));
        }
        if (a.IsUp)
        {
            details.Add(new DetailLine("Gateway", a.Gateway?.ToString() ?? "None"));
            details.Add(new DetailLine("DNS", a.DnsServers.Count == 0 ? "None" : string.Join(", ", a.DnsServers)));
            if (a.DnsSuffix is not null) details.Add(new DetailLine("DNS suffix", a.DnsSuffix));
            details.Add(new DetailLine("Metric", $"{a.InterfaceMetric}{(a.AutomaticMetric ? " (automatic)" : " (manual)")}"));
            var type = new List<string> { a.DhcpEnabled ? "DHCP" : "Static" };
            if (a.SpeedBitsPerSecond > 0) type.Add(FormatSpeed(a.SpeedBitsPerSecond));
            if (a.HasIPv6DefaultRoute) type.Add("IPv6 default route");
            details.Add(new DetailLine("Network", string.Join(" · ", type)));
        }

        return new AdapterViewModel
        {
            Name = a.Name,
            KindText = kind,
            Role = role,
            StatusLine = string.Join(" · ", parts),
            Address = a.PrimaryAddress?.Address.ToString() ?? "",
            Severity = sev,
            Details = details,
            Description = a.Description,
        };
    }

    private static string FormatSpeed(long bps) => bps >= 1_000_000_000 ? $"{bps / 1_000_000_000.0:0.#} Gbps" : $"{bps / 1_000_000} Mbps";
}

public sealed record RoutingLine(string Label, string Target, Severity Severity, string? Hint = null);

public sealed record Notice(Severity Severity, string Text);

public sealed record DnsGroup(string Title, string Servers, string? Note = null);

public sealed record StatusItem(Severity Severity, string Text);

public sealed record StatusSection(string Title, IReadOnlyList<StatusItem> Items);

public sealed record RouteRow(string Destination, string NextHop, string Interface, int Metric, int InterfaceMetric, string Protocol, bool IsOurs);

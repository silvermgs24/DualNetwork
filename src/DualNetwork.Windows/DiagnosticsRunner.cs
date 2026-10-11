using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using DualNetwork.Core.Model;
using DualNetwork.Core.Net;
using DualNetwork.Core.Planning;
using DualNetwork.Windows.Interop;

namespace DualNetwork.Windows;

public sealed record DiagnosticResult(string Name, Severity Status, string Detail);

/// <summary>"Run Network Test": active checks of links, gateways, internal reachability, Internet, DNS and the real routes.</summary>
public sealed class DiagnosticsRunner
{
    private readonly InternetProbe _probe;

    public DiagnosticsRunner(InternetProbe probe) => _probe = probe;

    public async Task<IReadOnlyList<DiagnosticResult>> RunAsync(NetworkState state, RoutingPlan plan, PlannerSettings settings, CancellationToken ct = default)
    {
        var results = new List<DiagnosticResult>();
        var client = plan.ClientAdapter;
        var wifi = plan.InternetAdapter;
        var internet = plan.EffectiveInternetAdapter ?? wifi;

        // 1. Links
        results.Add(LinkResult("Ethernet connectivity", client, "client network"));
        results.Add(LinkResult("Wi-Fi connectivity", wifi, "Internet"));

        // 2. Gateways
        if (client?.HasUsableIPv4 == true)
            results.Add(client.Gateway is null
                ? new DiagnosticResult("Ethernet gateway", Severity.Info, "No gateway: only the directly connected client network is reachable (normal for isolated networks)")
                : await PingResult("Ethernet gateway", client.Gateway, ct));
        if (wifi?.Gateway is not null)
            results.Add(await PingResult("Wi-Fi gateway", wifi.Gateway, ct));

        // 3. Internal reachability
        if (client?.HasUsableIPv4 == true)
            results.Add(await InternalReachability(client, ct));

        // 4. Internet
        results.Add(await InternetResult(ct));
        if (client?.HasUsableIPv4 == true && client.Gateway is not null)
        {
            _probe.Invalidate();
            var local = client.UsableAddresses.First().Address;
            var viaClient = await _probe.ProbeAsync(local, $"diag|{client.Index}|{local}", ct);
            results.Add(new DiagnosticResult($"Internet through {client.Name}", Severity.Info, viaClient == InternetStatus.Available
                ? "Available — the client network also provides Internet (you can choose the preferred adapter in Settings)"
                : "Not available (expected for an isolated client network)"));
        }

        // 5. DNS
        results.Add(await ResolveResult("DNS resolution (public)", "www.microsoft.com", Severity.Problem, ct));
        var domains = new List<string>();
        if (RoutingPlanner.NormalizeDomain(client?.DnsSuffix) is { } suffix) domains.Add(suffix);
        domains.AddRange(settings.ExtraInternalDomains.Select(RoutingPlanner.NormalizeDomain).Where(d => d is not null).Select(d => d!));
        foreach (var d in domains.Distinct())
            results.Add(await ResolveResult($"DNS resolution (internal: {d})", d, Severity.Warning, ct));
        if (domains.Count == 0 && client?.HasUsableIPv4 == true)
            results.Add(new DiagnosticResult("DNS resolution (internal)", Severity.Info, "No internal domain known — add one in Settings to test it"));

        // 6. Real routes (asked from Windows, not simulated)
        results.Add(RouteResult("Current default route (Internet)", IPAddress.Parse("8.8.8.8"), internet, state));
        if (client?.HasUsableIPv4 == true)
        {
            var target = client.Gateway ?? client.Subnets.First().FirstHost;
            results.Add(RouteResult($"Client network route ({client.Subnets.First()})", target, client, state));
        }

        // 7. VPNs
        foreach (var vpn in state.Adapters.Where(a => a.IsUp && a.Kind == AdapterKind.Vpn))
            results.Add(new DiagnosticResult($"VPN: {vpn.Name}", Severity.Info, $"{vpn.Product} is active — not modified by Dual Network"));

        return results;
    }

    private static DiagnosticResult LinkResult(string name, AdapterInfo? a, string role)
    {
        if (a is null) return new DiagnosticResult(name, Severity.Problem, $"No adapter found for the {role}");
        if (!a.IsUp) return new DiagnosticResult(name, Severity.Problem, $"{a.Name} is disconnected");
        if (a.HasOnlyLinkLocal) return new DiagnosticResult(name, Severity.Problem, $"{a.Name} only has a link-local address {a.PrimaryAddress} (DHCP failed?)");
        if (!a.HasUsableIPv4) return new DiagnosticResult(name, Severity.Problem, $"{a.Name} has no IPv4 address");
        return new DiagnosticResult(name, Severity.Good, $"{a.Name} connected · {string.Join(", ", a.UsableAddresses)}");
    }

    private static async Task<DiagnosticResult> PingResult(string name, IPAddress target, CancellationToken ct)
    {
        var rtt = await PingAsync(target, ct);
        return rtt is not null
            ? new DiagnosticResult(name, Severity.Good, $"{target} replied in {rtt} ms")
            : new DiagnosticResult(name, Severity.Warning, $"{target} did not reply to ping (ICMP may be blocked)");
    }

    private static async Task<long?> PingAsync(IPAddress target, CancellationToken ct)
    {
        using var ping = new Ping();
        for (var i = 0; i < 2; i++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var reply = await ping.SendPingAsync(target, 1000);
                if (reply.Status == IPStatus.Success) return reply.RoundtripTime;
            }
            catch (PingException) { }
        }
        return null;
    }

    private static async Task<DiagnosticResult> InternalReachability(AdapterInfo client, CancellationToken ct)
    {
        var reached = new List<string>();
        var tried = new List<string>();
        foreach (var dns in client.DnsServersV4.Distinct())
        {
            tried.Add($"DNS {dns}");
            if (await TcpAsync(dns, 53, ct) || await PingAsync(dns, ct) is not null) reached.Add($"DNS {dns}");
        }
        if (client.Gateway is not null)
        {
            tried.Add($"gateway {client.Gateway}");
            if (await PingAsync(client.Gateway, ct) is not null) reached.Add($"gateway {client.Gateway}");
        }
        if (tried.Count == 0)
            return new DiagnosticResult("Internal network reachability", Severity.Info,
                $"Connected to {string.Join(", ", client.Subnets)}; no gateway or DNS server to test against");
        return reached.Count > 0
            ? new DiagnosticResult("Internal network reachability", Severity.Good, $"Reached {string.Join(", ", reached)}")
            : new DiagnosticResult("Internal network reachability", Severity.Warning, $"No reply from {string.Join(", ", tried)} (may be filtered)");
    }

    private static async Task<bool> TcpAsync(IPAddress host, int port, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(1500);
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await s.ConnectAsync(new IPEndPoint(host, port), cts.Token);
            return true;
        }
        catch { return false; }
    }

    private static async Task<DiagnosticResult> InternetResult(CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
            var sw = Stopwatch.StartNew();
            var body = await http.GetStringAsync("http://www.msftconnecttest.com/connecttest.txt", ct);
            return body.Contains("Microsoft Connect Test")
                ? new DiagnosticResult("Internet connectivity", Severity.Good, $"Microsoft connectivity test passed in {sw.ElapsedMilliseconds} ms")
                : new DiagnosticResult("Internet connectivity", Severity.Warning, "Unexpected response — a captive portal (hotel/guest Wi-Fi login) may be in the way");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new DiagnosticResult("Internet connectivity", Severity.Problem, $"Failed: {ex.Message}");
        }
    }

    private static async Task<DiagnosticResult> ResolveResult(string name, string host, Severity onFail, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(5000);
            var sw = Stopwatch.StartNew();
            var addrs = await Dns.GetHostAddressesAsync(host, cts.Token);
            var v4 = addrs.Where(a => a.AddressFamily == AddressFamily.InterNetwork).Take(3).ToList();
            return new DiagnosticResult(name, Severity.Good, $"{host} → {string.Join(", ", v4.Count > 0 ? v4 : addrs.Take(2))} ({sw.ElapsedMilliseconds} ms)");
        }
        catch (Exception ex)
        {
            return new DiagnosticResult(name, onFail, $"{host} could not be resolved: {ex.Message}");
        }
    }

    private static DiagnosticResult RouteResult(string name, IPAddress destination, AdapterInfo? expected, NetworkState state)
    {
        var best = IpHelper.BestRoute(destination);
        if (best is null) return new DiagnosticResult(name, Severity.Problem, $"Windows has no route to {destination}");
        var via = state.Adapter(best.Value.InterfaceIndex)?.Name ?? $"interface {best.Value.InterfaceIndex}";
        var hop = IPv4.IsUnspecified(best.Value.NextHop) ? "on-link" : $"via {best.Value.NextHop}";
        var detail = $"{destination} → {via} ({best.Value.Prefix}, {hop})";
        if (expected is null) return new DiagnosticResult(name, Severity.Info, detail);
        return best.Value.InterfaceIndex == expected.Index
            ? new DiagnosticResult(name, Severity.Good, detail)
            : new DiagnosticResult(name, Severity.Problem, $"{detail} — expected {expected.Name}");
    }
}

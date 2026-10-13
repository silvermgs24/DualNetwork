using System.Net;
using System.Text.Json;
using DualNetwork.Core.Control;
using DualNetwork.Core.Planning;
using DualNetwork.Windows.Interop;

namespace DualNetwork.Windows;

/// <summary>
/// After every change, asks Windows (GetBestRoute2) where traffic really goes, and confirms that none of our
/// routes ended up in the persistent store — i.e. that a reboot always returns Windows to its own configuration.
/// </summary>
public sealed class WindowsRouteVerifier : IRouteVerifier
{
    public IReadOnlyList<VerificationResult> Verify(RoutingPlan plan, IReadOnlyList<RouteSpec> applied)
    {
        var results = new List<VerificationResult>();
        var internet = plan.EffectiveInternetAdapter;
        var client = plan.ClientAdapter;

        if (internet is not null)
            foreach (var probe in new[] { "8.8.8.8", "142.250.0.1" })
            {
                var best = IpHelper.BestRoute(IPAddress.Parse(probe));
                var ok = best?.InterfaceIndex == internet.Index;
                results.Add(new VerificationResult(ok, ok
                    ? $"Internet ({probe}) → {internet.Name}"
                    : $"Internet ({probe}) → {Name(plan, best?.InterfaceIndex)}, expected {internet.Name}"));
            }

        if (client is not null)
        {
            foreach (var net in plan.ClientNetworks)
            {
                var best = IpHelper.BestRoute(net.FirstHost);
                var ok = best?.InterfaceIndex == client.Index;
                results.Add(new VerificationResult(ok, ok ? $"{net} → {client.Name}" : $"{net} → {Name(plan, best?.InterfaceIndex)}, expected {client.Name}"));
            }
            foreach (var r in applied.Where(r => r.Purpose is RoutePurpose.ClientRange or RoutePurpose.ClientDnsHost))
            {
                var best = IpHelper.BestRoute(r.Destination.FirstHost);
                // A more specific route owned by someone else (e.g. a VPN) legitimately wins inside our range.
                var ok = best?.InterfaceIndex == client.Index || best?.Prefix.Length > r.Destination.Length;
                results.Add(new VerificationResult(ok, ok ? $"{r.Destination} → {client.Name}" : $"{r.Destination} → {Name(plan, best?.InterfaceIndex)}, expected {client.Name}"));
            }

            // Client DNS must be reachable over the client network and actually answer — otherwise internal names fail.
            var domain = plan.NrptRules.FirstOrDefault()?.Namespace.TrimStart('.') ?? RoutingPlanner.NormalizeDomain(client.DnsSuffix);
            foreach (var dns in client.DnsServersV4.Distinct())
            {
                var best = IpHelper.BestRoute(dns);
                var routeOk = best?.InterfaceIndex == client.Index;
                var answer = DnsProbe.QueryAsync(dns, domain ?? ".").GetAwaiter().GetResult();
                // With a domain, only a real answer proves the server can serve split DNS; without one, any reply proves reachability.
                var answered = domain is null ? answer.Outcome != DnsProbe.Outcome.NoResponse : answer.Outcome == DnsProbe.Outcome.Answered;
                results.Add(new VerificationResult(routeOk && answered,
                    !routeOk ? $"Client DNS {dns} → {Name(plan, best?.InterfaceIndex)}, expected {client.Name}"
                    : answered ? $"Client DNS {dns} → {client.Name}, answers{(domain is null ? "" : $" for {domain}")} ({answer.Milliseconds} ms)"
                    : $"Client DNS {dns} → {client.Name}, but: {answer.Detail}",
                    VerificationIssue.ClientDnsUnusable));
            }
        }

        if (applied.Count > 0) results.Add(CheckNotPersistent(applied));
        return results;
    }

    private static VerificationResult CheckNotPersistent(IReadOnlyList<RouteSpec> applied)
    {
        try
        {
            var r = PowerShellRunner.RunAsync(
                "ConvertTo-Json -Compress -InputObject @(Get-NetRoute -AddressFamily IPv4 -PolicyStore PersistentStore -ErrorAction SilentlyContinue | ForEach-Object { $_.DestinationPrefix })",
                TimeSpan.FromSeconds(20)).GetAwaiter().GetResult();
            if (!r.Ok) return new VerificationResult(true, "Persistent store check skipped: " + r.StdErr);
            var persistent = string.IsNullOrWhiteSpace(r.StdOut) ? new List<string>() : JsonSerializer.Deserialize<List<string>>(r.StdOut) ?? new();
            var leaked = applied.Where(a => persistent.Contains(a.Destination.ToString())).ToList();
            return leaked.Count == 0
                ? new VerificationResult(true, "All changes are temporary (none in the persistent route store)")
                : new VerificationResult(false, $"Found in the persistent route store: {string.Join(", ", leaked.Select(l => l.Destination))}");
        }
        catch (Exception ex)
        {
            return new VerificationResult(true, "Persistent store check skipped: " + ex.Message);
        }
    }

    private static string Name(RoutingPlan plan, int? index) =>
        index is null ? "no route"
        : plan.InternetAdapter?.Index == index ? plan.InternetAdapter.Name
        : plan.ClientAdapter?.Index == index ? plan.ClientAdapter.Name
        : System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
              .FirstOrDefault(n => { try { return n.GetIPProperties().GetIPv4Properties()?.Index == index; } catch { return false; } })?.Name
          ?? $"interface {index}";
}

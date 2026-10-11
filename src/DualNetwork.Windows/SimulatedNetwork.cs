using System.Net;
using DualNetwork.Core.Control;
using DualNetwork.Core.Model;
using DualNetwork.Core.Net;
using DualNetwork.Core.Planning;
using DualNetwork.Core.Routing;

namespace DualNetwork.Windows;

/// <summary>
/// In-memory network used by "--demo": a laptop on hotel Wi-Fi, plugged into a client network whose gateway
/// has no Internet, with Tailscale running. Lets you try the whole flow without changing anything in Windows.
/// </summary>
public sealed class SimulatedNetwork : INetworkStateProvider, IRouteWriter, IDnsPolicyWriter, IStateStore, IRouteVerifier
{
    private const int WiFi = 12, Eth = 7, Vpn = 40;
    private NetworkState _state;
    private Journal _journal = new();
    private ConfigurationSnapshot? _snapshot;

    public SimulatedNetwork()
    {
        var adapters = new List<AdapterInfo>
        {
            new()
            {
                Index = WiFi, Name = "Wi-Fi", Description = "Intel(R) Wi-Fi 6E AX211 160MHz", Kind = AdapterKind.WiFi, IsUp = true,
                Addresses = new[] { new AdapterAddress(IPAddress.Parse("192.168.1.25"), 24) },
                Gateways = new[] { IPAddress.Parse("192.168.1.1") },
                DnsServers = new[] { IPAddress.Parse("192.168.1.1") },
                InterfaceMetric = 35, DhcpEnabled = true, Internet = InternetStatus.Available, SpeedBitsPerSecond = 866_000_000,
            },
            new()
            {
                Index = Eth, Name = "Ethernet", Description = "Realtek USB GbE Family Controller", Kind = AdapterKind.Ethernet, IsUp = true,
                Addresses = new[] { new AdapterAddress(IPAddress.Parse("10.25.30.50"), 24) },
                Gateways = new[] { IPAddress.Parse("10.25.30.1") },
                DnsServers = new[] { IPAddress.Parse("10.25.30.10"), IPAddress.Parse("10.25.30.11") },
                DnsSuffix = "client.local", InterfaceMetric = 25, DhcpEnabled = true, Internet = InternetStatus.NotAvailable,
                SpeedBitsPerSecond = 1_000_000_000,
            },
            new()
            {
                Index = Vpn, Name = "Tailscale", Description = "Tailscale Tunnel", Kind = AdapterKind.Vpn, Product = "Tailscale", IsUp = true,
                Addresses = new[] { new AdapterAddress(IPAddress.Parse("100.101.1.2"), 32) },
                InterfaceMetric = 5, Internet = InternetStatus.Unknown,
            },
        };
        var routes = new List<RouteEntry>();
        foreach (var a in adapters)
        {
            foreach (var addr in a.Addresses)
            {
                routes.Add(new RouteEntry(addr.Subnet, IPAddress.Any, a.Index, 256, "Local"));
                if (addr.PrefixLength < 32) routes.Add(new RouteEntry(IPv4Prefix.Host(addr.Address), IPAddress.Any, a.Index, 256, "Local"));
                if (addr.PrefixLength < 31) routes.Add(new RouteEntry(new IPv4Prefix(addr.Subnet.Broadcast, 32), IPAddress.Any, a.Index, 256, "Local"));
            }
            foreach (var gw in a.Gateways) routes.Add(new RouteEntry(IPv4Prefix.Default, gw, a.Index, 0, "NetMgmt"));
        }
        routes.Add(new RouteEntry(IPv4Prefix.Parse("100.64.0.0/10"), IPAddress.Any, Vpn, 5, "NetMgmt"));
        routes.Add(new RouteEntry(IPv4Prefix.Parse("172.31.0.0/16"), IPAddress.Parse("10.25.30.254"), Eth, 10, "NetMgmt")); // client static route
        _state = new NetworkState { Adapters = adapters, Routes = routes, NrptRules = Array.Empty<NrptRule>() };
    }

    public int? BestInterface(IPAddress destination) => RouteTable.Lookup(destination, _state.Routes, _state)?.InterfaceIndex;

    public IReadOnlyList<VerificationResult> Verify(RoutingPlan plan, IReadOnlyList<RouteSpec> applied)
    {
        var results = new List<VerificationResult>();
        if (plan.EffectiveInternetAdapter is { } inet)
        {
            var ok = BestInterface(IPAddress.Parse("8.8.8.8")) == inet.Index;
            results.Add(new VerificationResult(ok, $"Internet → {(ok ? inet.Name : "another adapter")} (simulated)"));
        }
        if (plan.ClientAdapter is { } client)
            foreach (var net in plan.ClientNetworks)
                results.Add(new VerificationResult(BestInterface(net.FirstHost) == client.Index, $"{net} → {client.Name} (simulated)"));
        if (applied.Count > 0) results.Add(new VerificationResult(true, "All changes are temporary (simulated)"));
        return results;
    }

    public Task<NetworkState> CaptureAsync(CaptureOptions options, CancellationToken ct = default) =>
        Task.FromResult(_state with { CapturedAt = DateTimeOffset.Now });

    public bool CanModify => true;

    public WriteOutcome AddRoute(RouteSpec spec, NetworkState current)
    {
        WindowsRouteWriter.Validate(spec, current);
        if (_state.Routes.Any(r => r.Key == spec.Key)) return WriteOutcome.AlreadyExists;
        _state = _state with { Routes = _state.Routes.Append(new RouteEntry(spec.Destination, spec.NextHop, spec.InterfaceIndex, spec.Metric, OurProtocol)).ToList() };
        return WriteOutcome.Created;
    }

    public WriteOutcome RemoveRoute(RouteSpec spec)
    {
        var n = _state.Routes.Count;
        _state = _state with { Routes = _state.Routes.Where(r => r.Key != spec.Key).ToList() };
        return _state.Routes.Count < n ? WriteOutcome.Removed : WriteOutcome.NotFound;
    }

    public Task<NrptRuleSpec> AddRuleAsync(NrptRuleSpec spec, CancellationToken ct = default)
    {
        var name = Guid.NewGuid().ToString("B");
        _state = _state with { NrptRules = (_state.NrptRules ?? Array.Empty<NrptRule>()).Append(new NrptRule(name, spec.Namespace, spec.NameServers, NrptTag.Comment)).ToList() };
        return Task.FromResult(spec with { RuleName = name });
    }

    public Task<WriteOutcome> RemoveRuleAsync(NrptRuleSpec spec, CancellationToken ct = default)
    {
        var rules = _state.NrptRules ?? Array.Empty<NrptRule>();
        _state = _state with { NrptRules = rules.Where(r => r.Name != spec.RuleName).ToList() };
        return Task.FromResult(rules.Any(r => r.Name == spec.RuleName) ? WriteOutcome.Removed : WriteOutcome.NotFound);
    }

    public Task<IReadOnlyList<NrptRule>> ListRulesAsync(CancellationToken ct = default) =>
        Task.FromResult(_state.NrptRules ?? (IReadOnlyList<NrptRule>)Array.Empty<NrptRule>());

    public Task FlushCacheAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Journal LoadJournal() => _journal;
    public void SaveJournal(Journal journal) => _journal = journal;
    public string SaveSnapshot(ConfigurationSnapshot snapshot) { _snapshot = snapshot; return "(demo snapshot in memory)"; }
    public ConfigurationSnapshot? LoadSnapshot(string path) => _snapshot;

    private List<RouteEntry> _unpluggedRoutes = new();

    /// <summary>Demo helper: unplug / replug the client cable. Like Windows, unplugging drops every route on the adapter.</summary>
    public void ToggleEthernet()
    {
        var eth = _state.Adapter(Eth)!;
        if (eth.IsUp)
        {
            _unpluggedRoutes = _state.Routes.Where(r => r.InterfaceIndex == Eth && r.Protocol != OurProtocol).ToList();
            _state = _state with
            {
                Adapters = _state.Adapters.Select(a => a.Index == Eth ? a with { IsUp = false } : a).ToList(),
                Routes = _state.Routes.Where(r => r.InterfaceIndex != Eth).ToList(),
            };
        }
        else
        {
            _state = _state with
            {
                Adapters = _state.Adapters.Select(a => a.Index == Eth ? a with { IsUp = true } : a).ToList(),
                Routes = _state.Routes.Concat(_unpluggedRoutes).ToList(),
            };
        }
    }

    private const string OurProtocol = "NetMgmt (Dual Network)";
}

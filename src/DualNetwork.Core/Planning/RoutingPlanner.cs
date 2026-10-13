using System.Net;
using System.Text.RegularExpressions;
using DualNetwork.Core.Model;
using DualNetwork.Core.Net;
using DualNetwork.Core.Routing;

namespace DualNetwork.Core.Planning;

/// <summary>Changes this application already made (from the journal). They are ignored when analysing the system.</summary>
public sealed record OwnedChanges(IReadOnlyCollection<RouteKey> Routes, IReadOnlyCollection<string> NrptRuleNames)
{
    public static OwnedChanges None { get; } = new(Array.Empty<RouteKey>(), Array.Empty<string>());
}

/// <summary>
/// Pure decision engine: given a snapshot of the system and the user's settings, decide the minimal set
/// of additive changes that keeps client traffic on Ethernet and Internet traffic on Wi-Fi.
/// See docs/DESIGN.md for the reasoning behind every rule.
/// </summary>
public static class RoutingPlanner
{
    public static RoutingPlan Build(NetworkState state, PlannerSettings settings, OwnedChanges? owned = null) =>
        new Builder(state, settings, owned ?? OwnedChanges.None).Build();

    private static readonly Regex DomainPattern =
        new(@"^(?=.{1,253}$)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)(\.[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)*$", RegexOptions.Compiled);

    /// <summary>Normalises "Corp.Local." / ".corp.local" to "corp.local"; null when invalid.</summary>
    public static string? NormalizeDomain(string? domain)
    {
        if (string.IsNullOrWhiteSpace(domain)) return null;
        var d = domain.Trim().Trim('.').ToLowerInvariant();
        return DomainPattern.IsMatch(d) ? d : null;
    }

    private sealed class Builder
    {
        private readonly NetworkState _state;
        private readonly PlannerSettings _settings;
        private readonly OwnedChanges _owned;
        private readonly List<RouteEntry> _foreign;          // routes not created by us
        private readonly List<string> _trace = new();
        private readonly List<PlanNote> _warnings = new();
        private readonly List<string> _refusals = new();
        private readonly List<string> _preserved = new();
        private readonly List<RouteSpec> _routes = new();
        private readonly List<NrptRuleSpec> _nrpt = new();

        public Builder(NetworkState state, PlannerSettings settings, OwnedChanges owned)
        {
            _state = state;
            _settings = settings;
            _owned = owned;
            var ownedKeys = owned.Routes.ToHashSet();
            _foreign = state.Routes.Where(r => !ownedKeys.Contains(r.Key)).ToList();
        }

        private bool IsVpnIf(int index) => _state.Adapter(index) is { Kind: AdapterKind.Vpn };
        private string NameOf(int index) => _state.Adapter(index)?.Name ?? $"interface {index}";

        public RoutingPlan Build()
        {
            foreach (var vpn in _state.Adapters.Where(a => a.IsUp && a.Kind == AdapterKind.Vpn))
            {
                _trace.Add($"VPN adapter detected: {vpn.Name} ({vpn.Product}) — its routes will not be modified");
                _warnings.Add(new PlanNote(Severity.Info, $"VPN active: {vpn.Name} ({vpn.Product}). Its routes are left untouched."));
            }

            var internet = PickInternetAdapter();
            var client = PickClientAdapter(internet);
            if (internet is null || client is null || _refusals.Count > 0)
                return Finish(internet, client, null, false);

            if (internet.Index == client.Index)
            {
                _refusals.Add("The Internet adapter and the client adapter are the same adapter.");
                return Finish(internet, client, null, false);
            }

            _trace.Add($"Internet adapter: {internet.Name} — {Describe(internet)}");
            _trace.Add($"Client adapter: {client.Name} — {Describe(client)}");

            if (internet.Gateway is null)
                _refusals.Add($"{internet.Name} has no IPv4 default gateway, so there is no Internet path to route to.");

            foreach (var cs in client.Subnets)
            foreach (var ws in internet.Subnets)
                if (cs.Overlaps(ws))
                    _refusals.Add($"The client network {cs} ({client.Name}) overlaps the {internet.Name} network {ws}. " +
                                  "Destinations would be ambiguous, so routing is not changed automatically.");

            if (internet.Internet == InternetStatus.NotAvailable && client.Internet == InternetStatus.Available)
                _refusals.Add($"{internet.Name} has no Internet access but {client.Name} does. There is nothing to fix: " +
                              $"{client.Name} is the working Internet path.");
            else if (internet.Internet == InternetStatus.NotAvailable)
                _warnings.Add(new PlanNote(Severity.Warning, $"{internet.Name} currently has no Internet access. Internet traffic will still be kept on it."));

            var bothInternet = internet.Internet == InternetStatus.Available && client.Internet == InternetStatus.Available;
            var effective = internet;
            if (bothInternet)
            {
                _warnings.Add(new PlanNote(Severity.Warning, $"Both {internet.Name} and {client.Name} have Internet access. " +
                    $"Preferred Internet adapter: {(_settings.InternetPreference == InternetPreference.ClientNetwork ? client.Name : internet.Name)} (change it in Settings)."));
                if (_settings.InternetPreference == InternetPreference.ClientNetwork) effective = client;
            }

            CheckVpnsAndForeignOverrides();
            if (_refusals.Count > 0) return Finish(internet, client, effective, bothInternet);

            var clientNetworks = client.Subnets.ToList();
            PreserveClientRoutes(client);

            var needOverride = PlanDefaultRoute(effective, effective == internet ? client : internet);
            if (_refusals.Count > 0) return Finish(internet, client, effective, bothInternet);

            if (needOverride && effective == internet)
                PlanClientRanges(client, internet);

            PlanDnsHostRoutes(client, internet, effective);
            PlanNrpt(client);

            if (client.HasIPv6DefaultRoute && effective == internet)
                _warnings.Add(new PlanNote(Severity.Warning,
                    $"{client.Name} advertises an IPv6 default route. This version manages IPv4 only; IPv6-first apps may pause briefly before falling back to IPv4 over {internet.Name}."));

            return Finish(internet, client, effective, bothInternet, clientNetworks);
        }

        // ---------------------------------------------------------------- roles

        private AdapterInfo? PickInternetAdapter()
        {
            if (!string.IsNullOrWhiteSpace(_settings.InternetAdapterName))
            {
                var a = _state.Adapters.FirstOrDefault(x => x.Name.Equals(_settings.InternetAdapterName, StringComparison.OrdinalIgnoreCase));
                if (a is null || !a.IsUp) { _refusals.Add($"The configured Internet adapter '{_settings.InternetAdapterName}' is not connected."); return null; }
                if (a.Kind == AdapterKind.Vpn) { _refusals.Add($"'{a.Name}' is a VPN adapter and cannot be used as the Internet adapter."); return null; }
                if (!a.HasUsableIPv4) { _refusals.Add($"The configured Internet adapter '{a.Name}' has no usable IPv4 address."); return a; }
                _trace.Add($"Internet adapter set manually in Settings: {a.Name}");
                return a;
            }

            var wifi = _state.Adapters.Where(a => a.Kind == AdapterKind.WiFi && a.HasUsableIPv4).ToList();
            var withGw = wifi.Where(a => a.Gateway is not null).OrderBy(a => a.InterfaceMetric).ToList();
            if (withGw.Count == 0)
            {
                _refusals.Add(wifi.Count > 0
                    ? $"{wifi[0].Name} is connected but has no default gateway, so it cannot provide Internet."
                    : "No connected Wi-Fi adapter with an IPv4 address was found. Connect to Wi-Fi first (or pick an Internet adapter in Settings).");
                return wifi.FirstOrDefault();
            }
            if (withGw.Count > 1)
                _warnings.Add(new PlanNote(Severity.Warning, $"Several Wi-Fi adapters are connected; using {withGw[0].Name}. You can choose another in Settings."));
            return withGw[0];
        }

        private AdapterInfo? PickClientAdapter(AdapterInfo? internet)
        {
            if (!string.IsNullOrWhiteSpace(_settings.ClientAdapterName))
            {
                var a = _state.Adapters.FirstOrDefault(x => x.Name.Equals(_settings.ClientAdapterName, StringComparison.OrdinalIgnoreCase));
                if (a is null || !a.IsUp) { _refusals.Add($"The configured client adapter '{_settings.ClientAdapterName}' is not connected."); return null; }
                if (a.Kind == AdapterKind.Vpn) { _refusals.Add($"'{a.Name}' is a VPN adapter and cannot be used as the client adapter."); return null; }
                if (a.HasOnlyLinkLocal) { _refusals.Add(LinkLocalReason(a)); return a; }
                if (!a.HasUsableIPv4) { _refusals.Add($"The configured client adapter '{a.Name}' has no IPv4 address."); return a; }
                _trace.Add($"Client adapter set manually in Settings: {a.Name}");
                return a;
            }

            var eth = _state.Adapters.Where(a => a.Kind == AdapterKind.Ethernet && a.IsUp && a.Index != internet?.Index).ToList();
            var usable = eth.Where(a => a.HasUsableIPv4)
                .OrderBy(a => a.Internet == InternetStatus.Available ? 1 : 0) // an Ethernet without Internet is the likely client network
                .ThenBy(a => a.InterfaceMetric)
                .ToList();
            if (usable.Count == 0)
            {
                var apipa = eth.FirstOrDefault(a => a.HasOnlyLinkLocal);
                _refusals.Add(apipa is not null
                    ? LinkLocalReason(apipa)
                    : "No connected Ethernet adapter with an IPv4 address was found. Plug in the client network cable (or pick a client adapter in Settings).");
                return apipa;
            }
            if (usable.Count > 1)
                _warnings.Add(new PlanNote(Severity.Warning, $"Several Ethernet adapters are connected; using {usable[0].Name} as the client network. You can choose another in Settings."));
            return usable[0];
        }

        private static string LinkLocalReason(AdapterInfo a) =>
            $"{a.Name} only has a link-local address ({a.PrimaryAddress?.Address}). The client network did not hand out an address (DHCP failed?), so there is no client network to route to.";

        private static string Describe(AdapterInfo a) =>
            $"{string.Join(", ", a.UsableAddresses)}; gateway {a.Gateway?.ToString() ?? "none"}; " +
            $"interface metric {a.InterfaceMetric}; DNS {(a.DnsServers.Count == 0 ? "none" : string.Join(", ", a.DnsServers))}; Internet {a.Internet}";

        // ---------------------------------------------------------------- VPNs / other tools

        private void CheckVpnsAndForeignOverrides()
        {
            foreach (var r in _foreign.Where(r => r.Destination.Length <= 1))
            {
                var adapter = _state.Adapter(r.InterfaceIndex);
                if (adapter is not { IsUp: true }) continue;
                if (adapter.Kind == AdapterKind.Vpn)
                    _refusals.Add($"Full-tunnel VPN active: {adapter.Name} ({adapter.Product}) carries the default route ({r.Destination}). " +
                                  "Dual Network Mode will not override a VPN, because that would send traffic outside the tunnel.");
                else if (r.Destination.Length == 1)
                    _refusals.Add($"Another program has already installed a split default route ({r.Destination} via {r.NextHop} on {adapter.Name}). " +
                                  "Dual Network Mode will not compete with it.");
            }
        }

        // ---------------------------------------------------------------- client routes

        private void PreserveClientRoutes(AdapterInfo client)
        {
            foreach (var s in client.Subnets)
            {
                var onLink = _foreign.Any(r => r.InterfaceIndex == client.Index && r.Destination == s);
                if (onLink) _preserved.Add($"{s} — directly connected on {client.Name} (left as is)");
                else _warnings.Add(new PlanNote(Severity.Warning, $"No on-link route for {s} was found on {client.Name}; Windows normally creates one automatically."));
            }

            foreach (var r in _foreign.Where(r => r.InterfaceIndex == client.Index && !r.Destination.IsDefault))
            {
                if (RouteTable.IsSystemRoute(r, client)) continue;
                _preserved.Add($"{r.Destination} via {(r.IsOnLink ? "on-link" : r.NextHop.ToString())} — existing client route (left as is)");
            }
        }

        // ---------------------------------------------------------------- default route

        /// <returns>True when a /1 override pair is added.</returns>
        private bool PlanDefaultRoute(AdapterInfo effective, AdapterInfo other)
        {
            var defaults = _foreign.Where(r => r.Destination.IsDefault && _state.Adapter(r.InterfaceIndex) is { IsUp: true }).ToList();
            foreach (var d in defaults)
                _trace.Add($"Default route 0.0.0.0/0 via {d.NextHop} on {NameOf(d.InterfaceIndex)}: route metric {d.RouteMetric} + interface metric {_state.InterfaceMetric(d.InterfaceIndex)} = {_state.EffectiveMetric(d)}");

            var gw = effective.Gateway;
            if (gw is null)
            {
                _refusals.Add($"{effective.Name} has no IPv4 gateway to send Internet traffic to.");
                return false;
            }
            if (!defaults.Any(d => d.InterfaceIndex == effective.Index))
            {
                _refusals.Add($"{effective.Name} has a gateway ({gw}) but no default route. Its default routes may have been disabled by a VPN or by policy; not overriding that automatically.");
                return false;
            }

            var competing = defaults.Where(d => d.InterfaceIndex != effective.Index).ToList();
            var winner = RouteTable.InternetRoute(_foreign, _state);
            var winnerName = winner is null ? "nothing" : NameOf(winner.InterfaceIndex);
            _trace.Add($"Internet traffic currently uses: {winnerName}");

            var needOverride = competing.Count > 0 || winner?.InterfaceIndex != effective.Index;
            if (!needOverride)
            {
                _trace.Add($"Only {effective.Name} has a default route — no route override needed.");
                return false;
            }

            string reason;
            if (competing.Count > 0)
            {
                var c = competing.OrderBy(_state.EffectiveMetric).First();
                var ours = defaults.Where(d => d.InterfaceIndex == effective.Index).Min(_state.EffectiveMetric);
                var currently = winner?.InterfaceIndex == effective.Index
                    ? $"{effective.Name} wins today (metric {ours} vs {_state.EffectiveMetric(c)}), but DHCP renewals or dead-gateway detection can flip this"
                    : $"Windows currently prefers {NameOf(c.InterfaceIndex)} (metric {_state.EffectiveMetric(c)} vs {ours})";
                reason = $"{NameOf(c.InterfaceIndex)} also has a default route via {c.NextHop}. {currently}.";
                foreach (var d in competing)
                    _preserved.Add($"0.0.0.0/0 via {d.NextHop} on {NameOf(d.InterfaceIndex)} — kept, but no longer used for Internet traffic");
            }
            else
            {
                reason = $"Internet traffic currently goes to {winnerName}.";
            }

            foreach (var half in new[] { IPv4Prefix.LowerHalf, IPv4Prefix.UpperHalf })
                _routes.Add(new RouteSpec(half, gw, effective.Index, effective.Name, _settings.RouteMetric, RoutePurpose.InternetOverride,
                    $"{reason} Two /1 routes via {effective.Name} are more specific than any default route, so Internet traffic always uses {effective.Name}."));
            _trace.Add($"Plan: Internet override 0.0.0.0/1 + 128.0.0.0/1 via {gw} on {effective.Name}");
            return true;
        }

        // ---------------------------------------------------------------- client ranges

        private void PlanClientRanges(AdapterInfo client, AdapterInfo internet)
        {
            var gw = client.Gateway;
            if (gw is null)
            {
                _trace.Add($"{client.Name} has no gateway: only its directly connected network(s) are reachable, which Windows already routes.");
                return;
            }

            var candidates = new List<(IPv4Prefix Prefix, string Source)>();
            if (_settings.RouteAllPrivateBlocks)
                candidates.AddRange(IPv4Prefix.PrivateBlocks.Select(p => (p, "private address range")));
            else if (_settings.RouteContainingPrivateBlock)
                foreach (var a in client.UsableAddresses)
                foreach (var block in IPv4Prefix.PrivateBlocks.Where(b => b.Contains(a.Address)))
                    candidates.Add((block, $"private range containing the client address {a.Address}"));

            foreach (var text in _settings.ExtraClientPrefixes.Where(t => !string.IsNullOrWhiteSpace(t)))
            {
                if (!IPv4Prefix.TryParse(text, out var p))
                    _warnings.Add(new PlanNote(Severity.Warning, $"Ignored additional client network '{text}': not a valid IPv4 prefix."));
                else if (p.Length < 8)
                    _warnings.Add(new PlanNote(Severity.Warning, $"Ignored additional client network {p}: too broad (shorter than /8)."));
                else
                    candidates.Add((p, "additional client network from Settings"));
            }

            foreach (var (p, source) in candidates.DistinctBy(c => c.Prefix))
            {
                if (client.Subnets.Any(s => s.Contains(p))) { _trace.Add($"{p}: already directly connected on {client.Name}"); continue; }

                var wifiNet = internet.Subnets.Where(w => w.Overlaps(p) && w.Length <= p.Length).Cast<IPv4Prefix?>().FirstOrDefault();
                if (wifiNet is not null)
                {
                    _warnings.Add(new PlanNote(Severity.Warning, $"Skipped client network {p}: it is part of the {internet.Name} network {wifiNet}."));
                    continue;
                }

                // A VPN (e.g. a Tailscale subnet route) may claim the same addresses. While plugged into the client network,
                // the client's own ranges take priority by default: a broader VPN route loses on prefix length anyway, and an
                // identical one is beaten by splitting the range in two more specific halves. Narrower VPN routes keep working.
                var targets = new List<IPv4Prefix> { p };
                var vpn = _foreign.FirstOrDefault(r => IsVpnIf(r.InterfaceIndex) && r.Destination.Overlaps(p) && r.Destination.Length <= p.Length);
                if (vpn is not null)
                {
                    if (!_settings.ClientNetworkWinsOverVpn)
                    {
                        _warnings.Add(new PlanNote(Severity.Warning, $"Skipped client network {p}: the VPN {NameOf(vpn.InterfaceIndex)} routes {vpn.Destination} (Settings: VPN has priority)."));
                        continue;
                    }
                    targets = SplitPastVpnRoutes(p);
                    _warnings.Add(new PlanNote(Severity.Warning,
                        $"The VPN {NameOf(vpn.InterfaceIndex)} also routes {vpn.Destination}. While you are on the client network, {p} goes to {client.Name}; " +
                        "the VPN keeps its other routes (change this in Settings)."));
                }
                foreach (var inner in _foreign.Where(r => IsVpnIf(r.InterfaceIndex) && p.Contains(r.Destination) && r.Destination.Length > p.Length && r.Destination != p))
                    _trace.Add($"{inner.Destination} stays on the VPN {NameOf(inner.InterfaceIndex)} (more specific than {p})");

                foreach (var t in targets)
                {
                    var same = _foreign.Where(r => r.Destination == t && !IsVpnIf(r.InterfaceIndex)).ToList();
                    if (same.Any(r => r.InterfaceIndex == client.Index))
                    {
                        _trace.Add($"{t}: the client network already routes it on {client.Name}");
                        continue;
                    }
                    if (same.Count > 0)
                    {
                        _warnings.Add(new PlanNote(Severity.Warning, $"Skipped client network {t}: a route for it already exists on {NameOf(same[0].InterfaceIndex)}."));
                        continue;
                    }

                    foreach (var w in internet.Subnets.Where(t.Contains))
                        _trace.Add($"{w} stays on {internet.Name} (more specific than {t})");

                    _routes.Add(new RouteSpec(t, gw, client.Index, client.Name, _settings.RouteMetric, RoutePurpose.ClientRange,
                        $"Internal destinations in {t} ({source}) were reached through the client's default route. Keep them on {client.Name} via {gw}."));
                    _trace.Add($"Plan: client range {t} via {gw} on {client.Name}");
                }
            }
        }

        /// <summary>Splits <paramref name="p"/> into halves until no piece is identical to a VPN route (max 4 levels).</summary>
        private List<IPv4Prefix> SplitPastVpnRoutes(IPv4Prefix p)
        {
            var result = new List<IPv4Prefix>();
            var pending = new Stack<IPv4Prefix>();
            pending.Push(p);
            while (pending.Count > 0)
            {
                var x = pending.Pop();
                var identical = _foreign.Any(r => IsVpnIf(r.InterfaceIndex) && r.Destination == x);
                if (!identical || x.Length >= 32 || x.Length - p.Length >= 4) { result.Add(x); continue; }
                var half = x.Length + 1;
                pending.Push(new IPv4Prefix(x.Network | (1u << (32 - half)), half));
                pending.Push(new IPv4Prefix(x.Network, half));
            }
            return result;
        }

        // ---------------------------------------------------------------- DNS reachability

        private void PlanDnsHostRoutes(AdapterInfo client, AdapterInfo internet, AdapterInfo effective)
        {
            var simulated = _foreign.Concat(_routes.Select(r => new RouteEntry(r.Destination, r.NextHop, r.InterfaceIndex, r.Metric))).ToList();

            foreach (var dns in client.DnsServersV4.Distinct())
            {
                if (client.IsOnLink(dns)) continue;
                var hit = RouteTable.Lookup(dns, simulated, _state);
                if (hit?.InterfaceIndex == client.Index) { _trace.Add($"Client DNS {dns} → {client.Name} (already routed)"); continue; }
                // The client DNS addresses come from the client's own DHCP, so they always belong on the client network —
                // even when a VPN route (e.g. a Tailscale subnet route) happens to cover them.
                if (hit is not null && IsVpnIf(hit.InterfaceIndex))
                    _trace.Add($"Client DNS {dns} currently routes into the VPN {NameOf(hit.InterfaceIndex)}; pinning it to {client.Name}");
                if (client.Gateway is null)
                {
                    _warnings.Add(new PlanNote(Severity.Warning, $"Client DNS server {dns} is outside the client network and {client.Name} has no gateway, so it may be unreachable."));
                    continue;
                }
                AddHostRoute(dns, client, client.Gateway, RoutePurpose.ClientDnsHost,
                    $"Client DNS server {dns} is outside the directly connected network; keep it reachable over {client.Name}.", simulated);
            }

            if (effective != internet || internet.Gateway is null) return;
            foreach (var dns in internet.DnsServersV4.Distinct())
            {
                if (internet.IsOnLink(dns)) continue;
                var hit = RouteTable.Lookup(dns, simulated, _state);
                if (hit is null || hit.InterfaceIndex == internet.Index || IsVpnIf(hit.InterfaceIndex)) continue;
                AddHostRoute(dns, internet, internet.Gateway, RoutePurpose.InternetDnsHost,
                    $"{internet.Name} DNS server {dns} falls inside a range routed to {NameOf(hit.InterfaceIndex)}; keep it on {internet.Name}.", simulated);
            }
        }

        private void AddHostRoute(IPAddress dns, AdapterInfo adapter, IPAddress gw, RoutePurpose purpose, string reason, List<RouteEntry> simulated)
        {
            var host = IPv4Prefix.Host(dns);
            if (_foreign.Any(r => r.Destination == host))
            {
                _trace.Add($"{host}: a host route already exists; left as is");
                return;
            }
            var spec = new RouteSpec(host, gw, adapter.Index, adapter.Name, _settings.RouteMetric, purpose, reason);
            _routes.Add(spec);
            simulated.Add(new RouteEntry(host, gw, adapter.Index, spec.Metric));
            _trace.Add($"Plan: host route {host} via {gw} on {adapter.Name}");
        }

        // ---------------------------------------------------------------- NRPT

        private void PlanNrpt(AdapterInfo client)
        {
            if (_settings.SplitDnsDisabledReason is { } why)
            {
                _warnings.Add(new PlanNote(Severity.Warning, $"Split DNS is off for this network: {why} Windows' normal DNS is used instead."));
                return;
            }
            if (!_settings.ManageDns) { _trace.Add("DNS management is turned off in Settings."); return; }

            var servers = client.DnsServersV4.Distinct().Select(s => s.ToString()).ToList();
            if (servers.Count == 0)
            {
                _trace.Add($"{client.Name} provides no IPv4 DNS servers; DNS is left to Windows.");
                return;
            }

            var domains = new List<string>();
            var fromClient = NormalizeDomain(client.DnsSuffix);
            if (fromClient is not null) domains.Add(fromClient);
            foreach (var d in _settings.ExtraInternalDomains)
            {
                var n = NormalizeDomain(d);
                if (n is null) { if (!string.IsNullOrWhiteSpace(d)) _warnings.Add(new PlanNote(Severity.Warning, $"Ignored internal domain '{d}': not a valid DNS name.")); }
                else domains.Add(n);
            }

            if (domains.Count == 0)
            {
                _warnings.Add(new PlanNote(Severity.Info,
                    $"{client.Name} did not provide a DNS suffix. Windows queries the DNS servers of all adapters, so internal names usually still resolve. " +
                    "For guaranteed split DNS, add the client's internal domain(s) in Settings."));
                return;
            }

            var ownedNames = _owned.NrptRuleNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var d in domains.Distinct())
            {
                var ns = "." + d;
                var existing = _state.NrptRules?.FirstOrDefault(r =>
                    r.Namespace.Trim().Equals(ns, StringComparison.OrdinalIgnoreCase) && !r.IsOurs && !ownedNames.Contains(r.Name));
                if (existing is not null)
                {
                    _preserved.Add($"NRPT rule for {ns} → {string.Join(", ", existing.NameServers)} — already exists (left as is)");
                    continue;
                }
                _nrpt.Add(new NrptRuleSpec(ns, servers,
                    $"Names under {d} are resolved by the client DNS ({string.Join(", ", servers)}); all other names keep using Windows' normal DNS."));
                _trace.Add($"Plan: NRPT {ns} → {string.Join(", ", servers)}");
            }
        }

        // ---------------------------------------------------------------- result

        private RoutingPlan Finish(AdapterInfo? internet, AdapterInfo? client, AdapterInfo? effective, bool bothInternet,
            IReadOnlyList<IPv4Prefix>? clientNetworks = null)
        {
            var refused = _refusals.Count > 0;
            if (refused)
            {
                _routes.Clear();
                _nrpt.Clear();
                foreach (var r in _refusals) _trace.Add("Refused: " + r);
            }

            var nets = clientNetworks ?? client?.Subnets.ToList() ?? new List<IPv4Prefix>();
            var summary = new List<string>();
            var expectations = new List<RoutingExpectation>();

            if (!refused && internet is not null && client is not null && effective is not null)
            {
                summary.Add($"Keep {client.Name} connected");
                summary.Add($"Keep {internet.Name} connected");
                summary.Add($"Internet traffic → {effective.Name}");
                var clientRanges = nets.Concat(_routes.Where(r => r.Purpose == RoutePurpose.ClientRange).Select(r => r.Destination)).ToList();
                summary.Add($"Client network traffic ({string.Join(", ", clientRanges)}) → {client.Name}");
                if (_preserved.Count > 0) summary.Add($"Existing client routes preserved ({_preserved.Count})");
                summary.Add(_nrpt.Count > 0
                    ? $"Internal DNS ({string.Join(", ", _nrpt.Select(n => n.Namespace.TrimStart('.')))}) → client DNS; other names → normal DNS"
                    : "DNS settings unchanged");
                if (!(_routes.Count > 0 || _nrpt.Count > 0))
                    summary.Add("No changes are needed right now. Dual Network Mode will watch the network and step in if that changes.");

                expectations.Add(new RoutingExpectation("Internet", effective.Name, Severity.Good));
                foreach (var p in clientRanges) expectations.Add(new RoutingExpectation(p.ToString(), client.Name, Severity.Good));
                if (_nrpt.Count > 0)
                    expectations.Add(new RoutingExpectation($"DNS *.{string.Join(", *.", _nrpt.Select(n => n.Namespace.TrimStart('.')))}", $"{client.Name} DNS", Severity.Good));
                expectations.Add(new RoutingExpectation("DNS (public names)", $"{effective.Name} DNS", Severity.Good));
            }

            return new RoutingPlan
            {
                Status = refused ? PlanStatus.Refused : (_routes.Count > 0 || _nrpt.Count > 0) ? PlanStatus.Ready : PlanStatus.NothingToDo,
                InternetAdapter = internet,
                ClientAdapter = client,
                EffectiveInternetAdapter = refused ? null : effective,
                BothHaveInternet = bothInternet,
                ClientNetworks = nets,
                Routes = _routes.ToList(),
                NrptRules = _nrpt.ToList(),
                Preserved = _preserved.ToList(),
                Warnings = _warnings.ToList(),
                RefusalReasons = _refusals.ToList(),
                Summary = summary,
                Expectations = expectations,
                Trace = _trace.ToList(),
            };
        }
    }
}

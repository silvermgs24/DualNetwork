using System.Net;
using DualNetwork.Core.Model;
using DualNetwork.Core.Net;
using DualNetwork.Core.Planning;
using DualNetwork.Core.Routing;
using static DualNetwork.Core.Tests.Scenario;

namespace DualNetwork.Core.Tests;

public class RoutingPlannerTests
{
    private static RoutingPlan Plan(NetworkState s, PlannerSettings? settings = null, OwnedChanges? owned = null) =>
        RoutingPlanner.Build(s, settings ?? new PlannerSettings(), owned);

    private static string[] Prefixes(RoutingPlan p) => p.Routes.Select(r => r.Destination.ToString()).ToArray();

    /// <summary>Applies a plan's routes to a state, as Windows would after enabling.</summary>
    private static List<RouteEntry> After(NetworkState s, RoutingPlan p) =>
        s.Routes.Concat(p.Routes.Select(r => new RouteEntry(r.Destination, r.NextHop, r.InterfaceIndex, r.Metric))).ToList();

    private static int? Via(NetworkState s, RoutingPlan p, string ip) =>
        RouteTable.Lookup(IPAddress.Parse(ip), After(s, p), s)?.InterfaceIndex;

    // ------------------------------------------------------------------ the two core cases from the spec

    [Fact]
    public void Ethernet_without_gateway_needs_no_route_changes()
    {
        var s = Create().WithWiFi().WithEthernet("10.25.30.50/24").Build();

        var p = Plan(s);

        Assert.Equal(PlanStatus.NothingToDo, p.Status);
        Assert.Empty(p.Routes);
        Assert.Equal(new[] { IPv4Prefix.Parse("10.25.30.0/24") }, p.ClientNetworks);
        Assert.Equal(Eth, Via(s, p, "10.25.30.99"));
        Assert.Equal(WiFi, Via(s, p, "8.8.8.8"));
        Assert.Contains(p.Summary, l => l.Contains("Internet traffic → Wi-Fi"));
    }

    [Fact]
    public void Ethernet_gateway_without_internet_gets_overridden_by_two_half_routes_via_wifi()
    {
        // Ethernet's default route wins today (lower metric) — the classic hijack.
        var s = Create().WithWiFi(metric: 35).WithEthernet("10.10.10.20/24", gateway: "10.10.10.1", metric: 25).Build();
        Assert.Equal(Eth, RouteTable.InternetRoute(s.Routes, s)!.InterfaceIndex);

        var p = Plan(s);

        Assert.Equal(PlanStatus.Ready, p.Status);
        var overrides = p.Routes.Where(r => r.Purpose == RoutePurpose.InternetOverride).ToList();
        Assert.Equal(new[] { "0.0.0.0/1", "128.0.0.0/1" }, overrides.Select(r => r.Destination.ToString()));
        Assert.All(overrides, r => { Assert.Equal(WiFi, r.InterfaceIndex); Assert.Equal("192.168.1.1", r.NextHop.ToString()); });

        Assert.Equal(WiFi, Via(s, p, "8.8.8.8"));
        Assert.Equal(WiFi, Via(s, p, "200.1.2.3"));
        Assert.Equal(Eth, Via(s, p, "10.10.10.99"));     // directly connected
        Assert.Equal(Eth, Via(s, p, "10.200.0.5"));      // other client subnet behind the client gateway
        Assert.Equal(WiFi, Via(s, p, "192.168.1.50"));   // home LAN stays on Wi-Fi
        Assert.Contains(p.Preserved, x => x.Contains("0.0.0.0/0 via 10.10.10.1"));
    }

    [Fact]
    public void Client_range_is_derived_from_the_ethernet_address_not_hardcoded()
    {
        var s172 = Create().WithWiFi().WithEthernet("172.20.5.9/24", gateway: "172.20.5.1").Build();
        Assert.Contains("172.16.0.0/12", Prefixes(Plan(s172)));
        Assert.DoesNotContain("10.0.0.0/8", Prefixes(Plan(s172)));

        // A client using public addresses internally: only the on-link subnet is known.
        var sPublic = Create().WithWiFi().WithEthernet("45.10.20.30/24", gateway: "45.10.20.1").Build();
        var p = Plan(sPublic);
        Assert.Equal(new[] { "0.0.0.0/1", "128.0.0.0/1" }, Prefixes(p));
        Assert.Equal(Eth, Via(sPublic, p, "45.10.20.77"));
    }

    [Fact]
    public void Extra_client_prefixes_from_settings_are_routed_via_client_gateway()
    {
        var s = Create().WithWiFi().WithEthernet("10.10.10.20/24", gateway: "10.10.10.1").Build();
        var settings = new PlannerSettings { ExtraClientPrefixes = { "100.64.10.0/24", "garbage", "0.0.0.0/1" } };

        var p = Plan(s, settings);

        Assert.Contains("100.64.10.0/24", Prefixes(p));
        Assert.Equal(Eth, Via(s, p, "100.64.10.4"));
        Assert.Contains(p.Warnings, w => w.Text.Contains("garbage"));
        Assert.Contains(p.Warnings, w => w.Text.Contains("too broad"));
    }

    [Fact]
    public void All_private_blocks_option_keeps_wifi_lan_on_wifi()
    {
        var s = Create().WithWiFi("192.168.1.25/24").WithEthernet("10.10.10.20/24", gateway: "10.10.10.1").Build();

        var p = Plan(s, new PlannerSettings { RouteAllPrivateBlocks = true });

        Assert.Contains("192.168.0.0/16", Prefixes(p));
        Assert.Equal(WiFi, Via(s, p, "192.168.1.1"));
        Assert.Equal(Eth, Via(s, p, "192.168.77.1"));
    }

    [Fact]
    public void Client_range_inside_wifi_subnet_is_skipped()
    {
        // Wi-Fi is a large 10.0.0.0/8 network: a client range inside it would steal Wi-Fi traffic.
        var s = Create().WithWiFi("10.1.2.3/8", gateway: "10.0.0.1").WithEthernet("172.16.5.5/24", gateway: "172.16.5.1").Build();

        var p = Plan(s, new PlannerSettings { ExtraClientPrefixes = { "10.50.0.0/16" } });

        Assert.DoesNotContain("10.50.0.0/16", Prefixes(p));
        Assert.Contains(p.Warnings, w => w.Text.Contains("10.50.0.0/16"));
    }

    // ------------------------------------------------------------------ preserving the client's routes

    [Fact]
    public void Existing_client_routes_are_preserved_and_not_duplicated()
    {
        var s = Create().WithWiFi().WithEthernet("10.10.10.20/24", gateway: "10.10.10.1")
            .Route("10.0.0.0/8", "10.10.10.1", Eth, 10)          // static/DHCP option 121 route
            .Route("172.31.0.0/16", "10.10.10.254", Eth, 10)     // another client route via a different router
            .Build();

        var p = Plan(s);

        Assert.DoesNotContain(p.Routes, r => r.Destination.ToString() == "10.0.0.0/8");
        Assert.Contains(p.Preserved, x => x.StartsWith("10.0.0.0/8"));
        Assert.Contains(p.Preserved, x => x.StartsWith("172.31.0.0/16"));
        Assert.Equal(Eth, Via(s, p, "172.31.4.4"));
        Assert.Equal(Eth, Via(s, p, "10.99.0.1"));
    }

    [Fact]
    public void Planner_never_emits_default_routes_or_routes_on_vpn_adapters()
    {
        var states = new[]
        {
            Create().WithWiFi().WithEthernet().Build(),
            Create().WithWiFi().WithEthernet("10.10.10.20/24", gateway: "10.10.10.1", dns: new[] { "10.9.9.9" }, suffix: "corp.local").Build(),
            Create().WithWiFi().WithEthernet("10.10.10.20/24", gateway: "10.10.10.1").WithVpn().Route("100.64.0.0/10", "0.0.0.0", Vpn).Build(),
        };
        foreach (var s in states)
        foreach (var settings in new[] { new PlannerSettings(), new PlannerSettings { RouteAllPrivateBlocks = true } })
        {
            var p = Plan(s, settings);
            Assert.All(p.Routes, r => Assert.True(r.Destination.Length >= 1));
            Assert.All(p.Routes, r => Assert.NotEqual(AdapterKind.Vpn, s.Adapter(r.InterfaceIndex)!.Kind));
        }
    }

    // ------------------------------------------------------------------ refusals

    [Fact]
    public void Overlapping_subnets_are_refused()
    {
        var s = Create().WithWiFi("192.168.1.25/24").WithEthernet("192.168.1.80/24", gateway: "192.168.1.254").Build();

        var p = Plan(s);

        Assert.Equal(PlanStatus.Refused, p.Status);
        Assert.Empty(p.Routes);
        Assert.Contains(p.RefusalReasons, r => r.Contains("overlaps"));
    }

    [Fact]
    public void Link_local_only_ethernet_is_refused()
    {
        var s = Create().WithWiFi().WithEthernet("169.254.33.10/16").Build();

        var p = Plan(s);

        Assert.Equal(PlanStatus.Refused, p.Status);
        Assert.Contains(p.RefusalReasons, r => r.Contains("link-local"));
    }

    [Fact]
    public void Missing_wifi_or_ethernet_is_refused()
    {
        Assert.Equal(PlanStatus.Refused, Plan(Create().WithEthernet().Build()).Status);
        Assert.Equal(PlanStatus.Refused, Plan(Create().WithWiFi().Build()).Status);
        Assert.Equal(PlanStatus.Refused, Plan(Create().WithWiFi(gateway: null).WithEthernet().Build()).Status);
    }

    [Fact]
    public void Full_tunnel_vpn_is_never_overridden()
    {
        var s = Create().WithWiFi().WithEthernet("10.10.10.20/24", gateway: "10.10.10.1")
            .WithVpn("10.200.0.5/32", "Cisco AnyConnect")
            .Route("0.0.0.0/0", "10.200.0.1", Vpn, 1)
            .Build();

        var p = Plan(s);

        Assert.Equal(PlanStatus.Refused, p.Status);
        Assert.Empty(p.Routes);
        Assert.Contains(p.RefusalReasons, r => r.Contains("Full-tunnel VPN"));
    }

    [Fact]
    public void Openvpn_style_half_routes_count_as_full_tunnel()
    {
        var s = Create().WithWiFi().WithEthernet("10.10.10.20/24", gateway: "10.10.10.1")
            .WithVpn("10.8.0.2/24", "OpenVPN")
            .Route("0.0.0.0/1", "10.8.0.1", Vpn).Route("128.0.0.0/1", "10.8.0.1", Vpn)
            .Build();

        Assert.Equal(PlanStatus.Refused, Plan(s).Status);
    }

    [Fact]
    public void Vpn_route_identical_to_client_range_is_beaten_by_two_halves_by_default()
    {
        var s = Create().WithWiFi().WithEthernet("10.10.10.20/24", gateway: "10.10.10.1")
            .WithVpn("100.101.1.2/32")
            .Route("10.0.0.0/8", "0.0.0.0", Vpn, 5)       // e.g. a Tailscale subnet route for all of 10/8
            .Route("10.200.0.0/16", "0.0.0.0", Vpn, 5)    // a narrower VPN route: must keep working
            .Build();

        var p = Plan(s);

        Assert.Equal(PlanStatus.Ready, p.Status);
        Assert.Contains("10.0.0.0/9", Prefixes(p));
        Assert.Contains("10.128.0.0/9", Prefixes(p));
        Assert.DoesNotContain("10.0.0.0/8", Prefixes(p));
        Assert.Equal(Eth, Via(s, p, "10.50.0.1"));        // client network wins for its range
        Assert.Equal(Vpn, Via(s, p, "10.200.1.1"));       // VPN keeps its more specific route
        Assert.Equal(Eth, Via(s, p, "10.10.10.5"));
        Assert.Equal(WiFi, Via(s, p, "8.8.8.8"));
        Assert.Contains(p.Warnings, w => w.Text.Contains("also routes 10.0.0.0/8"));
    }

    [Fact]
    public void Vpn_priority_can_be_kept_in_settings()
    {
        var s = Create().WithWiFi().WithEthernet("10.10.10.20/24", gateway: "10.10.10.1")
            .WithVpn("100.101.1.2/32")
            .Route("10.0.0.0/8", "0.0.0.0", Vpn, 5)
            .Build();

        var p = Plan(s, new PlannerSettings { ClientNetworkWinsOverVpn = false });

        Assert.DoesNotContain(p.Routes, r => r.Purpose == RoutePurpose.ClientRange);
        Assert.Equal(Vpn, Via(s, p, "10.50.0.1"));
    }

    /// <summary>
    /// Reproduces the first real site visit: Wi-Fi hotspot in 10.99.94.0/24, client Ethernet 10.20.30.130/24 (gateway .254,
    /// with Internet), internal servers and DNS in 10.0.x.x, split DNS for client-example.org, Tailscale routing 10.0.0.0/8.
    /// The user's manual fix was "route add 10.0.0.0 mask 255.255.0.0 10.20.30.254".
    /// </summary>
    [Fact]
    public void Site_visit_hotspot_in_10_slash_8_with_tailscale_reaches_internal_10_0_servers_and_dns()
    {
        var s = Create()
            .WithWiFi("10.99.94.68/24", gateway: "10.99.94.132", dns: new[] { "10.99.94.132" })
            .WithEthernet("10.20.30.130/24", gateway: "10.20.30.254", dns: new[] { "10.0.0.11", "10.0.0.12" },
                suffix: "client-example.org", internet: InternetStatus.Available)
            .WithVpn("100.88.1.2/32", "Tailscale")
            .Route("100.64.0.0/10", "0.0.0.0", Vpn, 5)
            .Route("10.0.0.0/8", "0.0.0.0", Vpn, 5)
            .Build();

        var p = Plan(s);

        Assert.Equal(PlanStatus.Ready, p.Status);
        Assert.Equal(WiFi, Via(s, p, "8.8.8.8"));          // Internet → Wi-Fi
        Assert.Equal(Eth, Via(s, p, "10.0.0.11"));         // client DNS reachable
        Assert.Equal(Eth, Via(s, p, "10.0.5.20"));         // internal servers (app.client-example.org) reachable
        Assert.Equal(Eth, Via(s, p, "10.20.30.10"));       // directly connected client subnet
        Assert.Equal(WiFi, Via(s, p, "10.99.94.132"));    // Wi-Fi gateway/DNS stays on Wi-Fi
        Assert.Equal(Vpn, Via(s, p, "100.100.100.100"));   // Tailscale itself untouched
        Assert.Equal(".client-example.org", Assert.Single(p.NrptRules).Namespace);
    }

    [Fact]
    public void Same_site_without_vpn_routes_the_whole_private_block()
    {
        var s = Create()
            .WithWiFi("10.99.94.68/24", gateway: "10.99.94.132", dns: new[] { "10.99.94.132" })
            .WithEthernet("10.20.30.130/24", gateway: "10.20.30.254", dns: new[] { "10.0.0.11" }, suffix: "client-example.org", internet: InternetStatus.Available)
            .Build();

        var p = Plan(s);

        Assert.Contains("10.0.0.0/8", Prefixes(p));
        Assert.Equal(Eth, Via(s, p, "10.0.5.20"));
        Assert.Equal(Eth, Via(s, p, "10.0.0.11"));
        Assert.Equal(WiFi, Via(s, p, "10.99.94.50"));
        Assert.Equal(WiFi, Via(s, p, "1.1.1.1"));
    }

    [Fact]
    public void Client_dns_inside_a_vpn_route_is_pinned_to_the_client_network()
    {
        var s = Create().WithWiFi().WithEthernet("10.10.10.20/24", gateway: "10.10.10.1", dns: new[] { "172.18.0.53" })
            .WithVpn("100.101.1.2/32")
            .Route("172.16.0.0/12", "0.0.0.0", Vpn, 5)
            .Build();

        var p = Plan(s);

        Assert.Contains("172.18.0.53/32", Prefixes(p));
        Assert.Equal(Eth, Via(s, p, "172.18.0.53"));
        Assert.Equal(Vpn, Via(s, p, "172.18.0.54"));       // the rest of that VPN range is untouched
    }

    [Fact]
    public void Extra_client_network_from_settings_wins_over_a_vpn_route()
    {
        var s = Create().WithWiFi().WithEthernet("10.20.30.130/24", gateway: "10.20.30.254")
            .WithVpn("100.101.1.2/32")
            .Route("10.0.0.0/16", "0.0.0.0", Vpn, 5)
            .Build();

        var p = Plan(s, new PlannerSettings { RouteContainingPrivateBlock = false, ExtraClientPrefixes = { "10.0.0.0/16" } });

        Assert.Equal(Eth, Via(s, p, "10.0.5.20"));
    }

    [Fact]
    public void Foreign_half_routes_from_another_tool_are_refused()
    {
        var s = Create().WithWiFi().WithEthernet("10.10.10.20/24", gateway: "10.10.10.1")
            .Route("0.0.0.0/1", "192.168.1.1", WiFi).Build();

        Assert.Equal(PlanStatus.Refused, Plan(s).Status);
    }

    [Fact]
    public void Wifi_without_internet_while_ethernet_has_it_is_refused()
    {
        var s = Create().WithWiFi(internet: InternetStatus.NotAvailable)
            .WithEthernet("10.10.10.20/24", gateway: "10.10.10.1", internet: InternetStatus.Available).Build();

        var p = Plan(s);

        Assert.Equal(PlanStatus.Refused, p.Status);
        Assert.Contains(p.RefusalReasons, r => r.Contains("nothing to fix"));
    }

    // ------------------------------------------------------------------ both have Internet

    [Fact]
    public void Both_with_internet_defaults_to_wifi_and_reports_choice()
    {
        var s = Create().WithWiFi().WithEthernet("10.10.10.20/24", gateway: "10.10.10.1", internet: InternetStatus.Available).Build();

        var p = Plan(s);

        Assert.True(p.BothHaveInternet);
        Assert.Equal(WiFi, p.EffectiveInternetAdapter!.Index);
        Assert.Equal(WiFi, Via(s, p, "8.8.8.8"));
    }

    [Fact]
    public void Both_with_internet_and_client_preference_routes_internet_via_ethernet()
    {
        // Wi-Fi currently wins on metric; user prefers the client network for Internet.
        var s = Create().WithWiFi(metric: 10).WithEthernet("10.10.10.20/24", gateway: "10.10.10.1", metric: 50, internet: InternetStatus.Available).Build();

        var p = Plan(s, new PlannerSettings { InternetPreference = InternetPreference.ClientNetwork });

        Assert.Equal(Eth, p.EffectiveInternetAdapter!.Index);
        Assert.DoesNotContain(p.Routes, r => r.Purpose == RoutePurpose.ClientRange);
        Assert.Equal(Eth, Via(s, p, "8.8.8.8"));
        Assert.Equal(WiFi, Via(s, p, "192.168.1.10"));
    }

    // ------------------------------------------------------------------ DNS

    [Fact]
    public void Client_dns_outside_client_subnet_gets_host_route_when_not_covered()
    {
        var s = Create().WithWiFi().WithEthernet("10.10.10.20/24", gateway: "10.10.10.1", dns: new[] { "172.18.0.53", "10.10.10.2" }).Build();

        // With the containing private block only (10/8), 172.18.0.53 is not covered.
        var p = Plan(s);

        var host = Assert.Single(p.Routes, r => r.Purpose == RoutePurpose.ClientDnsHost);
        Assert.Equal("172.18.0.53/32", host.Destination.ToString());
        Assert.Equal(Eth, Via(s, p, "172.18.0.53"));
    }

    [Fact]
    public void Client_dns_inside_routed_block_needs_no_host_route()
    {
        var s = Create().WithWiFi().WithEthernet("10.10.10.20/24", gateway: "10.10.10.1", dns: new[] { "10.0.0.10" }).Build();

        var p = Plan(s);

        Assert.DoesNotContain(p.Routes, r => r.Purpose == RoutePurpose.ClientDnsHost);
        Assert.Equal(Eth, Via(s, p, "10.0.0.10"));
    }

    [Fact]
    public void Wifi_dns_captured_by_client_range_is_pinned_to_wifi()
    {
        // Odd but real: the hotspot hands out a DNS server in 10/8 that is not on the Wi-Fi subnet.
        var s = Create().WithWiFi(dns: new[] { "10.255.0.1" }).WithEthernet("10.10.10.20/24", gateway: "10.10.10.1").Build();

        var p = Plan(s);

        var pin = Assert.Single(p.Routes, r => r.Purpose == RoutePurpose.InternetDnsHost);
        Assert.Equal("10.255.0.1/32", pin.Destination.ToString());
        Assert.Equal(WiFi, Via(s, p, "10.255.0.1"));
    }

    [Fact]
    public void Client_dns_unreachable_without_gateway_is_warned_not_routed()
    {
        var s = Create().WithWiFi().WithEthernet("10.25.30.50/24", dns: new[] { "10.99.0.53" }).Build();

        var p = Plan(s);

        Assert.Empty(p.Routes);
        Assert.Contains(p.Warnings, w => w.Text.Contains("10.99.0.53") && w.Text.Contains("no gateway"));
    }

    [Fact]
    public void Dns_suffix_creates_split_dns_rule_and_existing_rules_are_respected()
    {
        var s = Create().WithWiFi()
            .WithEthernet("10.10.10.20/24", gateway: "10.10.10.1", dns: new[] { "10.10.10.10", "10.10.10.11" }, suffix: "Client.Local.")
            .Nrpt(".lab.internal", "10.1.1.1")     // someone else's rule
            .Build();

        var p = Plan(s, new PlannerSettings { ExtraInternalDomains = { "lab.internal", "bad domain!" } });

        var rule = Assert.Single(p.NrptRules);
        Assert.Equal(".client.local", rule.Namespace);
        Assert.Equal(new[] { "10.10.10.10", "10.10.10.11" }, rule.NameServers);
        Assert.Contains(p.Preserved, x => x.Contains(".lab.internal"));
        Assert.Contains(p.Warnings, w => w.Text.Contains("bad domain!"));
    }

    [Fact]
    public void No_dns_suffix_means_no_nrpt_and_an_explanation()
    {
        var s = Create().WithWiFi().WithEthernet(dns: new[] { "10.25.30.1" }).Build();

        var p = Plan(s);

        Assert.Empty(p.NrptRules);
        Assert.Contains(p.Warnings, w => w.Text.Contains("did not provide a DNS suffix"));
    }

    [Fact]
    public void Dns_management_can_be_turned_off()
    {
        var s = Create().WithWiFi().WithEthernet(dns: new[] { "10.25.30.1" }, suffix: "corp.local").Build();

        Assert.Empty(Plan(s, new PlannerSettings { ManageDns = false }).NrptRules);
    }

    [Fact]
    public void Ipv6_default_route_on_client_network_is_warned()
    {
        var s = Create().WithWiFi().WithEthernet(ipv6Default: true).Build();

        Assert.Contains(Plan(s).Warnings, w => w.Text.Contains("IPv6"));
    }

    // ------------------------------------------------------------------ roles & reconciliation inputs

    [Fact]
    public void Owned_routes_are_ignored_so_replanning_is_stable()
    {
        var s0 = Create().WithWiFi().WithEthernet("10.10.10.20/24", gateway: "10.10.10.1").Build();
        var first = Plan(s0);

        // The routes we created are now in the table; without knowing they're ours we'd refuse (foreign /1).
        var s1 = s0 with { Routes = After(s0, first) };
        var owned = new OwnedChanges(first.Routes.Select(r => r.Key).ToList(), Array.Empty<string>());

        var second = Plan(s1, owned: owned);

        Assert.Equal(PlanStatus.Ready, second.Status);
        Assert.Equal(first.Fingerprint, second.Fingerprint);
        Assert.Equal(PlanStatus.Refused, Plan(s1).Status);
    }

    [Fact]
    public void Ethernet_without_internet_is_preferred_as_client_when_several_exist()
    {
        var s = Create().WithWiFi()
            .WithEthernet("192.168.50.10/24", gateway: "192.168.50.1", index: 3, name: "Dock Ethernet", internet: InternetStatus.Available, metric: 5)
            .WithEthernet("10.25.30.50/24", index: Eth, name: "Ethernet")
            .Build();

        var p = Plan(s);

        Assert.Equal(Eth, p.ClientAdapter!.Index);
        Assert.Contains(p.Warnings, w => w.Text.Contains("Several Ethernet"));
    }

    [Fact]
    public void Manual_role_override_allows_usb_tethering_as_internet()
    {
        var tether = new AdapterInfo
        {
            Index = 50, Name = "Ethernet 3", Kind = AdapterKind.Ethernet, IsUp = true, InterfaceMetric = 20,
            Addresses = new[] { new AdapterAddress(IPAddress.Parse("172.20.10.2"), 28) },
            Gateways = new[] { IPAddress.Parse("172.20.10.1") }, Internet = InternetStatus.Available,
        };
        var s = Create().WithEthernet("10.10.10.20/24", gateway: "10.10.10.1").WithAdapter(tether).Build();

        var p = Plan(s, new PlannerSettings { InternetAdapterName = "Ethernet 3", ClientAdapterName = "Ethernet" });

        Assert.Equal(PlanStatus.Ready, p.Status);
        Assert.Equal(50, Via(s, p, "8.8.8.8"));
        Assert.Equal(Eth, Via(s, p, "10.10.10.99"));
    }

    [Fact]
    public void Manual_role_pointing_at_vpn_is_refused()
    {
        var s = Create().WithWiFi().WithEthernet().WithVpn().Build();

        Assert.Equal(PlanStatus.Refused, Plan(s, new PlannerSettings { ClientAdapterName = "Tailscale" }).Status);
    }
}

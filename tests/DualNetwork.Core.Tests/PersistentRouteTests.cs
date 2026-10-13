using System.Net;
using DualNetwork.Core.Model;
using DualNetwork.Core.Net;
using DualNetwork.Core.Planning;
using DualNetwork.Core.Routing;
using static DualNetwork.Core.Tests.Scenario;

namespace DualNetwork.Core.Tests;

public class PersistentRouteTests
{
    [Fact]
    public void Parses_route_exe_registry_entries()
    {
        var r = PersistentRoute.TryParseLegacy("10.0.0.0,255.255.0.0,10.20.30.254,1")!;

        Assert.Equal("10.0.0.0/16", r.Destination.ToString());
        Assert.Equal("10.20.30.254", r.NextHop.ToString());
        Assert.Equal(1, r.Metric);
        Assert.Equal(PersistentRouteSource.RouteExe, r.Source);
        Assert.Equal("route -p add 10.0.0.0 mask 255.255.0.0 10.20.30.254 metric 1", r.ToRouteExe());
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("10.0.0.0,255.0.255.0,10.0.0.1,1")]   // non-contiguous mask
    [InlineData("10.0.0.0,255.255.0.0")]
    public void Rejects_malformed_registry_entries(string value) => Assert.Null(PersistentRoute.TryParseLegacy(value));

    [Fact]
    public void Merges_the_two_stores_into_one_entry_per_route()
    {
        var legacy = new[] { PersistentRoute.TryParseLegacy("10.0.0.0,255.255.0.0,10.20.30.254,1")! };
        var store = new[]
        {
            new PersistentRoute(IPv4Prefix.Parse("10.0.0.0/16"), IPAddress.Parse("10.20.30.254"), 1, PersistentRouteSource.PersistentStore, 7, "Ethernet"),
            new PersistentRoute(IPv4Prefix.Parse("172.16.0.0/12"), IPAddress.Parse("192.168.1.254"), 10, PersistentRouteSource.PersistentStore),
        };

        var merged = PersistentRoute.Merge(legacy, store);

        Assert.Equal(2, merged.Count);
        var both = merged.Single(r => r.Destination.ToString() == "10.0.0.0/16");
        Assert.Equal(PersistentRouteSource.RouteExe | PersistentRouteSource.PersistentStore, both.Source);
        Assert.Equal("Ethernet", both.InterfaceAlias);
    }

    [Fact]
    public void Analyzer_explains_active_inactive_and_conflicting_routes()
    {
        var state = Create().WithWiFi("10.99.94.68/24", gateway: "10.99.94.132")
            .WithEthernet("10.20.30.130/24", gateway: "10.20.30.254")
            .Route("10.0.0.0/16", "10.20.30.254", Eth, 1)
            .Build();
        var plan = RoutingPlanner.Build(state, new PlannerSettings());
        var routes = new[]
        {
            new PersistentRoute(IPv4Prefix.Parse("10.0.0.0/16"), IPAddress.Parse("10.20.30.254"), 1, PersistentRouteSource.RouteExe),   // active at this client
            new PersistentRoute(IPv4Prefix.Parse("192.168.50.0/24"), IPAddress.Parse("172.16.8.1"), 1, PersistentRouteSource.RouteExe), // from another site
            new PersistentRoute(IPv4Prefix.Parse("10.99.0.0/16"), IPAddress.Parse("10.20.30.254"), 1, PersistentRouteSource.RouteExe),  // swallows the Wi-Fi network
        };

        var s = PersistentRouteAnalyzer.Analyze(routes, state, plan);

        Assert.True(s[0].Active);
        Assert.Equal("Ethernet", s[0].ViaAdapter);
        Assert.Contains(s[0].Notes, n => n.Contains("Convert"));
        Assert.True(s[0].CanConvert);

        Assert.False(s[1].Active);
        Assert.Null(s[1].ViaAdapter);
        Assert.Contains(s[1].Notes, n => n.Contains("not on any network you are connected to"));

        Assert.Equal(Severity.Warning, s[2].Severity);
        Assert.Contains(s[2].Notes, n => n.Contains("overlaps the Wi-Fi network"));
    }

    [Fact]
    public void Permanent_default_route_is_flagged_and_not_convertible()
    {
        var state = Create().WithWiFi().WithEthernet().Build();
        var r = new PersistentRoute(IPv4Prefix.Default, IPAddress.Parse("10.25.30.1"), 1, PersistentRouteSource.RouteExe);

        var s = Assert.Single(PersistentRouteAnalyzer.Analyze(new[] { r }, state, null));

        Assert.Equal(Severity.Warning, s.Severity);
        Assert.False(s.CanConvert);
    }
}

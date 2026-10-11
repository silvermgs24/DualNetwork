using System.Net;
using System.Text.Json;
using DualNetwork.Core.Control;
using DualNetwork.Core.Model;
using DualNetwork.Core.Net;
using DualNetwork.Core.Planning;
using DualNetwork.Core.Routing;
using static DualNetwork.Core.Tests.Scenario;

namespace DualNetwork.Core.Tests;

public class IPv4PrefixTests
{
    [Theory]
    [InlineData("10.25.30.50/24", "10.25.30.0/24")]
    [InlineData("192.168.1.1/32", "192.168.1.1/32")]
    [InlineData("0.0.0.0/0", "0.0.0.0/0")]
    [InlineData("172.31.255.255/12", "172.16.0.0/12")]
    [InlineData("10.1.2.3", "10.1.2.3/32")]
    public void Parse_normalises(string input, string expected) => Assert.Equal(expected, IPv4Prefix.Parse(input).ToString());

    [Theory]
    [InlineData("10.1/8")]
    [InlineData("10.0.0.0/33")]
    [InlineData("fe80::1/64")]
    [InlineData("hello")]
    [InlineData("")]
    public void Parse_rejects_invalid(string input) => Assert.False(IPv4Prefix.TryParse(input, out _));

    [Fact]
    public void Mask_conversion_round_trips()
    {
        var p = IPv4Prefix.FromAddressAndMask(IPAddress.Parse("10.25.30.50"), IPAddress.Parse("255.255.255.0"));
        Assert.Equal("10.25.30.0/24", p.ToString());
        Assert.Equal("255.255.255.0", p.MaskAddress.ToString());
        Assert.Throws<FormatException>(() => IPv4Prefix.FromAddressAndMask(IPAddress.Any, IPAddress.Parse("255.0.255.0")));
    }

    [Fact]
    public void Containment_and_overlap()
    {
        var block = IPv4Prefix.Parse("10.0.0.0/8");
        Assert.True(block.Contains(IPv4Prefix.Parse("10.25.30.0/24")));
        Assert.False(IPv4Prefix.Parse("10.25.30.0/24").Contains(block));
        Assert.True(block.Overlaps(IPv4Prefix.Parse("10.25.30.0/24")));
        Assert.False(block.Overlaps(IPv4Prefix.Parse("11.0.0.0/8")));
        Assert.True(IPv4Prefix.LowerHalf.Contains(IPAddress.Parse("127.255.255.255")));
        Assert.True(IPv4Prefix.UpperHalf.Contains(IPAddress.Parse("128.0.0.0")));
        Assert.Equal("10.25.30.255", IPv4.FromUInt(IPv4Prefix.Parse("10.25.30.0/24").Broadcast).ToString());
    }

    [Fact]
    public void Json_round_trip()
    {
        var spec = new RouteSpec(IPv4Prefix.Parse("0.0.0.0/1"), IPAddress.Parse("192.168.1.1"), 12, "Wi-Fi", 5, RoutePurpose.InternetOverride, "why");
        var json = JsonSerializer.Serialize(spec, JsonDefaults.Options);
        var back = JsonSerializer.Deserialize<RouteSpec>(json, JsonDefaults.Options)!;
        Assert.Equal(spec.Key, back.Key);
        Assert.Contains("\"0.0.0.0/1\"", json);
    }
}

public class AdapterClassifierTests
{
    [Theory]
    [InlineData("Wi-Fi", "Intel(R) Wi-Fi 6 AX201 160MHz", "Wireless80211", AdapterKind.WiFi)]
    [InlineData("Ethernet", "Realtek USB GbE Family Controller", "Ethernet", AdapterKind.Ethernet)]
    [InlineData("Ethernet0", "vmxnet3 Ethernet Adapter", "Ethernet", AdapterKind.Ethernet)]   // VMware *guest* NIC is real Ethernet
    [InlineData("Tailscale", "Tailscale Tunnel", "Unknown", AdapterKind.Vpn)]
    [InlineData("Ethernet 2", "Cisco AnyConnect Secure Mobility Client Virtual Miniport Adapter for Windows x64", "Ethernet", AdapterKind.Vpn)]
    [InlineData("Ethernet 3", "PANGP Virtual Ethernet Adapter Secure", "Ethernet", AdapterKind.Vpn)]
    [InlineData("Ethernet 4", "Fortinet SSL VPN Virtual Ethernet Adapter", "Ethernet", AdapterKind.Vpn)]
    [InlineData("OpenVPN TAP", "TAP-Windows Adapter V9", "Ethernet", AdapterKind.Vpn)]
    [InlineData("wg0", "WireGuard Tunnel", "Unknown", AdapterKind.Vpn)]
    [InlineData("VMware Network Adapter VMnet8", "VMware Virtual Ethernet Adapter for VMnet8", "Ethernet", AdapterKind.Virtual)]
    [InlineData("vEthernet (Default Switch)", "Hyper-V Virtual Ethernet Adapter", "Ethernet", AdapterKind.Virtual)]
    [InlineData("vEthernet (WSL)", "Hyper-V Virtual Ethernet Adapter", "Ethernet", AdapterKind.Virtual)]
    [InlineData("Ethernet 5", "VirtualBox Host-Only Ethernet Adapter", "Ethernet", AdapterKind.Virtual)]
    [InlineData("Bluetooth Network Connection", "Bluetooth Device (Personal Area Network)", "Ethernet", AdapterKind.Virtual)]
    [InlineData("Loopback Pseudo-Interface 1", "Software Loopback Interface 1", "Loopback", AdapterKind.Loopback)]
    public void Classifies(string name, string description, string type, AdapterKind expected) =>
        Assert.Equal(expected, AdapterClassifier.Classify(name, description, type).Kind);
}

public class RouteTableTests
{
    [Fact]
    public void Longest_prefix_beats_metric()
    {
        var s = Create().WithWiFi(metric: 1).WithEthernet("10.10.10.20/24", gateway: "10.10.10.1", metric: 500)
            .Route("10.0.0.0/8", "10.10.10.1", Eth, 9000).Build();
        Assert.Equal(Eth, RouteTable.Lookup(IPAddress.Parse("10.99.1.1"), s.Routes, s)!.InterfaceIndex);
    }

    [Fact]
    public void Equal_prefix_uses_route_plus_interface_metric()
    {
        var s = Create().WithWiFi(metric: 35).WithEthernet("10.10.10.20/24", gateway: "10.10.10.1", metric: 25).Build();
        Assert.Equal(Eth, RouteTable.InternetRoute(s.Routes, s)!.InterfaceIndex);

        var s2 = Create().WithWiFi(metric: 20).WithEthernet("10.10.10.20/24", gateway: "10.10.10.1", metric: 25).Build();
        Assert.Equal(WiFi, RouteTable.InternetRoute(s2.Routes, s2)!.InterfaceIndex);
    }

    [Fact]
    public void Routes_on_disconnected_adapters_are_ignored()
    {
        var s = Create().WithWiFi(metric: 50).WithEthernet("10.10.10.20/24", gateway: "10.10.10.1", metric: 5).Build();
        s = s with { Adapters = s.Adapters.Select(a => a.Index == Eth ? a with { IsUp = false } : a).ToList() };
        Assert.Equal(WiFi, RouteTable.InternetRoute(s.Routes, s)!.InterfaceIndex);
    }
}

public class SnapshotComparerTests
{
    [Fact]
    public void Identical_states_have_no_differences()
    {
        var s = Create().WithWiFi().WithEthernet().Build();
        Assert.Empty(SnapshotComparer.Compare(s, s));
    }

    [Fact]
    public void Reports_dhcp_and_route_changes()
    {
        var before = Create().WithWiFi().WithEthernet("10.25.30.50/24").Route("172.31.0.0/16", "10.25.30.1", Eth).Build();
        var after = Create().WithWiFi().WithEthernet("10.25.31.7/24").Build();

        var diffs = SnapshotComparer.Compare(before, after);

        Assert.Contains(diffs, d => d.Contains("IP address"));
        Assert.Contains(diffs, d => d.Contains("172.31.0.0/16"));
    }
}

public class NetworkChangeDescriberTests
{
    [Fact]
    public void Describes_connects_disconnects_and_network_changes()
    {
        var before = Create().WithWiFi("192.168.1.25/24", "192.168.1.1").WithEthernet().Build();
        var after = Create().WithWiFi("10.0.0.8/24", "10.0.0.1").WithEthernet().WithVpn().Build().Let(s => s with
        {
            Adapters = s.Adapters.Select(a => a.Index == Eth ? a with { IsUp = false } : a).ToList(),
        });

        var events = NetworkChangeDescriber.Describe(before, after);

        Assert.Contains(events, e => e.StartsWith("Wi-Fi changed: 192.168.1.25/24 (gateway 192.168.1.1) → 10.0.0.8/24 (gateway 10.0.0.1)"));
        Assert.Contains("Ethernet disconnected", events);
        Assert.Contains(events, e => e.StartsWith("VPN Tailscale (Tailscale) connected"));
        Assert.Empty(NetworkChangeDescriber.Describe(after, after));
    }
}

internal static class FunctionalExtensions
{
    public static TOut Let<TIn, TOut>(this TIn value, Func<TIn, TOut> f) => f(value);
}

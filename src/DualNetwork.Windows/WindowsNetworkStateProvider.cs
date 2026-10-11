using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using DualNetwork.Core.Control;
using DualNetwork.Core.Model;
using DualNetwork.Core.Net;
using DualNetwork.Windows.Interop;

namespace DualNetwork.Windows;

/// <summary>Reads adapters, metrics and the IPv4 routing table through native Windows APIs.</summary>
public sealed class WindowsNetworkStateProvider : INetworkStateProvider
{
    private readonly IDnsPolicyWriter _nrpt;
    private (DateTime At, IReadOnlyList<NrptRule>? Rules) _nrptCache;

    public WindowsNetworkStateProvider(IDnsPolicyWriter nrpt, InternetProbe probe)
    {
        _nrpt = nrpt;
        Probe = probe;
    }

    public InternetProbe Probe { get; }
    public TimeSpan NrptCacheFor { get; set; } = TimeSpan.FromSeconds(60);

    public void InvalidateCaches()
    {
        Probe.Invalidate();
        _nrptCache = default;
    }

    public async Task<NetworkState> CaptureAsync(CaptureOptions options, CancellationToken ct = default)
    {
        var ipInterfaces = IpHelper.GetIPv4Interfaces();
        var routes = IpHelper.GetIPv4Routes();
        HashSet<int> v6Defaults;
        try { v6Defaults = IpHelper.GetIPv6DefaultRouteInterfaces(); } catch { v6Defaults = new(); }

        var adapters = new List<AdapterInfo>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (!ni.Supports(NetworkInterfaceComponent.IPv4)) continue;
            IPInterfaceProperties props;
            IPv4InterfaceProperties? v4;
            try
            {
                props = ni.GetIPProperties();
                v4 = props.GetIPv4Properties();
            }
            catch (NetworkInformationException) { continue; }
            if (v4 is null) continue;

            var (kind, product) = AdapterClassifier.Classify(ni.Name, ni.Description, ni.NetworkInterfaceType.ToString());
            ipInterfaces.TryGetValue(v4.Index, out var ipIf);

            adapters.Add(new AdapterInfo
            {
                Index = v4.Index,
                Name = ni.Name,
                Description = ni.Description,
                Kind = kind,
                Product = product,
                IsUp = ni.OperationalStatus == OperationalStatus.Up,
                Addresses = props.UnicastAddresses
                    .Where(u => u.Address.AddressFamily == AddressFamily.InterNetwork)
                    .Select(u => new AdapterAddress(u.Address, u.PrefixLength))
                    .ToList(),
                Gateways = props.GatewayAddresses.Select(g => g.Address)
                    .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPv4.IsUnspecified(a))
                    .ToList(),
                DnsServers = props.DnsAddresses
                    .Where(a => a.AddressFamily == AddressFamily.InterNetwork || !a.IsIPv6SiteLocal)
                    .ToList(),
                DnsSuffix = string.IsNullOrWhiteSpace(props.DnsSuffix) ? null : props.DnsSuffix,
                DhcpEnabled = v4.IsDhcpEnabled,
                InterfaceMetric = ipIf?.Metric ?? 0,
                AutomaticMetric = ipIf?.AutomaticMetric ?? true,
                HasIPv6DefaultRoute = v6Defaults.Contains(v4.Index),
                MacAddress = FormatMac(ni.GetPhysicalAddress()),
                SpeedBitsPerSecond = ni.OperationalStatus == OperationalStatus.Up ? ni.Speed : 0,
            });
        }

        if (options.ProbeInternet)
        {
            var probes = adapters.Select(async a =>
            {
                if (!a.HasUsableIPv4 || a.Kind == AdapterKind.Loopback) return a;
                if (a.Gateway is null) return a with { Internet = InternetStatus.NotAvailable };
                var local = a.UsableAddresses.First().Address;
                var status = await Probe.ProbeAsync(local, $"{a.Index}|{local}|{a.Gateway}", ct);
                return a with { Internet = status };
            });
            adapters = (await Task.WhenAll(probes)).ToList();
        }

        IReadOnlyList<NrptRule>? nrpt = null;
        if (options.IncludeNrpt)
        {
            if (_nrptCache.Rules is not null && DateTime.UtcNow - _nrptCache.At < NrptCacheFor)
                nrpt = _nrptCache.Rules;
            else
            {
                try
                {
                    nrpt = await _nrpt.ListRulesAsync(ct);
                    _nrptCache = (DateTime.UtcNow, nrpt);
                }
                catch { nrpt = null; }
            }
        }

        return new NetworkState
        {
            CapturedAt = DateTimeOffset.Now,
            Adapters = adapters.OrderBy(a => a.Kind == AdapterKind.Loopback).ThenBy(a => !a.IsUp).ThenBy(a => a.InterfaceMetric).ToList(),
            Routes = routes,
            NrptRules = nrpt,
        };
    }

    private static string? FormatMac(PhysicalAddress mac)
    {
        var b = mac.GetAddressBytes();
        return b.Length == 0 ? null : string.Join("-", b.Select(x => x.ToString("X2")));
    }

    /// <summary>Which adapter Windows would actually use for a destination (GetBestRoute2).</summary>
    public static int? BestInterface(IPAddress destination) => IpHelper.BestRoute(destination)?.InterfaceIndex;
}

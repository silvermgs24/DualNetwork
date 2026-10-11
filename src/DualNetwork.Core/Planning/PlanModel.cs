using System.Net;
using System.Text.Json.Serialization;
using DualNetwork.Core.Model;
using DualNetwork.Core.Net;

namespace DualNetwork.Core.Planning;

public enum InternetPreference
{
    /// <summary>Wi-Fi carries Internet traffic (the normal case).</summary>
    WiFi,
    /// <summary>Only meaningful when the client network also has Internet: let the client network carry it.</summary>
    ClientNetwork,
}

public sealed class PlannerSettings
{
    /// <summary>Adapter name to use as the Internet adapter instead of auto-detection (e.g. USB tethering).</summary>
    public string? InternetAdapterName { get; set; }
    /// <summary>Adapter name to use as the client adapter instead of auto-detection.</summary>
    public string? ClientAdapterName { get; set; }
    public InternetPreference InternetPreference { get; set; } = InternetPreference.WiFi;
    /// <summary>Route the RFC 1918 block that contains the client address (e.g. 10.0.0.0/8) to the client gateway.</summary>
    public bool RouteContainingPrivateBlock { get; set; } = true;
    /// <summary>Route all three RFC 1918 blocks to the client gateway.</summary>
    public bool RouteAllPrivateBlocks { get; set; }
    /// <summary>Additional client networks reachable via the client gateway, e.g. "100.64.10.0/24".</summary>
    public List<string> ExtraClientPrefixes { get; set; } = new();
    /// <summary>Internal DNS namespaces to resolve via the client DNS, in addition to the DHCP-provided suffix.</summary>
    public List<string> ExtraInternalDomains { get; set; } = new();
    /// <summary>Create NRPT rules so internal domains always use the client DNS servers.</summary>
    public bool ManageDns { get; set; } = true;
    /// <summary>Route metric used for routes we add. Only matters for ties of equal prefix length.</summary>
    public int RouteMetric { get; set; } = 5;
}

public enum RoutePurpose { InternetOverride, ClientRange, ClientDnsHost, InternetDnsHost }

/// <summary>A route this application wants to exist (and will own if it creates it).</summary>
public sealed record RouteSpec(
    IPv4Prefix Destination,
    [property: JsonConverter(typeof(IPAddressJsonConverter))] IPAddress NextHop,
    int InterfaceIndex,
    string InterfaceName,
    int Metric,
    RoutePurpose Purpose,
    string Reason)
{
    [JsonIgnore] public RouteKey Key => new(Destination, NextHop.ToString(), InterfaceIndex);

    /// <summary>Equivalent PowerShell command, for the "exact changes" view and the log.</summary>
    public string ToPowerShell() =>
        $"New-NetRoute -DestinationPrefix {Destination} -InterfaceIndex {InterfaceIndex} -NextHop {NextHop} -RouteMetric {Metric} -PolicyStore ActiveStore";

    public string ToRouteExe() =>
        $"route ADD {Destination.NetworkAddress} MASK {Destination.MaskAddress} {NextHop} METRIC {Metric} IF {InterfaceIndex}";

    public override string ToString() => $"{Destination} → {InterfaceName} via {NextHop}";
}

/// <summary>An NRPT rule this application wants to exist.</summary>
public sealed record NrptRuleSpec(string Namespace, IReadOnlyList<string> NameServers, string Reason)
{
    /// <summary>The Windows rule name (GUID) once created. Not part of the identity.</summary>
    public string? RuleName { get; init; }

    [JsonIgnore] public string Key => $"{Namespace.ToLowerInvariant()}|{string.Join(",", NameServers)}";

    public string ToPowerShell() =>
        $"Add-DnsClientNrptRule -Namespace \"{Namespace}\" -NameServers {string.Join(",", NameServers.Select(s => $"\"{s}\""))} -Comment \"{NrptTag.Comment}\"";

    public override string ToString() => $"{Namespace} → {string.Join(", ", NameServers)}";
}

public enum PlanStatus
{
    /// <summary>Changes are needed and safe to apply.</summary>
    Ready,
    /// <summary>Configuration is already correct; nothing to change right now.</summary>
    NothingToDo,
    /// <summary>The engine will not change anything automatically; see RefusalReasons.</summary>
    Refused,
}

public enum Severity { Info, Good, Warning, Problem }

public sealed record PlanNote(Severity Severity, string Text)
{
    public override string ToString() => Text;
}

/// <summary>"Destination class → adapter" line for the routing status panel.</summary>
public sealed record RoutingExpectation(string Label, string AdapterName, Severity Severity);

public sealed class RoutingPlan
{
    public PlanStatus Status { get; init; }
    public AdapterInfo? InternetAdapter { get; init; }
    public AdapterInfo? ClientAdapter { get; init; }
    /// <summary>The adapter that will carry public Internet traffic after the plan is applied.</summary>
    public AdapterInfo? EffectiveInternetAdapter { get; init; }
    public bool BothHaveInternet { get; init; }
    public IReadOnlyList<IPv4Prefix> ClientNetworks { get; init; } = Array.Empty<IPv4Prefix>();
    public IReadOnlyList<RouteSpec> Routes { get; init; } = Array.Empty<RouteSpec>();
    public IReadOnlyList<NrptRuleSpec> NrptRules { get; init; } = Array.Empty<NrptRuleSpec>();
    /// <summary>Existing client routes/settings that are deliberately left untouched.</summary>
    public IReadOnlyList<string> Preserved { get; init; } = Array.Empty<string>();
    public IReadOnlyList<PlanNote> Warnings { get; init; } = Array.Empty<PlanNote>();
    public IReadOnlyList<string> RefusalReasons { get; init; } = Array.Empty<string>();
    /// <summary>Plain-language summary shown in the confirmation dialog.</summary>
    public IReadOnlyList<string> Summary { get; init; } = Array.Empty<string>();
    public IReadOnlyList<RoutingExpectation> Expectations { get; init; } = Array.Empty<RoutingExpectation>();
    /// <summary>Step-by-step reasoning, written to the log.</summary>
    public IReadOnlyList<string> Trace { get; init; } = Array.Empty<string>();

    [JsonIgnore] public bool HasChanges => Routes.Count > 0 || NrptRules.Count > 0;

    /// <summary>Stable fingerprint of the actions, used to detect that the network changed between preview and apply.</summary>
    [JsonIgnore] public string Fingerprint =>
        string.Join(";", Routes.Select(r => r.Key.ToString()).OrderBy(s => s, StringComparer.Ordinal)) + "#" +
        string.Join(";", NrptRules.Select(r => r.Key).OrderBy(s => s, StringComparer.Ordinal)) + "#" + Status;
}

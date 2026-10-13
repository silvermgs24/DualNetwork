using System.Text;
using System.Text.Json;
using DualNetwork.Core.Control;
using DualNetwork.Core.Model;
using DualNetwork.Core.Planning;

namespace DualNetwork.Windows;

/// <summary>
/// One readable text file with everything needed to understand a site: adapters, the full IPv4 route table,
/// NRPT rules, the plan and its reasoning, what is applied, the last network test and the recent log.
/// </summary>
public static class SupportReport
{
    public static string Write(DualNetworkHost host, IReadOnlyList<DiagnosticResult>? lastTest, string? folder = null,
        IReadOnlyList<DualNetwork.Core.Routing.PersistentRoute>? permanent = null)
    {
        var c = host.Controller;
        var state = c.LastState;
        var plan = c.LastPlan;
        var sb = new StringBuilder();
        sb.AppendLine($"Dual Network support report — {DateTime.Now:yyyy-MM-dd HH:mm:ss} — version {typeof(SupportReport).Assembly.GetName().Version}");
        sb.AppendLine($"Elevated: {host.IsElevated}   Mode on: {c.IsEnabled}   Automatic: {host.Settings.AutoManage}   Demo: {host.IsDemo}");
        sb.AppendLine();

        if (state is not null)
        {
            sb.AppendLine("=== ADAPTERS");
            foreach (var a in state.Adapters.Where(a => a.Kind != AdapterKind.Loopback))
            {
                sb.AppendLine($"#{a.Index} {a.Name} — {a.Kind}{(a.Product is null ? "" : $" ({a.Product})")} — {(a.IsUp ? "up" : "down")} — {a.Description}");
                sb.AppendLine($"    IP {string.Join(", ", a.Addresses)}  gateway {string.Join(", ", a.Gateways)}  metric {a.InterfaceMetric}{(a.AutomaticMetric ? " auto" : "")}  {(a.DhcpEnabled ? "DHCP" : "static")}");
                sb.AppendLine($"    DNS {string.Join(", ", a.DnsServers)}  suffix {a.DnsSuffix ?? "-"}  Internet {a.Internet}{(a.HasIPv6DefaultRoute ? "  IPv6 default route" : "")}");
            }
            sb.AppendLine();
            sb.AppendLine("=== IPV4 ROUTES (destination, next hop, interface, route metric, interface metric, protocol)");
            var owned = c.Journal.Routes.Select(r => r.Key).ToHashSet();
            foreach (var r in state.Routes.OrderBy(r => r.InterfaceIndex).ThenBy(r => r.Destination))
                sb.AppendLine($"{(owned.Contains(r.Key) ? "*" : " ")} {r.Destination,-20} {(r.IsOnLink ? "on-link" : r.NextHop.ToString()),-16} {state.Adapter(r.InterfaceIndex)?.Name ?? r.InterfaceIndex.ToString(),-28} {r.RouteMetric,6} {state.InterfaceMetric(r.InterfaceIndex),6}  {r.Protocol}");
            sb.AppendLine("  (* = added by Dual Network)");
            sb.AppendLine();
            sb.AppendLine("=== NRPT RULES");
            if (state.NrptRules is null) sb.AppendLine("(not read)");
            else foreach (var n in state.NrptRules) sb.AppendLine($"{n.Namespace} → {string.Join(", ", n.NameServers)}  [{n.Comment}]");
            sb.AppendLine();
        }

        if (plan is not null)
        {
            sb.AppendLine($"=== PLAN: {plan.Status}  (Internet adapter: {plan.InternetAdapter?.Name ?? "-"}, client adapter: {plan.ClientAdapter?.Name ?? "-"}, both have Internet: {plan.BothHaveInternet})");
            foreach (var r in plan.RefusalReasons) sb.AppendLine("REFUSED: " + r);
            foreach (var w in plan.Warnings) sb.AppendLine($"{w.Severity}: {w.Text}");
            foreach (var r in plan.Routes) sb.AppendLine($"ROUTE {r.Destination} via {r.NextHop} on {r.InterfaceName} — {r.Purpose}");
            foreach (var n in plan.NrptRules) sb.AppendLine($"NRPT {n}");
            foreach (var p in plan.Preserved) sb.AppendLine("preserved: " + p);
            sb.AppendLine("--- decision trace");
            foreach (var t in plan.Trace) sb.AppendLine("  " + t);
            sb.AppendLine();
        }

        if (permanent is not null)
        {
            sb.AppendLine("=== PERMANENT ROUTES (route -p / PersistentStore)");
            if (permanent.Count == 0) sb.AppendLine("(none)");
            foreach (var p in permanent) sb.AppendLine($"{p}  metric {p.Metric}  {p.Source}{(p.InterfaceAlias is null ? "" : $"  {p.InterfaceAlias}")}");
            foreach (var r in host.PersistentRoutes.LoadRemoved()) sb.AppendLine($"removed {r.RemovedAt:yyyy-MM-dd HH:mm}: {r.Route}");
            sb.AppendLine();
        }

        sb.AppendLine("=== APPLIED (journal)");
        sb.AppendLine(JsonSerializer.Serialize(c.Journal, JsonDefaults.Options));
        sb.AppendLine();
        sb.AppendLine("=== SETTINGS");
        sb.AppendLine(JsonSerializer.Serialize(host.Settings, JsonDefaults.Options));
        sb.AppendLine();

        if (lastTest is { Count: > 0 })
        {
            sb.AppendLine("=== LAST NETWORK TEST");
            foreach (var t in lastTest) sb.AppendLine($"[{t.Status}] {t.Name}: {t.Detail}");
            sb.AppendLine();
        }

        sb.AppendLine("=== LOG (most recent 400 lines)");
        foreach (var e in host.Log.Recent.TakeLast(400)) sb.AppendLine(e.ToString());

        folder ??= Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"DualNetwork-report-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        File.WriteAllText(path, sb.ToString());
        return path;
    }
}

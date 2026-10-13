using System.Text;
using System.Text.Json;
using DualNetwork.Core.Control;
using DualNetwork.Core.Model;
using DualNetwork.Core.Planning;
using DualNetwork.Windows;

Console.OutputEncoding = Encoding.UTF8;

var command = args.FirstOrDefault()?.ToLowerInvariant() ?? "help";
var flags = args.Skip(1).Select(a => a.ToLowerInvariant()).ToHashSet();
using var host = new DualNetworkHost();
host.Log.Written += (_, e) =>
{
    if (e.Level == DualNetwork.Core.Control.LogLevel.Detail && !flags.Contains("--verbose")) return;
    Console.WriteLine(e);
};

switch (command)
{
    case "status": await Status(); break;
    case "routes": await Routes(); break;
    case "plan": await PlanCmd(); break;
    case "test": await Test(); break;
    case "enable": await Enable(); break;
    case "disable":
    case "restore": await Restore(); break;
    case "run": await Run(); break;
    case "snapshot": await Snapshot(); break;
    case "startup": Startup(); break;
    case "report": await Report(); break;
    default: Help(); break;
}
return Environment.ExitCode;

void Help() => Console.WriteLine("""
    dualnet — Dual Network command line

      status            Adapters, roles and where traffic goes right now
      routes            IPv4 routing table (as read through the IP Helper API)
      plan [--json]     What Dual Network Mode would change, and why
      test              Run the network test (diagnostics)
      enable --yes      Apply the plan once (requires Administrator). Without monitoring; prefer 'run'.
      run               Enable and keep monitoring until Ctrl+C, then restore (requires Administrator)
      disable|restore   Remove every change Dual Network made (also cleans up after a crash)
      snapshot          Print the current configuration as JSON
      report            Save a support report (adapters, routes, DNS rules, plan, test, log) to the Desktop
      startup on|off    Start the Dual Network app (elevated, in the tray) at sign-in; no argument shows status

      --verbose         Include detailed decision trace in the output
    """);

async Task Status()
{
    var plan = await host.Controller.PreviewAsync();
    var state = host.Controller.LastState!;
    Console.WriteLine($"Administrator: {(host.IsElevated ? "yes" : "no")}    Dual Network Mode: {(host.Controller.HasLeftovers ? "changes left from a previous session" : host.Controller.Journal.HasChanges ? "active" : "off")}");
    Console.WriteLine();
    foreach (var a in state.Adapters.Where(a => a.Kind != AdapterKind.Loopback))
    {
        var role = a.Index == plan.InternetAdapter?.Index ? "  [Internet]" : a.Index == plan.ClientAdapter?.Index ? "  [Client network]" : "";
        Console.WriteLine($"{a.Name} (#{a.Index}) — {a.Kind}{(a.Product is null ? "" : $" / {a.Product}")}{role}");
        Console.WriteLine($"   {(a.IsUp ? "Connected" : "Disconnected")} · {a.Description}");
        if (a.Addresses.Count > 0) Console.WriteLine($"   IP {string.Join(", ", a.Addresses)} · gateway {a.Gateway?.ToString() ?? "none"} · metric {a.InterfaceMetric}{(a.AutomaticMetric ? " (auto)" : "")}");
        if (a.DnsServers.Count > 0) Console.WriteLine($"   DNS {string.Join(", ", a.DnsServers)}{(a.DnsSuffix is null ? "" : $" · suffix {a.DnsSuffix}")}");
        if (a.IsUp && a.HasUsableIPv4) Console.WriteLine($"   Internet: {a.Internet}{(a.HasIPv6DefaultRoute ? " · IPv6 default route" : "")}");
    }
    Console.WriteLine();
    var best = WindowsNetworkStateProvider.BestInterface(System.Net.IPAddress.Parse("8.8.8.8"));
    Console.WriteLine($"Internet traffic currently → {state.Adapter(best ?? -1)?.Name ?? "no route"}");
    PrintPlan(plan);
}

async Task Routes()
{
    var state = await host.Provider.CaptureAsync(new CaptureOptions(ProbeInternet: false, IncludeNrpt: false));
    Console.WriteLine($"{"Destination",-20} {"Next hop",-16} {"If",4} {"Interface",-22} {"Metric",7} {"IfMetric",8}  Protocol");
    foreach (var r in state.Routes.OrderBy(r => r.InterfaceIndex).ThenBy(r => r.Destination))
        Console.WriteLine($"{r.Destination,-20} {(r.IsOnLink ? "on-link" : r.NextHop.ToString()),-16} {r.InterfaceIndex,4} {Trunc(state.Adapter(r.InterfaceIndex)?.Name ?? "?", 22),-22} {r.RouteMetric,7} {state.InterfaceMetric(r.InterfaceIndex),8}  {r.Protocol}");
}

static string Trunc(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";

async Task PlanCmd()
{
    var plan = await host.Controller.PreviewAsync();
    if (flags.Contains("--json")) { Console.WriteLine(JsonSerializer.Serialize(plan, JsonDefaults.Options)); return; }
    PrintPlan(plan);
    Console.WriteLine();
    Console.WriteLine("Decision trace:");
    foreach (var t in plan.Trace) Console.WriteLine("  · " + t);
}

void PrintPlan(RoutingPlan plan)
{
    Console.WriteLine();
    Console.WriteLine($"Plan: {plan.Status}");
    foreach (var r in plan.RefusalReasons) Console.WriteLine($"  ✗ {r}");
    foreach (var s in plan.Summary) Console.WriteLine($"  ✓ {s}");
    foreach (var w in plan.Warnings) Console.WriteLine($"  ! {w.Text}");
    if (plan.Preserved.Count > 0)
    {
        Console.WriteLine("  Preserved:");
        foreach (var p in plan.Preserved) Console.WriteLine($"    = {p}");
    }
    if (plan.HasChanges)
    {
        Console.WriteLine("  Exact changes (non-persistent):");
        foreach (var r in plan.Routes) { Console.WriteLine($"    + {r.ToPowerShell()}"); Console.WriteLine($"      why: {r.Reason}"); }
        foreach (var n in plan.NrptRules) { Console.WriteLine($"    + {n.ToPowerShell()}"); Console.WriteLine($"      why: {n.Reason}"); }
    }
}

async Task Test()
{
    var plan = await host.Controller.PreviewAsync();
    var results = await host.Diagnostics.RunAsync(host.Controller.LastState!, plan, host.Settings.Planner);
    foreach (var r in results)
    {
        var mark = r.Status switch { Severity.Good => "✓", Severity.Warning => "!", Severity.Problem => "✗", _ => "·" };
        Console.WriteLine($"{mark} {r.Name}: {r.Detail}");
    }
}

async Task<bool> RequireAdmin()
{
    if (host.IsElevated) return true;
    Console.Error.WriteLine("This command changes routes and must be run from an elevated (Administrator) prompt.");
    Environment.ExitCode = 2;
    await Task.CompletedTask;
    return false;
}

async Task Enable()
{
    if (!await RequireAdmin()) return;
    var plan = await host.Controller.PreviewAsync();
    PrintPlan(plan);
    if (!flags.Contains("--yes")) { Console.WriteLine("\nRe-run with --yes to apply."); return; }
    var result = await host.Controller.EnableAsync(plan);
    Console.WriteLine(result.Message);
    if (!result.Success) Environment.ExitCode = 1;
}

async Task Restore()
{
    if (!host.Controller.Journal.HasChanges && !host.Controller.HasLeftovers)
    {
        Console.WriteLine("Nothing to restore: Dual Network has no active changes.");
        if (host.IsElevated) await host.Controller.DisableAsync(); // also removes tagged NRPT leftovers
        return;
    }
    if (!await RequireAdmin()) return;
    var result = await host.Controller.DisableAsync();
    Console.WriteLine(result.Message);
    if (!result.Success) Environment.ExitCode = 1;
}

async Task Run()
{
    if (!await RequireAdmin()) return;
    if (host.Controller.HasLeftovers) await host.Controller.DisableAsync();
    var plan = await host.Controller.PreviewAsync();
    PrintPlan(plan);
    var result = await host.Controller.EnableAsync(plan);
    Console.WriteLine(result.Message);
    if (!result.Success) { Environment.ExitCode = 1; return; }

    var done = new TaskCompletionSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; done.TrySetResult(); };
    host.Monitor.Changed += async (_, reason) =>
    {
        try { await host.Controller.ReconcileAsync(reason); }
        catch (Exception ex) { Console.Error.WriteLine(ex.Message); }
    };
    host.Monitor.Start();
    Console.WriteLine("Monitoring. Press Ctrl+C to disable and restore.");
    await done.Task;
    host.Monitor.Stop();
    await host.Controller.DisableAsync();
}

async Task Snapshot()
{
    var state = await host.Provider.CaptureAsync(new CaptureOptions());
    Console.WriteLine(JsonSerializer.Serialize(new ConfigurationSnapshot { State = state, Reason = "Manual" }, JsonDefaults.Options));
}

void Startup()
{
    var arg = args.Skip(1).FirstOrDefault()?.ToLowerInvariant();
    if (arg == "on")
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "DualNetwork.exe");
        if (args.Length > 2) exe = Path.GetFullPath(args[2]);
        if (!File.Exists(exe)) { Console.Error.WriteLine($"App not found: {exe} (pass its path as the next argument)"); Environment.ExitCode = 1; return; }
        StartupTask.Register(exe);
    }
    else if (arg == "off") StartupTask.Unregister();
    else if (arg == "selftest") { Console.WriteLine(StartupTaskSelfTest.Run(Path.GetFullPath(args[2]))); return; }
    var path = StartupTask.RegisteredPath();
    Console.WriteLine(path is null ? "Start with Windows: off" : $"Start with Windows: on → {path} {StartupTask.BackgroundArgument}");
}

async Task Report()
{
    var plan = await host.Controller.PreviewAsync();
    var results = await host.Diagnostics.RunAsync(host.Controller.LastState!, plan, host.Settings.Planner);
    var path = SupportReport.Write(host, results, args.Length > 1 ? args[1] : null);
    Console.WriteLine($"Report saved: {path}");
}

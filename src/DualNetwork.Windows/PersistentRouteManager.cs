using System.Diagnostics;
using System.Net;
using System.Text.Json;
using DualNetwork.Core.Control;
using DualNetwork.Core.Net;
using DualNetwork.Core.Routing;
using Microsoft.Win32;

namespace DualNetwork.Windows;

/// <summary>A permanent route the user removed through Dual Network, kept so it can be restored.</summary>
public sealed record RemovedPersistentRoute(PersistentRoute Route, DateTimeOffset RemovedAt);

/// <summary>
/// Visibility and control over routes saved permanently in Windows ("route -p", PersistentStore).
/// Reading needs no rights; removing and restoring need Administrator. Every change is verified by reading back.
/// </summary>
public interface IPersistentRouteStore
{
    bool CanModify { get; }
    Task<IReadOnlyList<PersistentRoute>> ListAsync(CancellationToken ct = default);
    Task RemoveAsync(PersistentRoute route, CancellationToken ct = default);
    Task RestoreAsync(PersistentRoute route, CancellationToken ct = default);
    IReadOnlyList<RemovedPersistentRoute> LoadRemoved();
    void ForgetRemoved(PersistentRoute route);
}

public sealed class PersistentRouteManager : IPersistentRouteStore
{
    public bool CanModify => Privileges.IsElevated;

    private const string LegacyKey = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\PersistentRoutes";
    private static string RemovedFile => Path.Combine(AppPaths.MachineData, "removed-permanent-routes.json");

    public async Task<IReadOnlyList<PersistentRoute>> ListAsync(CancellationToken ct = default)
    {
        var legacy = new List<PersistentRoute>();
        using (var key = Registry.LocalMachine.OpenSubKey(LegacyKey))
            if (key is not null)
                foreach (var name in key.GetValueNames())
                    if (PersistentRoute.TryParseLegacy(name) is { } r) legacy.Add(r);

        var store = new List<PersistentRoute>();
        var res = await PowerShellRunner.RunAsync(
            "$r = @(Get-NetRoute -AddressFamily IPv4 -PolicyStore PersistentStore -ErrorAction SilentlyContinue | ForEach-Object { " +
            "[pscustomobject]@{ Prefix = [string]$_.DestinationPrefix; NextHop = [string]$_.NextHop; Metric = [int]$_.RouteMetric; " +
            "Index = [int]$_.InterfaceIndex; Alias = [string]$_.InterfaceAlias } }); ConvertTo-Json -InputObject $r -Compress",
            TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
        if (res.Ok && !string.IsNullOrWhiteSpace(res.StdOut))
        {
            using var doc = JsonDocument.Parse(res.StdOut);
            var items = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().ToList() : new() { doc.RootElement };
            foreach (var e in items)
            {
                if (!IPv4Prefix.TryParse(e.GetProperty("Prefix").GetString(), out var p)) continue;
                if (!IPAddress.TryParse(e.GetProperty("NextHop").GetString(), out var hop) || !IPv4.IsV4(hop)) continue;
                store.Add(new PersistentRoute(p, hop, e.GetProperty("Metric").GetInt32(), PersistentRouteSource.PersistentStore,
                    e.GetProperty("Index").GetInt32(), e.GetProperty("Alias").GetString()));
            }
        }
        return PersistentRoute.Merge(legacy, store);
    }

    /// <summary>Removes the route from both permanent stores and from the active table, then verifies it is gone.</summary>
    public async Task RemoveAsync(PersistentRoute route, CancellationToken ct = default)
    {
        RequireAdmin();
        var dest = route.Destination;
        // route.exe removes the active route and the "route -p" entry.
        RunRouteExe($"delete {dest.NetworkAddress} mask {dest.MaskAddress} {route.NextHop}");
        // Remove-NetRoute covers the modern persistent store (and anything left in the active store).
        await PowerShellRunner.RunAsync(
            $"Get-NetRoute -AddressFamily IPv4 -DestinationPrefix '{dest}' -ErrorAction SilentlyContinue | " +
            $"Where-Object {{ $_.NextHop -eq '{route.NextHop}' }} | Remove-NetRoute -Confirm:$false -ErrorAction SilentlyContinue; " +
            $"Get-NetRoute -AddressFamily IPv4 -DestinationPrefix '{dest}' -PolicyStore PersistentStore -ErrorAction SilentlyContinue | " +
            $"Where-Object {{ $_.NextHop -eq '{route.NextHop}' }} | Remove-NetRoute -Confirm:$false -ErrorAction SilentlyContinue",
            TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);

        if ((await ListAsync(ct).ConfigureAwait(false)).Any(r => r.Key == route.Key))
            throw new InvalidOperationException($"Windows still lists the permanent route {route} after removing it.");

        var removed = LoadRemoved().Where(r => r.Route.Key != route.Key).ToList();
        removed.Insert(0, new RemovedPersistentRoute(route, DateTimeOffset.Now));
        SaveRemoved(removed);
    }

    /// <summary>Recreates a previously removed route exactly as "route -p add" would, then verifies it.</summary>
    public async Task RestoreAsync(PersistentRoute route, CancellationToken ct = default)
    {
        RequireAdmin();
        var dest = route.Destination;
        RunRouteExe($"-p add {dest.NetworkAddress} mask {dest.MaskAddress} {route.NextHop} metric {Math.Clamp(route.Metric, 1, 9999)}");
        if (!(await ListAsync(ct).ConfigureAwait(false)).Any(r => r.Key == route.Key))
            throw new InvalidOperationException($"Windows did not save the permanent route {route}.");
        SaveRemoved(LoadRemoved().Where(r => r.Route.Key != route.Key).ToList());
    }

    public IReadOnlyList<RemovedPersistentRoute> LoadRemoved()
    {
        try
        {
            return File.Exists(RemovedFile)
                ? JsonSerializer.Deserialize<List<RemovedPersistentRoute>>(File.ReadAllText(RemovedFile), JsonDefaults.Options) ?? new()
                : new List<RemovedPersistentRoute>();
        }
        catch { return new List<RemovedPersistentRoute>(); }
    }

    public void ForgetRemoved(PersistentRoute route) => SaveRemoved(LoadRemoved().Where(r => r.Route.Key != route.Key).ToList());

    private static void SaveRemoved(IReadOnlyList<RemovedPersistentRoute> list) =>
        AppPaths.WriteAtomic(RemovedFile, JsonSerializer.Serialize(list, JsonDefaults.Options));

    private static void RequireAdmin()
    {
        if (!Privileges.IsElevated) throw new UnauthorizedAccessException("Administrator rights are required to change permanent routes.");
    }

    /// <summary>Arguments are built only from parsed IP addresses and numbers, never from free text.</summary>
    private static void RunRouteExe(string args)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "route.exe"), args)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit(15000);
    }
}

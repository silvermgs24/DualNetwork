using System.Net;
using DualNetwork.Core.Net;
using DualNetwork.Core.Routing;

namespace DualNetwork.Windows;

/// <summary>Demo: the laptop still carries a "route -p" from an earlier client visit, like the real one did.</summary>
public sealed class SimulatedPersistentRoutes : IPersistentRouteStore
{
    private readonly SimulatedNetwork _network;
    private readonly List<PersistentRoute> _routes = new()
    {
        new(IPv4Prefix.Parse("10.0.0.0/16"), IPAddress.Parse("10.20.30.254"), 1, PersistentRouteSource.RouteExe),
        new(IPv4Prefix.Parse("192.168.50.0/24"), IPAddress.Parse("172.16.8.1"), 1, PersistentRouteSource.RouteExe),
    };
    private readonly List<RemovedPersistentRoute> _removed = new();

    public SimulatedPersistentRoutes(SimulatedNetwork network)
    {
        _network = network;
        foreach (var r in _routes) _network.AddActiveIfReachable(r);
    }

    public bool CanModify => true;

    public Task<IReadOnlyList<PersistentRoute>> ListAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<PersistentRoute>>(_routes.OrderBy(r => r.Destination).ToList());

    public Task RemoveAsync(PersistentRoute route, CancellationToken ct = default)
    {
        _routes.RemoveAll(r => r.Key == route.Key);
        _network.RemoveActive(route);
        _removed.RemoveAll(r => r.Route.Key == route.Key);
        _removed.Insert(0, new RemovedPersistentRoute(route, DateTimeOffset.Now));
        return Task.CompletedTask;
    }

    public Task RestoreAsync(PersistentRoute route, CancellationToken ct = default)
    {
        if (_routes.All(r => r.Key != route.Key)) _routes.Add(route);
        _network.AddActiveIfReachable(route);
        _removed.RemoveAll(r => r.Route.Key == route.Key);
        return Task.CompletedTask;
    }

    public IReadOnlyList<RemovedPersistentRoute> LoadRemoved() => _removed.ToList();

    public void ForgetRemoved(PersistentRoute route) => _removed.RemoveAll(r => r.Route.Key == route.Key);
}

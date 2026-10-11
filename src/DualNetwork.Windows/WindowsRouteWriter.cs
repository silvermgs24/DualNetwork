using DualNetwork.Core.Control;
using DualNetwork.Core.Model;
using DualNetwork.Core.Net;
using DualNetwork.Core.Planning;
using DualNetwork.Windows.Interop;

namespace DualNetwork.Windows;

/// <summary>
/// Adds and removes single IPv4 routes via CreateIpForwardEntry2 / DeleteIpForwardEntry2.
/// Routes are created in the active store only (not persistent: a reboot removes them).
/// Every request is validated before it reaches Windows, and verified afterwards.
/// </summary>
public sealed class WindowsRouteWriter : IRouteWriter
{
    public bool CanModify => Privileges.IsElevated;

    public WriteOutcome AddRoute(RouteSpec spec, NetworkState current)
    {
        Validate(spec, current);
        var rc = IpHelper.CreateRoute(spec.Destination, spec.NextHop, spec.InterfaceIndex, spec.Metric);
        switch (rc)
        {
            case IpHelper.NO_ERROR:
                if (!IpHelper.GetIPv4Routes().Any(r => r.Key == spec.Key))
                    throw new InvalidOperationException($"Windows accepted route {spec} but it is not in the routing table");
                return WriteOutcome.Created;
            case IpHelper.ERROR_OBJECT_ALREADY_EXISTS:
                return WriteOutcome.AlreadyExists;
            case IpHelper.ERROR_ACCESS_DENIED:
                throw new UnauthorizedAccessException("Administrator rights are required to add routes");
            default:
                throw new Win32Error("CreateIpForwardEntry2", rc);
        }
    }

    public WriteOutcome RemoveRoute(RouteSpec spec)
    {
        if (spec.Destination.IsDefault) throw new InvalidOperationException("Refusing to remove a default route");
        var rc = IpHelper.DeleteRoute(spec.Destination, spec.NextHop, spec.InterfaceIndex);
        return rc switch
        {
            IpHelper.NO_ERROR => WriteOutcome.Removed,
            IpHelper.ERROR_NOT_FOUND => WriteOutcome.NotFound,
            IpHelper.ERROR_ACCESS_DENIED => throw new UnauthorizedAccessException("Administrator rights are required to remove routes"),
            _ => throw new Win32Error("DeleteIpForwardEntry2", rc),
        };
    }

    /// <summary>Hard safety checks, independent of the planner.</summary>
    internal static void Validate(RouteSpec spec, NetworkState current)
    {
        if (spec.Destination.Length < 1)
            throw new InvalidOperationException($"Refusing to add {spec.Destination}: default routes are never created by Dual Network");
        if (spec.Metric is < 1 or > 9999)
            throw new InvalidOperationException($"Refusing route {spec}: metric {spec.Metric} out of range");
        var adapter = current.Adapter(spec.InterfaceIndex)
            ?? throw new InvalidOperationException($"Refusing route {spec}: interface {spec.InterfaceIndex} not found");
        if (!adapter.IsUp) throw new InvalidOperationException($"Refusing route {spec}: {adapter.Name} is not connected");
        if (adapter.Kind is AdapterKind.Vpn or AdapterKind.Loopback)
            throw new InvalidOperationException($"Refusing route {spec}: {adapter.Name} is a {adapter.Kind} adapter");
        if (!IPv4.IsV4(spec.NextHop) || IPv4.IsUnspecified(spec.NextHop))
            throw new InvalidOperationException($"Refusing route {spec}: invalid next hop");
        if (!adapter.IsOnLink(spec.NextHop))
            throw new InvalidOperationException($"Refusing route {spec}: next hop {spec.NextHop} is not on {adapter.Name}'s network");
    }
}

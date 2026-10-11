using System.Net.NetworkInformation;
using DualNetwork.Windows.Interop;

namespace DualNetwork.Windows;

/// <summary>
/// Raises <see cref="Changed"/> (debounced) when addresses, interfaces or IPv4 routes change, plus a periodic tick
/// as a safety net. Events arrive on a thread-pool thread.
/// </summary>
public sealed class NetworkMonitor : IDisposable
{
    private readonly object _lock = new();
    private readonly HashSet<string> _pending = new();
    private readonly Timer _debounce;
    private readonly Timer _poll;
    private readonly IpHelper.MibChangeCallback _routeCallback;
    private readonly IpHelper.MibChangeCallback _interfaceCallback;
    private readonly IpHelper.MibChangeCallback _addressCallback;
    private IntPtr _routeHandle, _interfaceHandle, _addressHandle;
    private bool _started;

    public NetworkMonitor()
    {
        _debounce = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
        _poll = new Timer(_ => Signal("Periodic check"), null, Timeout.Infinite, Timeout.Infinite);
        // Keep delegates in fields so the GC never collects them while native code holds them.
        _routeCallback = (_, _, _) => Signal("Route table changed");
        _interfaceCallback = (_, _, _) => Signal("Network interface changed");
        _addressCallback = (_, _, _) => Signal("IP address changed");
    }

    public TimeSpan Debounce { get; set; } = TimeSpan.FromSeconds(2);
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Reason(s) for the change, e.g. "Route table changed, IP address changed".</summary>
    public event EventHandler<string>? Changed;

    public void Start()
    {
        if (_started) return;
        _started = true;
        NetworkChange.NetworkAvailabilityChanged += OnAvailability;
        IpHelper.NotifyRouteChange2(IpHelper.AF_INET, _routeCallback, IntPtr.Zero, false, out _routeHandle);
        IpHelper.NotifyIpInterfaceChange(IpHelper.AF_INET, _interfaceCallback, IntPtr.Zero, false, out _interfaceHandle);
        IpHelper.NotifyUnicastIpAddressChange(IpHelper.AF_INET, _addressCallback, IntPtr.Zero, false, out _addressHandle);
        _poll.Change(PollInterval, PollInterval);
    }

    public void Stop()
    {
        if (!_started) return;
        _started = false;
        NetworkChange.NetworkAvailabilityChanged -= OnAvailability;
        Cancel(ref _routeHandle);
        Cancel(ref _interfaceHandle);
        Cancel(ref _addressHandle);
        _poll.Change(Timeout.Infinite, Timeout.Infinite);
        _debounce.Change(Timeout.Infinite, Timeout.Infinite);
    }

    private static void Cancel(ref IntPtr handle)
    {
        if (handle != IntPtr.Zero) IpHelper.CancelMibChangeNotify2(handle);
        handle = IntPtr.Zero;
    }

    private void OnAvailability(object? sender, NetworkAvailabilityEventArgs e) =>
        Signal(e.IsAvailable ? "Network became available" : "Network became unavailable");

    private void Signal(string reason)
    {
        lock (_lock)
        {
            _pending.Add(reason);
            _debounce.Change(Debounce, Timeout.InfiniteTimeSpan);
        }
    }

    private void Flush()
    {
        string reasons;
        lock (_lock)
        {
            if (_pending.Count == 0) return;
            // A periodic tick on its own is not worth naming next to real events.
            if (_pending.Count > 1) _pending.Remove("Periodic check");
            reasons = string.Join(", ", _pending);
            _pending.Clear();
        }
        Changed?.Invoke(this, reasons);
    }

    public void Dispose()
    {
        Stop();
        _debounce.Dispose();
        _poll.Dispose();
    }
}
